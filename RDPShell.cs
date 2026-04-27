// RDPShell.cs
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32; // Used for SessionSwitch events and Registry
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text;
using System.Linq;
using System.Security.Principal; // Required for SID operations
using System.Security; // Required for SecurityException
using System.Collections.Generic; // Required for List<T>

public class RDPShell
{
    // --- STATE MANAGEMENT ---
    // Global state for debug mode.
    private static bool IsDebugMode = false;
    
    // Store the process launched in RDP mode so the event handler can access it
    private static Process? RDPSubShellProcess;
    // Used to manage the asynchronous cleanup loop
    private static CancellationTokenSource? CleanupCts;

    // --- NATIVE IMPORTS (P/Invoke) ---

    // User32 imports for Keyboard state and window manipulation
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Synchronous key state check (used for startup detection)
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);

    // User32 imports for window enumeration
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // P/Invoke for getting the window title
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    // P/Invoke for native Windows MessageBox API (replaces System.Windows.Forms.MessageBox)
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);
    
    // P/Invoke for forced logoff
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    // Delegate for the EnumWindows callback
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // --- NATIVE IMPORTS for Console Allocation ---
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
    
    // --- CONSTANTS AND CONFIGURATION ---
    private const int VK_LWIN = 0x5B; // Left Windows Key
    private const int VK_RWIN = 0x5C; // Right Windows Key
    private const uint WM_CLOSE = 0x0010;

    // Constants for the persistent key check
    private const int INITIAL_DELAY_MS = 500; // Single delay before polling
    private const short KEY_DOWN_BIT = unchecked((short)0x8000);
    private const short KEY_PRESSED_BIT = 0x0001;
    private const short KEY_CHECK_MASK = KEY_DOWN_BIT | KEY_PRESSED_BIT;
    
    // ExitWindowsEx flags
    private const uint EWX_LOGOFF = 0x00000000;
    private const uint EWX_FORCE = 0x00000004;

    // MessageBox constants
    private const uint MB_OK = 0x00000000;
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_ICONINFORMATION = 0x00000040;
    
    // App constants
    private const string AppName = "RDPShell";
    private const string ShellFlag = "-shell";
    private const string DebugFlag = "-debug";
    // FLAG: Used only to trigger the UAC prompt
    private const string AdminCheckFlag = "-admincheck";

    // Task Manager Elevation Flags
    private const string TaskMgrSetFlag = "-settaskmgr";
    private const string TaskMgrRemoveFlag = "-removetaskmgr";
    
    // Exit Codes for Elevated Operations
    private const int ExitCodeSuccess = 0;
    private const int ExitCodeFailure = 1;
    private const int ExitCodePermissionDenied = 2; // Used when UAC is canceled

    // Per-user registry path for the shell override
    private const string WinlogonRegistryKeyPath = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    // Task Manager Suppression Registry Constants
    private const string TaskMgrPoliciesPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string DisableTaskMgrValueName = "DisableTaskMgr";
    // Value 1 means disabled/suppressed, 0 or deletion means enabled/visible
    private const int DisableTaskMgrValue = 1; 

    private static readonly string UserProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string InstallFolderPath = Path.Combine(UserProfilePath, AppName);

    // Log file path in the installation directory
    private static readonly string LogFilePath = Path.Combine(InstallFolderPath, "RDPShell.log");

    private static readonly string TargetExePath = Path.Combine(InstallFolderPath, AppName + ".exe");
    private static readonly string ReadmePath = Path.Combine(InstallFolderPath, "readme.txt");
    private const string DefaultShell = "explorer.exe";
    
    // Exact title of the RDP dialog that shows when the RDP session is disconnected.
    private const string RDPDisconnectionDialogTitle = "Remote Desktop Connection";

    // Multi-line text for the Readme file.
    private static readonly string ReadmeFileText =
$@"--- {AppName} Readme ---
This utility has been installed as your Windows Shell.

Install Path: {InstallFolderPath}
User: {Environment.UserName}

This utility searches for an RDP file named 'RDPShell*.rdp' in the
install folder and launches the Remote Desktop Client (mstsc.exe)
using that file.  If no RDP file is found, it logs the user out.

Feel free to add your own annotation to the filename after RDPShell,
e.g. the name of the computer you are connecting to.

You may want to edit the RDP file manually and change displayconnectionbar:i:1
to displayconnectionbar:i:0 to disable the connection bar.  It will still show
briefly upon connection, but then go away completely.  Ctrl+Alt+Break will
still toggle full screen mode, and closing the RDP window will trigger log off.

To access the normal shell environment (explorer.exe) repeatedly press
the Windows key after entering your login credentials or clicking 'Sign in'.
If the user does not have Administrative privileges, a UAC prompt will
ask you to provide them.

To Uninstall:
Simply run the '{AppName}.exe' file from any location (e.g., double-click it).
The program will detect the installation and prompt you for uninstallation.
Note: You must log off and log back in for changes to the shell to take effect.";

    // --- LOGGING HELPER ---
    private static void LogDebugMessage(string message)
    {
        // Check the control flag before writing the message
        if (!IsDebugMode) return; // Now checks IsDebugMode static variable

        try
        {
            // Appends the current time and the message to the log file.
            File.AppendAllText(LogFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            // If logging fails (e.g., permissions), we capture the failure but don't crash the main app.
            Debug.WriteLine($"Logging failed: {ex.Message}");
        }
    }

    // --- NATIVE MESSAGE BOX HELPER ---
    // Used only in shell mode or for fatal errors to provide a non-console notification.
    private static int ShowMessageBox(string text, string caption, uint type)
    {
        // Use IntPtr.Zero for hWnd to make it a general message box owned by the desktop.
        return MessageBox(IntPtr.Zero, text, caption, type);
    }

    // --- SHUTDOWN HELPER ---
    // Initiates logoff using ExitWindowsEx with the EWX_FORCE flag.
    private static void InitiateLogoff()
    {
        LogDebugMessage("Starting logoff (ExitWindowsEx EWX_LOGOFF | EWX_FORCE).");
        try
        {
            // Log off and force all open applications to close.
            if (!ExitWindowsEx(EWX_LOGOFF | EWX_FORCE, 0))
            {
                // This is a safety check; ExitWindowsEx rarely fails if the user has rights.
                LogDebugMessage($"ERROR: ExitWindowsEx failed with error code: {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            LogDebugMessage($"CRITICAL ERROR: Failed to call ExitWindowsEx: {ex.Message}");
            
            // Fallback to forced logoff if the initial attempt failed unexpectedly
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/l /f", // Logoff and Force
                    CreateNoWindow = true, 
                    UseShellExecute = false 
                };
                Process.Start(psi);
            }
            catch (Exception forcedEx)
            {
                 LogDebugMessage($"ERROR: Failed to initiate forced logoff using shutdown.exe: {forcedEx.Message}");
            }
        }
    }

    // --- UTILITY FUNCTIONS ---

    // Helper to get the current user's SID (Used for targeting registry in elevated process)
    private static string GetSessionUserSid()
    {
        try
        {
            // Get the SID of the user who launched this process (the non-elevated user)
            System.Security.Principal.SecurityIdentifier sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            return sid.Value;
        }
        catch (Exception ex)
        {
            LogDebugMessage($"ERROR: Failed to get current user SID: {ex.Message}");
            return string.Empty;
        }
    }

    private static bool IsWinKeyDown()
    {
        LogDebugMessage($"Delaying {INITIAL_DELAY_MS}ms for input system initialization...");
        Thread.Sleep(INITIAL_DELAY_MS);

        LogDebugMessage("Performing single poll check for Windows Key (State bit | Pressed bit)...");

        if ((GetAsyncKeyState(VK_LWIN) & KEY_CHECK_MASK) != 0 ||
            (GetAsyncKeyState(VK_RWIN) & KEY_CHECK_MASK) != 0)
        {
            LogDebugMessage("Windows key (LWIN or RWIN) detected during single poll.");
            return true;
        }

        LogDebugMessage("Windows key NOT detected after single poll.");
        return false;
    }

    // --- CONSOLE MANAGEMENT (THE FIX) ---
    private static void AllocateAndRedirectConsole()
    {
        // 1. Force the allocation of a NEW console window
        bool consoleAllocated = AllocConsole();

        if (consoleAllocated)
        {
            // --- CRITICAL FIX: Explicitly redirect streams to the newly allocated console ---
            try
            {
                // Standard Output Stream
                TextWriter tw = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true };
                Console.SetOut(tw);

                // Standard Input Stream
                TextReader tr = new StreamReader(Console.OpenStandardInput(), Console.InputEncoding);
                Console.SetIn(tr);
                
                LogDebugMessage($"New console successfully allocated and streams redirected.");
            }
            catch (Exception ex)
            {
                LogDebugMessage($"Failed to redirect console streams after allocation: {ex.Message}");
                // Fallback to message box if console fails
                ShowMessageBox(
                    "Could not initialize console for installer mode.",
                    AppName,
                    MB_ICONERROR | MB_OK
                );
            }
        }
        else
        {
            LogDebugMessage("FATAL: Failed to allocate a new console window.");
            // Fallback to message box if console fails
            ShowMessageBox(
                "FATAL ERROR: Could not allocate a console for user interaction. Exiting.",
                AppName,
                MB_ICONERROR | MB_OK
            );
            Environment.Exit(ExitCodeFailure);
        }
    }


    // --- MAIN ENTRY POINT ---
    [STAThread] 
    public static void Main(string[] args)
    {
        try
        {
            List<string> filteredArgs = new List<string>();
            
            // 1. Check and strip -debug argument, set global flag
            // This happens for ALL modes (shell, elevated, installer).
            foreach (string arg in args)
            {
                if (arg.Equals(DebugFlag, StringComparison.OrdinalIgnoreCase))
                {
                    IsDebugMode = true;
                }
                else
                {
                    filteredArgs.Add(arg);
                }
            }
            
            LogDebugMessage($"Application started. Debug Mode: {IsDebugMode}. Filtered Arguments: {string.Join(" ", filteredArgs)}");

            // Re-assign filtered args for primary mode checks
            args = filteredArgs.ToArray();

            // 2. Determine Primary Mode based on filtered arguments
            bool isShellMode = args.Length > 0 && args[0].Equals(ShellFlag, StringComparison.OrdinalIgnoreCase);
            bool isAdminCheckMode = args.Length > 0 && args[0].Equals(AdminCheckFlag, StringComparison.OrdinalIgnoreCase);

            // Elevated TaskMgr Mode Check: Expects 2 arguments: Flag and Target SID
            bool isTaskMgrElevatedMode = false;
            string taskMgrFlag = string.Empty;
            string targetSid = string.Empty;

            // Check for 2 arguments indicating elevated set task manager mode, as -debug will have been filtered out
            if (args.Length == 2)
            {
                if (args[0].Equals(TaskMgrSetFlag, StringComparison.OrdinalIgnoreCase) || 
                    args[0].Equals(TaskMgrRemoveFlag, StringComparison.OrdinalIgnoreCase))
                {
                    taskMgrFlag = args[0];
                    targetSid = args[1]; // The SID of the user who launched the installer
                    isTaskMgrElevatedMode = true;
                }
            }

            if (isShellMode)
            {
                RunAsShell();
            }
            else if (isAdminCheckMode)
            {
                LogDebugMessage("AdminCheck mode triggered. Exiting successfully.");
                return;
            }
            else if (isTaskMgrElevatedMode)
            {
                // Elevated operation requested. This process is running as Administrator.
                RunAsTaskMgrElevated(taskMgrFlag, targetSid);
            }
            else
            {
                // Installer/Uninstaller Mode: REQUIRES A NEW CONSOLE FOR USER INTERACTION
                AllocateAndRedirectConsole();
                CheckAndManageInstallation();

                // Clean up the allocated console (optional, as the process exits)
                FreeConsole();
            }
        }
        catch (Exception ex)
        {
            LogDebugMessage($"FATAL UNHANDLED EXCEPTION: {ex}");

            // Use native message box for visibility, as this could happen in shell mode or before
            // console output is established correctly.
            ShowMessageBox(
                $"FATAL ERROR: An unhandled exception occurred.\n\nDetails written to {LogFilePath}",
                AppName,
                MB_ICONERROR | MB_OK
            );
            Environment.Exit(ExitCodeFailure);
        }
    }

    // --- SHELL MODE LOGIC ---
    private static void RunAsShell()
    {
        LogDebugMessage("Entering RunAsShell mode.");

        Process? subShellProcess = null;
        bool rdpMode = false;

        try
        {
            if (IsWinKeyDown())
            {
                LogDebugMessage("Windows key detected. Attempting admin credential check.");

                // Attempt to elevate the process just to confirm UAC acceptance
                if (AttemptAdminCredentialCheck())
                {
                    LogDebugMessage($"Admin check passed. Launching {DefaultShell}.");
                    // Launch Explorer directly without elevation
                    subShellProcess = Process.Start(DefaultShell);
                }
                else
                {
                    LogDebugMessage("Admin check failed or canceled. Logging off.");
                    ShowMessageBox(
                        "You must have or provide administrative privileges to access the local desktop on this account.",
                        AppName,
                        MB_ICONERROR | MB_OK
                    );
                }
            }
            else
            {
                // Win Key is not pressed (RDP Mode)
                LogDebugMessage("Windows key not detected. Attempting RDP mode.");

                string[] rdpFiles = Directory.GetFiles(InstallFolderPath, "RDPShell*.rdp", SearchOption.TopDirectoryOnly);

                if (rdpFiles.Length >= 1)
                {
                    // Look for RDPShell.ps1 or RDPShell.bat (prefer .ps1)
                    string ps1File = Path.Combine(InstallFolderPath, "RDPShell.ps1");
                    string batFile = Path.Combine(InstallFolderPath, "RDPShell.bat");
                
                    string preScript = null;
                    bool isPowerShell = false;
                
                    if (File.Exists(ps1File))
                    {
                        preScript = ps1File;
                        isPowerShell = true;
                    }
                    else if (File.Exists(batFile))
                    {
                        preScript = batFile;
                    }
                
                    if (preScript != null)
                    {
                        LogDebugMessage($"Found pre-launch script: {Path.GetFileName(preScript)}. Executing...");
                
                        try
                        {
                            ProcessStartInfo psi;
                
                            if (isPowerShell)
                            {
                                psi = new ProcessStartInfo
                                {
                                    FileName = "powershell.exe",
                                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{preScript}\"",
                                    WorkingDirectory = InstallFolderPath,
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                };
                            }
                            else
                            {
                                psi = new ProcessStartInfo
                                {
                                    FileName = preScript,
                                    WorkingDirectory = InstallFolderPath,
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                };
                            }
                
                            using (var proc = Process.Start(psi))
                            {
                                proc.WaitForExit();
                
                                if (proc.ExitCode != 0)
                                {
                                    throw new Exception($"{Path.GetFileName(preScript)} exited with code {proc.ExitCode}.");
                                }
                
                                LogDebugMessage($"{Path.GetFileName(preScript)} completed successfully.");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogDebugMessage($"Error running {Path.GetFileName(preScript)}: {ex.Message}");
                        }
                    }
                    else
                    {
                        LogDebugMessage("No RDPShell.ps1 or RDPShell.bat found. Skipping pre-launch script.");
                    }
                    
                    if (rdpFiles.Length > 1)
                    {
                        LogDebugMessage($"Found multiple RDP files. Using the first one: {Path.GetFileName(rdpFiles[0])}.");
                    } else {
                        LogDebugMessage($"Found RDP file. Launching mstsc.exe with: {rdpFiles[0]}.");
                    }
                    subShellProcess = LaunchRDP(rdpFiles[0]);
                    rdpMode = true;
                }
                else
                {
                    LogDebugMessage("No RDP file found. Logging off.");
                    ShowMessageBox(
                        $"No RDP file found in '{InstallFolderPath}' matching 'RDPShell*.rdp'. Exiting user session now.",
                        AppName,
                        MB_ICONWARNING | MB_OK
                    );
                }
            }
        }
        catch (Exception ex)
        {
            LogDebugMessage($"CRITICAL ERROR during shell launch: {ex.Message}");
            ShowMessageBox(
                $"Critical Error during sub-shell launch: {ex.Message}. Logging off now.",
                AppName,
                MB_ICONERROR | MB_OK
            );
            InitiateLogoff();
            return;
        }

        // 2. Monitor the Subshell Process (Only if a process was started)
        if (subShellProcess != null)
        {
            LogDebugMessage($"Monitoring subshell process ID: {subShellProcess.Id}");

            if (rdpMode)
            {
                // RDP Mode: Set global process and register the event handler for cleanup
                RDPSubShellProcess = subShellProcess;
                SystemEvents.SessionSwitch += OnSessionSwitch;
                LogDebugMessage("SessionSwitch event listener registered for RDP mode.");
            }

            // Simplified monitoring loop: block until the subshell process exits.
            try
            {
                subShellProcess.WaitForExit();
            }
            catch (InvalidOperationException)
            {
                // Process may have already exited and been disposed by an external event (e.g., logoff triggered by CleanupLoop).
                LogDebugMessage("Subshell process was already disposed or exited.");
            }

            LogDebugMessage("Subshell process exited.");

            // Clean up RDP resources and event handler
            if (rdpMode)
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                CleanupCts?.Cancel(); // Ensure the cleanup loop stops if it's running
                CleanupCts?.Dispose();
                RDPSubShellProcess?.Dispose();
                RDPSubShellProcess = null;
                LogDebugMessage("SessionSwitch event listener unregistered.");
            }
        }

        // 3. Exit the shell process.
        LogDebugMessage("Subshell exited. Initiating session logoff.");
        // Use silent logoff helper
        InitiateLogoff();

        Environment.Exit(ExitCodeSuccess);
    }

    // --- ASYNCHRONOUS SESSION SWITCH HANDLER ---
    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Only interested if we are in RDP mode (RDPSubShellProcess is set and running)
        if (RDPSubShellProcess == null || RDPSubShellProcess.HasExited) return;

        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            LogDebugMessage("SessionSwitch: Workstation locked. Starting asynchronous cleanup loop.");

            // If a cleanup loop is already running, cancel and dispose of the old one first, just in case.
            CleanupCts?.Cancel();
            CleanupCts?.Dispose();

            CleanupCts = new CancellationTokenSource();

            // Start the polling loop on a background thread
            // Note: The loop runs until unlocked OR logoff is triggered.
            Task.Run(() => CleanupLoop(RDPSubShellProcess.Id, CleanupCts.Token));
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            LogDebugMessage("SessionSwitch: Workstation unlocked. Canceling asynchronous cleanup loop.");

            // Signal the background task to stop
            CleanupCts?.Cancel(); // The task itself will exit gracefully upon cancellation.
        }
    }

    // The polling loop for forced logoff when a blocking RDP dialog appears while the system is locked.
    private static void CleanupLoop(int processId, CancellationToken token)
    {
        LogDebugMessage($"CleanupLoop started for PID: {processId}.");

        while (!token.IsCancellationRequested)
        {
            // The method called here will trigger logoff if it finds a match.
            PollAndLogoffIfBlockingWindowFound((uint)processId);

            try
            {
                // Wait 1 second (longer interval since it's only active when locked)
                // WaitHandle.WaitOne is a blocking wait, which is fine for this background thread.
                token.WaitHandle.WaitOne(1000);
            }
            catch (OperationCanceledException)
            {
                // Loop breaks naturally when token is canceled.
                break;
            }
            catch (Exception ex)
            {
                LogDebugMessage($"CleanupLoop encountered unexpected error during wait: {ex.Message}");
            }
        }

        LogDebugMessage("CleanupLoop stopped due to cancellation/unlock.");
    }

    // --- RDP WINDOW CLEANUP LOGIC ---

    // New method to wrap the enumeration and handle exceptions.
    private static void PollAndLogoffIfBlockingWindowFound(uint subShellProcessId)
    {
        try
        {
            // Use GCHandle for safely passing the process ID to the native C# callback
            GCHandle gch = GCHandle.Alloc(subShellProcessId);
            try
            {
                // Enumerate all top-level windows (the callback will handle the logoff)
                EnumWindows(EnumWindowCallback, GCHandle.ToIntPtr(gch));
            }
            finally
            {
                if (gch.IsAllocated)
                    gch.Free();
            }
        }
        catch (Exception ex)
        {
            // Catch and log, but don't crash the loop
            LogDebugMessage($"Error during window cleanup poll: {ex.Message}");
        }
    }


    // Static callback used by EnumWindows to check if a window belongs to our RDP process
    // AND if it has the known blocking title.
    private static bool EnumWindowCallback(IntPtr hWnd, IntPtr lParam)
    {
        object? target = GCHandle.FromIntPtr(lParam).Target;

        if (target is not uint ownerProcessId)
        {
            LogDebugMessage("Target is not a valid uint process ID.");
            return true;
        }

        uint windowProcessId;
        GetWindowThreadProcessId(hWnd, out windowProcessId);

        if (windowProcessId == ownerProcessId)
        {
            StringBuilder windowTitle = new StringBuilder(256);
            GetWindowText(hWnd, windowTitle, windowTitle.Capacity);

            LogDebugMessage($"[Enum] Found RDP Process Window (PID: {windowProcessId}). Title: '{windowTitle}'");
            
            // Check only for the specific blocking title
            if (windowTitle.ToString() == RDPDisconnectionDialogTitle)
            {
                LogDebugMessage($"[Enum] Found MATCHING blocking RDP window: Title='{windowTitle}'. INITIATING LOGOFF.");

                // Force logoff immediately.
                // This will end both the RDP process and the RDPShell process (triggering the WaitForExit in RunAsShell).
                // Use silent logoff helper
                InitiateLogoff();

                // Stop enumeration early, as the entire session is about to terminate.
                return false;
            }
        }

        return true;
    }


    // --- CREDENTIAL CHECK LOGIC ---

    // Helper function to force a UAC prompt and check if elevation was accepted.
    private static bool AttemptAdminCredentialCheck()
    {
        // Use the application's own executable path and the admin check flag which will exit immediately
        ProcessStartInfo psi = new ProcessStartInfo(TargetExePath, AdminCheckFlag);
        psi.UseShellExecute = true;
        psi.Verb = "runas"; // Requests elevation

        try
        {
            LogDebugMessage("UAC Check: Starting Admin credential check attempt.");
            
            LogDebugMessage($"UAC Check: Launching process with verb 'runas': {TargetExePath} {AdminCheckFlag}");

            // Start the process, which will trigger the UAC prompt
            Process? tempProcess = Process.Start(psi);

            if (tempProcess != null)
            {
                LogDebugMessage($"UAC Check: Process started (PID: {tempProcess.Id}). Waiting for exit...");
                // Wait for the tiny sub-process to exit. If UAC is accepted, it exits immediately.
                tempProcess.WaitForExit();
                LogDebugMessage($"UAC Check: Process exited. Exit Code: {tempProcess.ExitCode}");
                tempProcess.Dispose();
            }
            // If we reach here, the process launched and exited successfully (UAC accepted).
            LogDebugMessage("UAC Check: SUCCESS path (UAC likely accepted). Returning true.");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Error code 1223 means "The operation was canceled by the user." (UAC declined).
            LogDebugMessage($"UAC Check: CANCELED path (Win32Exception 1223). UAC was likely declined by the user. Returning false.");
            return false;
        }
        catch (Exception ex)
        {
            // Use native message box for visibility in shell context
            LogDebugMessage($"UAC Check: UNEXPECTED ERROR path: {ex.Message} (Error Code: {((ex is Win32Exception w32) ? w32.NativeErrorCode.ToString() : "N/A")}). Returning false.");
            ShowMessageBox(
                $"Unexpected error during credential check: {ex.Message}",
                AppName,
                MB_ICONERROR | MB_OK
            );
            return false;
        }
    }

    // Helper to launch the RDP client securely
    private static Process LaunchRDP(string rdpFilePath)
    {
        ProcessStartInfo psi = new ProcessStartInfo("mstsc.exe", $"\"{rdpFilePath}\"");
        psi.UseShellExecute = true;
        return Process.Start(psi)!;
    }


    // --- INSTALLER/UNINSTALLER LOGIC ---
    private static void CheckAndManageInstallation()
    {
        LogDebugMessage("Entering CheckAndManageInstallation mode (Allocated Console I/O).");

        string currentShellValue = GetUserShellRegistryValue();
        
        // We check for a prefix match, ignoring the optional '-debug' flag.
        string requiredShellPrefix = $"\"{TargetExePath}\" {ShellFlag}";

        if (currentShellValue.StartsWith(requiredShellPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // --- SHELL IS INSTALLED ---
            LogDebugMessage($"Installation detected. Current Shell Value: {currentShellValue}");

            Console.WriteLine($"========================================================================");
            Console.WriteLine($" {AppName} Shell Detected");
            Console.WriteLine($"========================================================================");
            Console.WriteLine($"The {AppName} shell is currently installed for user {Environment.UserName}.");
            Console.WriteLine($"What would you like to do?");
            Console.WriteLine($"(C)hange \"Task Manager\" suppression on Ctrl+Alt+Del screen");
            Console.WriteLine($"(U)ninstall {AppName} shell and revert to default");
            Console.Write($"\n(Press C or U, or any other key to exit): ");

            char input = char.ToLower(Console.ReadKey(true).KeyChar);
            Console.WriteLine(input); // Newline after key press

            if (input == 'c')
            {
                Console.Clear();
                ChangeTaskManagerSuppression();
            }
            else if (input == 'u')
            {
                Console.Clear();
                Console.Write("Are you sure you wish to UNINSTALL RDPShell? (Press y to confirm, or any other key to cancel): ");
                char confirm = char.ToLower(Console.ReadKey(true).KeyChar);
                Console.WriteLine(confirm); // Ensure a newline after confirmation read

                if (confirm == 'y')
                {
                    UninstallShell();
                }
                else
                {
                    Console.WriteLine("Uninstallation canceled. Press any key to exit.");
                    Console.ReadKey(true);
                    Console.WriteLine(); // Ensure prompt returns on a new line
                }
            }
            else
            {
                Console.WriteLine("Canceled. Press any key to exit.");
                Console.ReadKey(true);
                Console.WriteLine(); // Ensure prompt returns on a new line
            }
            return;
        }

        // --- SHELL IS NOT INSTALLED ---
        LogDebugMessage("Installation not detected. Prompting for install.");
        // Prepare the base shell value.
        string baseShellValue = requiredShellPrefix;


        Console.WriteLine($"========================================================================");
        Console.WriteLine($" {AppName} Shell Not Installed");
        Console.WriteLine($"========================================================================");
        Console.WriteLine($"The {AppName} shell is currently NOT installed for user {Environment.UserName}.");
        Console.Write($"Would you like to INSTALL {AppName} as your default shell? (Press y to confirm, or any other key to exit): ");

        char installInput = Console.ReadKey(true).KeyChar;
        Console.WriteLine(installInput);

        if (char.ToLower(installInput) == 'y')
        {
            InstallShell(baseShellValue);
        }
        else
        {
            Console.WriteLine("Installation canceled. Press any key to exit.");
            Console.ReadKey(true);
            Console.WriteLine();
        }
    }


    private static void InstallShell(string baseShellValue)
    {
        try
        {
            LogDebugMessage("Starting installation process.");
            
            // Get the path of the running executable. Use MainModule.FileName for single-file executables.
            string? currentExePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExePath))
            {
                // Fallback, though should not be needed for a running executable.
                currentExePath = Path.Combine(AppContext.BaseDirectory, AppName + ".exe");
            }

            // Determine the FINAL shell value, including the debug flag if active.
            string finalShellValue = baseShellValue;
            if (IsDebugMode)
            {
                finalShellValue += $" {DebugFlag}";
                LogDebugMessage($"Adding {DebugFlag} flag to final shell registry value: {finalShellValue}");
            }
            

            Console.WriteLine("\n--- Installing ---");

            if (!Directory.Exists(InstallFolderPath))
            {
                LogDebugMessage($"Creating install directory: {InstallFolderPath}");
                Console.WriteLine($"1. Creating install directory: {InstallFolderPath}");
                // This is a critical check where install must be cancelled on failure
                try
                {
                    Directory.CreateDirectory(InstallFolderPath);
                }
                catch (Exception createDirEx)
                {
                    LogDebugMessage($"FATAL: Directory creation failed at {InstallFolderPath}\nCanceling install: {createDirEx.Message}");
                    Console.WriteLine($"\n--- Installation FAILED ---\nFailed to create installlation directory at ({InstallFolderPath})");
                    Console.WriteLine($"FATAL ERROR: {createDirEx.Message}");
                    Console.WriteLine("\nInstallation canceled.");
                    Console.WriteLine("\nPress any key to exit.");
                    Console.ReadKey(true);
                    Console.WriteLine(); // ADDED: Ensure prompt returns on a new line
                    return; // *** ABORT INSTALLATION ***
                }
                
            }
            else
            {
                Console.WriteLine($"1. Verifying install directory: {InstallFolderPath}");
            }

            Thread.Sleep(500);
            if (!currentExePath.Equals(TargetExePath, StringComparison.OrdinalIgnoreCase))
            {
                LogDebugMessage($"Copying executable from {currentExePath} to {TargetExePath}.");
                Console.WriteLine("2. Copying executable to the persistent install location...");
                // This is the critical check where install must be cancelled on failure
                try
                {
                    File.Copy(currentExePath, TargetExePath, true);
                }
                catch (Exception copyEx)
                {
                    LogDebugMessage($"FATAL: File copy failed from {currentExePath} to {TargetExePath}\nCanceling install: {copyEx.Message}");
                    Console.WriteLine($"\n--- Installation FAILED ---\nFailed to copy file from {currentExePath} to {TargetExePath}");
                    Console.WriteLine($"FATAL ERROR: {copyEx.Message}");
                    Console.WriteLine("Installation canceled.");
                    Console.WriteLine("\nPress any key to exit.");
                    Console.ReadKey(true);
                    Console.WriteLine(); // ADDED: Ensure prompt returns on a new line
                    return; // *** ABORT INSTALLATION HERE ***
                }
            }
            else
            {
                Console.WriteLine("2. Verifying current executable path.");
            }

            Thread.Sleep(500);
            LogDebugMessage($"Writing Readme to {ReadmePath}.");
            Console.WriteLine($"3. Writing Readme file to: {ReadmePath}");
            File.WriteAllText(ReadmePath, ReadmeFileText);

            Thread.Sleep(500);
            LogDebugMessage($"Setting registry Shell value to: {finalShellValue}");
            Console.WriteLine("4. Setting Windows registry key...");
            SetUserShellRegistryValue(finalShellValue);

            Thread.Sleep(500);
            Console.WriteLine("\n--- Installation Successful ---");
            Console.WriteLine($"{AppName} is successfully installed as your shell.");
            Console.WriteLine("The new shell will take effect on your next login.");
            
            // TASK MANAGER PROMPT
            Console.WriteLine($"\n--- Task Manager Suppression ---");
            Console.Write("Would you like to disable \"Task Manager\" on the Ctrl+Alt+Del screen?\n(Press y to confirm, or any other key to keep enabled): ");

            char suppressInput = Console.ReadKey(true).KeyChar;
            Console.WriteLine(suppressInput); // ADDED: Ensure a newline after key press

            if (char.ToLower(suppressInput) == 'y')
            {
                // Call the updated method which handles UAC and modification
                int result = SuppressTaskManager();

                if (result == ExitCodeSuccess)
                {
                    Console.WriteLine("Task Manager suppressed. It will not be visible on the Ctrl+Alt+Del screen.");
                }
                else if (result == ExitCodePermissionDenied)
                {
                    Console.WriteLine("! WARNING: FAILED to suppress Task Manager. The required administrator credentials were not provided or were denied.");
                    Console.WriteLine("Task Manager will remain enabled. You can rerun RDPShell to attempt changing setting again.");
                }
                else // ExitCodeFailure
                {
                     Console.WriteLine("! ERROR: FAILED to suppress Task Manager. An unexpected internal error occurred.");
                    Console.WriteLine("Task Manager will remain enabled. You can rerun RDPShell to attempt changing setting again.");
                }
            }
            else
            {
                Console.WriteLine("Task Manager will remain enabled.");
            }
            
            Console.WriteLine($"\nReadme file created at: {ReadmePath}");

            Console.Write($"Would you like to view the readme now? (Press y, or any other key to exit): ");

            char readmeInput = Console.ReadKey(true).KeyChar;
            Console.WriteLine(readmeInput); // ADDED: Ensure a newline after key press

            if (char.ToLower(readmeInput) == 'y')
            {
                // Show Readme inline
                Console.Clear();
                Console.WriteLine(ReadmeFileText);
                Console.WriteLine("\nPress any key to exit.");
                Console.ReadKey(true);
                Console.WriteLine(); // ADDED: Ensure prompt returns on a new line
            }
            
            LogDebugMessage("Installation successful. Console displayed Readme.");
        }
        catch (Exception ex)
        {
            LogDebugMessage($"INSTALLATION FAILED: {ex.Message}");
            Console.WriteLine("\n--- Installation FAILED ---");
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine("\nPress any key to exit.");
            Console.ReadKey(true);
            Console.WriteLine(); // ADDED: Ensure prompt returns on a new line
        }
    }

    private static void UninstallShell()
    {
        try
        {
            // --- TASK MANAGER RESTORATION CHECK (MUST OCCUR FIRST AND CAN ABORT) ---
            bool wasTaskMgrSuppressed = IsTaskManagerSuppressed();
            
            if (wasTaskMgrSuppressed)
            {
                Console.WriteLine($"\n--- Task Manager Restoration ---");
                Console.WriteLine("Task Manager on the Ctrl+Alt+Del screen is currently disabled.");
                Console.Write("Would you like to RESTORE that functionality? (Press y to restore, or any other key to keep it disabled): ");

                char restoreInput = char.ToLower(Console.ReadKey(true).KeyChar);
                Console.WriteLine(restoreInput); // ADDED: Ensure a newline after key press

                if (restoreInput == 'y')
                {
                    int result = RestoreTaskManager();

                    if (result != ExitCodeSuccess) // Check for failure (including permission denied)
                    {
                        LogDebugMessage($"UNINSTALL CANCELED: Failed to restore Task Manager.");
                        
                        // CANCEL THE UNINSTALL
                        Console.WriteLine("\n\n!! UNINSTALLATION CANCELED !!");
                        Console.WriteLine("ERROR: Task Manager could not be re-enabled.");
                        if (result == ExitCodePermissionDenied)
                        {
                            Console.WriteLine("You must provide administrator credentials to change the setting.");
                        }
                        else
                        {
                            Console.WriteLine("An unexpected error occurred during restoration attempt.");
                        }

                        Console.WriteLine("\nPress any key to exit.");
                        Console.ReadKey(true);
                        Console.WriteLine(); // ADDED: Ensure prompt returns on a new line
                        return; // Exit the function, stopping uninstallation
                    }
                    Console.WriteLine("Task Manager restored. It will be visible on the Ctrl+Alt+Del screen after next login.");
                }
                else
                {
                    Console.WriteLine("Task Manager will remain disabled until manually re-enabled.");
                }
            }
            // --- END TASK MANAGER CHECK ---

            LogDebugMessage("Starting uninstallation process.");
            Console.WriteLine("\n--- Uninstalling ---");

            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(WinlogonRegistryKeyPath, true))
            {
                if (key != null)
                {
                    key.DeleteValue("Shell", false);
                    Console.WriteLine("1. Registry shell value successfully deleted/reset.");
                }
            }

            // Attempt to delete Readme file first
            if (File.Exists(ReadmePath))
            {
                try
                {
                    File.Delete(ReadmePath);
                    Console.WriteLine("2. Readme file deleted.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("2. Could not delete Readme file.");
                    LogDebugMessage($"Failed to delete Readme file: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("2. Readme file not found.");
            }

            // Attempt to delete Log file second
            if (File.Exists(LogFilePath))
            {
                try
                {
                    File.Delete(LogFilePath);
                    Console.WriteLine("3. Log file deleted.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("3. Could not delete Log file.");
                    LogDebugMessage($"Failed to delete Log file: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("3. Log file not found.");
            }
            
            Console.WriteLine("4. Executable file remains in install directory for potential re-use.");

            LogDebugMessage("Uninstallation complete.");

            Console.WriteLine("\n--- Uninstallation Complete ---");
            Console.WriteLine($"{AppName} has been uninstalled. The shell has been reverted to the default ({DefaultShell}).");
            Console.WriteLine("Please log off and log back on for changes to take effect.");
            Console.WriteLine("\nPress any key to exit.");
            Console.ReadKey(true);
            Console.WriteLine(); // ADDED: Ensure prompt returns on a new line
        }
        catch (Exception ex)
        {
            LogDebugMessage($"UNINSTALLATION FAILED: {ex.Message}");
            Console.WriteLine("\n--- Uninstallation FAILED ---");
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine("\nPress any key to exit.");
            Console.ReadKey(true);
            Console.WriteLine(); // ADDED: Ensure prompt returns on a new line
        }
    }
    
    // --- TASK MANAGER REGISTRY HELPERS ---

    // Check if the Task Manager is currently disabled (i.e., DisableTaskMgr is set to 1)
    private static bool IsTaskManagerSuppressed()
    {
        // This check targets the current user's policy setting (HKCU)
        // Since this is running in the non-elevated installer context, HKCU is correct.
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(TaskMgrPoliciesPath))
        {
            if (key != null)
            {
                // Check if the value exists and is set to 1
                object? value = key.GetValue(DisableTaskMgrValueName);
                if (value is int intValue && intValue == DisableTaskMgrValue)
                {
                    return true;
                }
            }
        }
        return false;
    }
    
    // Set the registry value to disable (suppress) Task Manager. Returns status code.
    private static int SuppressTaskManager()
    {
        LogDebugMessage($"Suppressing Task Manager (Setting {DisableTaskMgrValueName}=1). Skipping local modification and attempting UAC elevation immediately.");
        
        // 1. Get the SID of the current (non-elevated) user session
        string targetSid = GetSessionUserSid();
        if (string.IsNullOrEmpty(targetSid)) return ExitCodeFailure;
        
        // 2. Launch UAC elevation using the appropriate flag
        return LaunchTaskMgrElevation(TaskMgrSetFlag, targetSid);
    }

    // Delete the registry value to enable (restore) Task Manager. Returns status code.
    private static int RestoreTaskManager()
    {
        LogDebugMessage($"Restoring Task Manager (Deleting {DisableTaskMgrValueName}). Skipping local modification and attempting UAC elevation immediately.");
        
        // 1. Get the SID of the current (non-elevated) user session
        string targetSid = GetSessionUserSid();
        if (string.IsNullOrEmpty(targetSid)) return ExitCodeFailure;

        // 2. Launch UAC elevation using the appropriate flag
        return LaunchTaskMgrElevation(TaskMgrRemoveFlag, targetSid);
    }
    
    // --- ELEVATED REGISTRY MODIFICATION LOGIC ---
    // This is run by the elevated process created in LaunchTaskMgrElevation
    private static void RunAsTaskMgrElevated(string taskMgrFlag, string targetSid)
    {
        // Elevated process is running. It must modify HKEY_USERS\<SID>\... or HKEY_CURRENT_USER\...
        // targetSid is the SID of the user who originally launched the installer.
        LogDebugMessage($"[ELEVATED] Starting Task Manager operation for original SID: {targetSid}. Flag: {taskMgrFlag}.");

        if (string.IsNullOrEmpty(targetSid))
        {
            LogDebugMessage("[ELEVATED] Target SID is empty. Exiting with generic failure.");
            Environment.Exit(ExitCodeFailure);
            return;
        }
        
        // 1. Get the SID of the currently running (elevated) user
        string currentElevatedSid = GetSessionUserSid();

        // Determine the root key and path based on whether the elevation was done by the logged-in user or another admin
        RegistryKey rootKey;
        string policyPath;

        // If the original user's SID matches the elevated user's SID, we can write to HKCU directly.
        // This handles cases where the original user is already an admin.
        if (targetSid.Equals(currentElevatedSid, StringComparison.OrdinalIgnoreCase))
        {
            rootKey = Registry.CurrentUser;
            policyPath = TaskMgrPoliciesPath; // e.g., Software\Microsoft\Windows\CurrentVersion\Policies\System
            LogDebugMessage("[ELEVATED] Target SID matches elevated SID. Modifying HKEY_CURRENT_USER.");
        }
        else
        {
            // The original user (targetSid) is different from the elevated user.
            // We must write to HKEY_USERS\<targetSid>\...
            rootKey = Registry.Users;
            // Path is HKEY_USERS\<SID>\Software\Microsoft\Windows\CurrentVersion\Policies\System
            policyPath = $"{targetSid}\\{TaskMgrPoliciesPath}"; 
            LogDebugMessage($"[ELEVATED] Target SID different from elevated SID. Modifying HKEY_USERS\\{targetSid}.");
        }
        
        // Determine if we are setting (Suppressing) or removing (Restoring)
        bool setSuppression = taskMgrFlag.Equals(TaskMgrSetFlag, StringComparison.OrdinalIgnoreCase);

        try
        {
            // Try to open the key
            using (RegistryKey? targetKey = rootKey.OpenSubKey(policyPath, writable: true))
            {
                if (targetKey != null)
                {
                    if (setSuppression)
                    {
                        targetKey.SetValue(DisableTaskMgrValueName, DisableTaskMgrValue, RegistryValueKind.DWord);
                        LogDebugMessage($"[ELEVATED] Task Manager suppressed successfully.");
                    }
                    else
                    {
                        // DeleteValue with throwOnMissingValue=false avoids exceptions if the value doesn't exist
                        targetKey.DeleteValue(DisableTaskMgrValueName, throwOnMissingValue: false);
                        LogDebugMessage($"[ELEVATED] Task Manager restored successfully.");
                    }
                    targetKey.Close();
                    Environment.Exit(ExitCodeSuccess);
                }
                else
                {
                    // If opening failed, attempt to create the missing key (Policies\System)
                    using (RegistryKey? newKey = rootKey.CreateSubKey(policyPath, writable: true))
                    {
                        if (newKey != null)
                        {
                            if (setSuppression)
                            {
                                newKey.SetValue(DisableTaskMgrValueName, DisableTaskMgrValue, RegistryValueKind.DWord);
                                LogDebugMessage($"[ELEVATED] Task Manager suppressed successfully after creating key.");
                            }
                            // If restoring and the key didn't exist, we're done (success)
                            
                            newKey.Close();
                            Environment.Exit(ExitCodeSuccess); 
                            return;
                        }

                        LogDebugMessage($"[ELEVATED] FAILED: Could not open OR create target registry key: {policyPath}");
                        Environment.Exit(ExitCodeFailure); // Internal failure
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogDebugMessage($"[ELEVATED] CRITICAL ERROR during registry modification for SID {targetSid}: {ex.Message}");
            Environment.Exit(ExitCodeFailure); // Internal failure
        }
    }


    // --- TASK MANAGER REGISTRY HELPERS (WITH ELEVATION FALLBACK) ---
    // Helper to launch the elevated process and check result. Returns the exit code of the elevated process.
    private static int LaunchTaskMgrElevation(string flag, string sid)
    {
        // TargetExePath is the path to the persistent executable
        // Arguments: <Flag> <TargetSID> [optional -debug]
        string arguments = $"{flag} {sid}"; 
        
        // CHANGED: Conditionally append -debug flag
        if (IsDebugMode)
        {
            arguments += $" {DebugFlag}";
        }
        
        ProcessStartInfo psi = new ProcessStartInfo(TargetExePath, arguments); 
        psi.UseShellExecute = true;
        psi.Verb = "runas"; // Requests elevation

        try
        {
            LogDebugMessage($"[ELEVATION] Starting process with verb 'runas': {TargetExePath} {arguments}");
            
            Process? tempProcess = Process.Start(psi);
            if (tempProcess == null) return ExitCodeFailure;

            tempProcess.WaitForExit();
            int exitCode = tempProcess.ExitCode;
            tempProcess.Dispose();

            LogDebugMessage($"[ELEVATION] Elevated process exited. Exit Code: {exitCode}");
            
            // Return the exit code from the elevated process directly
            return exitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Error code 1223 means "The operation was canceled by the user." (UAC declined).
            LogDebugMessage("[ELEVATION] UAC was canceled by the user (Win32Exception 1223).");
            // Map UAC cancellation to the PermissionDenied exit code
            return ExitCodePermissionDenied;
        }
        catch (Exception ex)
        {
            LogDebugMessage($"[ELEVATION] UNEXPECTED ERROR during elevation: {ex.Message}");
            return ExitCodeFailure;
        }
    }

    // --- TASK MANAGER INTERACTIVE MANAGEMENT ---
    private static void ChangeTaskManagerSuppression()
    {
        LogDebugMessage("Entering ChangeTaskManagerSuppression mode.");
        Console.WriteLine($"\n========================================================================");
        Console.WriteLine($" Task Manager Suppression Management");
        Console.WriteLine($"========================================================================");

        bool isSuppressed = IsTaskManagerSuppressed();
        string status = isSuppressed ? "DISABLED (hidden)" : "ENABLED (visible)";
        Console.WriteLine($"Current Status: Task Manager is currently {status} on Ctrl+Alt+Del screen.");
        Console.WriteLine("------------------------------------------------------------------------");
        
        Console.WriteLine("Options:");
        Console.WriteLine("(E)nable \"Task Manager\" (restores functionality, deletes registry key)");
        Console.WriteLine("(R)emove \"Task Manager\" (suppresses functionality, sets registry key)");
        Console.Write("\n(Press E or R, or any other key to cancel): ");

        char input = char.ToLower(Console.ReadKey(true).KeyChar);
        Console.WriteLine(input); // ADDED: Ensure a newline after key press

        if (input == 'e')
        {
            int result = RestoreTaskManager();
            if (result == ExitCodeSuccess)
            {
                Console.WriteLine("Task Manager restored (enabled) successfully.");
            }
            else if (result == ExitCodePermissionDenied)
            {
                Console.WriteLine("! WARNING: RESTORE FAILED. The required administrator credentials were not provided or were denied.");
            }
            else
            {
                Console.WriteLine("! ERROR: FAILED to restore Task Manager. An unexpected internal error occurred.");
            }
        }
        else if (input == 'r')
        {
            int result = SuppressTaskManager();
            if (result == ExitCodeSuccess)
            {
                Console.WriteLine("Task Manager suppression (remove from screen) enabled successfully.");
            }
            else if (result == ExitCodePermissionDenied)
            {
                Console.WriteLine("! WARNING: SUPPRESSION FAILED. The required administrator credentials were not provided or were denied.");
            }
            else
            {
                Console.WriteLine("! ERROR: FAILED to suppress Task Manager. An unexpected internal error occurred.");
            }
        }
        else
        {
            Console.WriteLine("Change canceled.");
        }
        
        Console.WriteLine("\nPress any key to exit.");
        Console.ReadKey(true);
        Console.WriteLine(); // ADDED: Ensure prompt returns on a new line
    }


    // --- REGISTRY HELPERS ---
    private static string GetUserShellRegistryValue()
    {
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(WinlogonRegistryKeyPath))
        {
            if (key != null)
            {
                string? shellValue = key.GetValue("Shell") as string;
                return shellValue ?? string.Empty;
            }
            return string.Empty;
        }
    }

    private static void SetUserShellRegistryValue(string value)
    {
        RegistryKey? key = Registry.CurrentUser.OpenSubKey(WinlogonRegistryKeyPath, true);
        if (key == null)
        {
            key = Registry.CurrentUser.CreateSubKey(WinlogonRegistryKeyPath, true);
        }

        if (key != null)
        {
            key.SetValue("Shell", value, RegistryValueKind.String);
            key.Close();
        }
        else
        {
            throw new Exception("Could not open or create the Winlogon registry key.");
        }
    }
}
