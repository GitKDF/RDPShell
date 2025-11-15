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

public class RDPShell
{
    // DEBUG CONTROL FLAG: Set to 'true' to enable all file logging across the application.
    private const bool DEBUG_ENABLED = false;
    
    // --- NATIVE IMPORTS (P/Invoke) ---

    // User32 imports for Keyboard state and window manipulation
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Synchronous key state check (used for startup detection)
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);

    // User32 imports for window enumeration
    [DllImport("user32.dll")]
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

    // Delegate for the EnumWindows callback
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // --- NATIVE IMPORTS for Console Allocation ---
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();


    // --- CONSTANTS AND CONFIGURATION ---
    private const int VK_LWIN = 0x5B; // Left Windows Key
    private const int VK_RWIN = 0x5C; // Right Windows Key
    private const uint WM_CLOSE = 0x0010;

    // Console constant
    private const int ATTACH_PARENT_PROCESS = -1;

    // Constants for the persistent key check
    private const int INITIAL_DELAY_MS = 500; // Single delay before polling
    private const short KEY_DOWN_BIT = unchecked((short)0x8000);
    private const short KEY_PRESSED_BIT = 0x0001;
    private const short KEY_CHECK_MASK = KEY_DOWN_BIT | KEY_PRESSED_BIT;

    // MessageBox constants
    private const uint MB_OK = 0x00000000;
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_ICONINFORMATION = 0x00000040;

    // App constants
    private const string AppName = "RDPShell";
    private const string ShellFlag = "-shell";
    // FLAG: Used only to trigger the UAC prompt
    private const string AdminCheckFlag = "-admincheck";

    // Per-user registry path for the shell override
    private const string RegistryKeyPath = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private static readonly string UserProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string InstallFolderPath = Path.Combine(UserProfilePath, AppName);

    // Log file path in the installation directory
    private static readonly string LogFilePath = Path.Combine(InstallFolderPath, "RDPShell.log");

    private static readonly string TargetExePath = Path.Combine(InstallFolderPath, AppName + ".exe");
    private static readonly string ReadmePath = Path.Combine(InstallFolderPath, "readme.txt");
    private const string DefaultShell = "explorer.exe";
    // Exact title of the RDP dialog that often blocks logoff when disconnected/locked.
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

    // --- STATE MANAGEMENT ---
    // Store the process launched in RDP mode so the event handler can access it
    private static Process? RDPSubShellProcess;
    // Used to manage the asynchronous cleanup loop
    private static CancellationTokenSource? CleanupCts;

    // --- LOGGING HELPER ---
    private static void LogDebugMessage(string message)
    {
        // Check the control flag before writing the message
        if (!DEBUG_ENABLED) return;

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
    // Initiates logoff without causing a console window flash by setting ProcessStartInfo properties.
    private static void InitiateLogoff()
    {
        LogDebugMessage("Starting silent logoff (shutdown.exe /l /f).");
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/l /f",
                // CRITICAL: Prevent console window from appearing
                CreateNoWindow = true, 
                // CRITICAL: Required for CreateNoWindow=true to work reliably on console executables
                UseShellExecute = false 
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            LogDebugMessage($"ERROR: Failed to initiate logoff silently: {ex.Message}. Falling back to non-silent.");
            // Fallback to the non-silent method if the silent one fails (still better than failing to logoff)
            Process.Start("shutdown.exe", "/l /f");
        }
    }

    // --- UTILITY FUNCTIONS ---

    private static string GetWindowClassName(IntPtr hWnd)
    {
        StringBuilder className = new StringBuilder(256);
        GetClassName(hWnd, className, className.Capacity);
        return className.ToString();
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

    // --- CONSOLE MANAGEMENT ---
    private static void EnsureConsoleAttachedOrAllocated()
    {
        // 1. Try to attach to the console of the process that launched us (if any).
        if (AttachConsole(ATTACH_PARENT_PROCESS))
        {
            LogDebugMessage("Successfully attached to the parent console.");
            return;
        }

        // 2. If step 1 fails (e.g., the app was double-clicked from Explorer),
        // allocate a new console for user interaction.
        if (AllocConsole())
        {
            // Re-route the console standard streams to the newly allocated console.
            try
            {
                // Standard Output Stream
                TextWriter tw = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true };
                Console.SetOut(tw);

                // Standard Input Stream
                TextReader tr = new StreamReader(Console.OpenStandardInput(), Console.InputEncoding);
                Console.SetIn(tr);

                LogDebugMessage("Successfully allocated a new console and redirected streams.");
            }
            catch (Exception ex)
            {
                LogDebugMessage($"Failed to redirect console streams: {ex.Message}");
                ShowMessageBox(
                    "Could not initialize console for installer mode.",
                    AppName,
                    MB_ICONERROR | MB_OK
                );
            }
        }
        else
        {
            LogDebugMessage("Failed to allocate or attach to any console.");
        }
    }


    // --- MAIN ENTRY POINT ---
    [STAThread] // CRITICAL FIX: Ensures the main thread runs in STA model for UAC/shell compatibility
    public static void Main(string[] args)
    {
        try
        {
            LogDebugMessage($"Application started. Arguments: {string.Join(" ", args)}");

            bool isShellMode = args.Length > 0 && args[0].Equals(ShellFlag, StringComparison.OrdinalIgnoreCase);
            bool isAdminCheckMode = args.Length > 0 && args[0].Equals(AdminCheckFlag, StringComparison.OrdinalIgnoreCase);

            if (isShellMode)
            {
                RunAsShell();
            }
            else if (isAdminCheckMode)
            {
                LogDebugMessage("AdminCheck mode triggered. Exiting successfully.");
                return;
            }
            else
            {
                // Installer/Uninstaller Mode: Requires a console for user interaction
                EnsureConsoleAttachedOrAllocated();

                CheckAndManageInstallation();

                // Clean up the console if one was allocated (optional, as the process exits)
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
            Environment.Exit(1);
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

                if (AttemptAdminCredentialCheck())
                {
                    LogDebugMessage($"Admin check passed. Launching {DefaultShell}.");
                    subShellProcess = Process.Start(DefaultShell);
                }
                else
                {
                    LogDebugMessage("Admin check failed or canceled. Logging off.");
                    ShowMessageBox(
                        "You must have administrative privileges to access the local desktop on this account.",
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
                    LogDebugMessage($"Found RDP file(s). Using: {rdpFiles[0]}. Launching mstsc.exe.");

                    if (rdpFiles.Length > 1)
                    {
                        LogDebugMessage($"Found multiple RDP files. Using the first one: {Path.GetFileName(rdpFiles[0])}.");
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
            // Use silent logoff helper
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

        Environment.Exit(0);
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
            // Add a short delay before launching the elevated process.
            //LogDebugMessage("UAC Check: Delaying 1000ms to stabilize environment before launching UAC check.");
            //Thread.Sleep(1000);
            
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


    // --- INSTALLER/UNINSTALLER LOGIC (Converted to Console I/O) ---
    private static void CheckAndManageInstallation()
    {
        // NOTE: This function now relies on EnsureConsoleAttachedOrAllocated() being called 
        // prior to entry, so Console I/O is expected to work.
        LogDebugMessage("Entering CheckAndManageInstallation mode (Console I/O).");

        string currentShellValue = GetUserShellRegistryValue();
        string requiredShellValue = $"\"{TargetExePath}\" {ShellFlag}";

        if (currentShellValue.Equals(requiredShellValue, StringComparison.OrdinalIgnoreCase))
        {
            LogDebugMessage("Installation detected. Prompting for uninstall.");

            Console.WriteLine($"========================================================================");
            Console.WriteLine($" {AppName} Shell Detected");
            Console.WriteLine($"========================================================================");
            Console.WriteLine($"The {AppName} shell is currently installed for user {Environment.UserName}.");
            Console.Write($"Would you like to UNINSTALL {AppName} and revert to the default shell? (Press y, or any other key to exit): ");

            char input = Console.ReadKey(true).KeyChar;

            if (char.ToLower(input) == 'y')
            {
                UninstallShell();
            }
            else
            {
                Console.WriteLine("\nUninstallation canceled. Press any key to exit.");
                Console.ReadKey(true);
            }
            return;
        }

        LogDebugMessage("Installation not detected. Prompting for install.");

        Console.WriteLine($"========================================================================");
        Console.WriteLine($" {AppName} Shell Not Installed");
        Console.WriteLine($"========================================================================");
        Console.WriteLine($"The {AppName} shell is currently NOT installed for user {Environment.UserName}.");
        Console.Write($"Would you like to INSTALL {AppName} as your default shell? (Press y, or any other key to exit): ");

        char installInput = Console.ReadKey(true).KeyChar;

        if (char.ToLower(installInput) == 'y')
        {
            InstallShell(requiredShellValue);
        }
        else
        {
            Console.WriteLine("\nInstallation canceled. Press any key to exit.");
            Console.ReadKey(true);
        }
    }


    private static void InstallShell(string requiredShellValue)
    {
        try
        {
            LogDebugMessage("Starting installation process.");

            string currentExePath = Path.Combine(AppContext.BaseDirectory, AppName + ".exe");

            Console.WriteLine("\n--- Installing ---");

            if (!Directory.Exists(InstallFolderPath))
            {
                LogDebugMessage($"Creating install directory: {InstallFolderPath}");
                Console.WriteLine($"1. Creating install directory: {InstallFolderPath}");
                Directory.CreateDirectory(InstallFolderPath);
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

                try
                {
                    File.Copy(currentExePath, TargetExePath, true);
                }
                catch (Exception copyEx)
                {
                    LogDebugMessage($"Error during file copy: {copyEx.Message}");
                    Console.WriteLine($"    ! WARNING: Failed to copy executable: {copyEx.Message}");
                    Console.WriteLine("    ! The shell may not launch correctly if the current path is removed.");
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
            LogDebugMessage("Setting registry Shell value.");
            Console.WriteLine("4. Setting Windows registry key...");
            SetUserShellRegistryValue(requiredShellValue);

            Thread.Sleep(500);
            Console.WriteLine("\n--- Installation Successful ---");
            Console.WriteLine($"{AppName} is successfully installed as your shell.");
            Console.WriteLine("The new shell will take effect on your next login.");
            Console.WriteLine($"\nReadme file created at: {ReadmePath}");

            Console.Write($"Would you like to view the readme now? (Press y, or any other key to exit): ");

            char readmeInput = Console.ReadKey(true).KeyChar;

            if (char.ToLower(readmeInput) == 'y')
            {
                // Show Readme inline
                Console.Clear();
                Console.WriteLine(ReadmeFileText);
                Console.WriteLine("\nPress any key to exit.");
                Console.ReadKey(true);
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
        }
    }

    private static void UninstallShell()
    {
        try
        {
            LogDebugMessage("Starting uninstallation process.");
            Console.WriteLine("\n--- Uninstalling ---");

            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true))
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
                    Console.WriteLine("3. Could not delete Log file.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("3. Log file could not delete.");
                    LogDebugMessage($"Failed to delete Log file: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("3. Log file not found.");
            }

            LogDebugMessage("Uninstallation complete.");

            Console.WriteLine("\n--- Uninstallation Complete ---");
            Console.WriteLine($"{AppName} has been uninstalled. The shell has been reverted to the default ({DefaultShell}).");
            Console.WriteLine("Please log off and log back on for changes to take effect.");
            Console.WriteLine("\nPress any key to exit.");
            Console.ReadKey(true);
        }
        catch (Exception ex)
        {
            LogDebugMessage($"UNINSTALLATION FAILED: {ex.Message}");
            Console.WriteLine("\n--- Uninstallation FAILED ---");
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine("\nPress any key to exit.");
            Console.ReadKey(true);
        }
    }


    // --- REGISTRY HELPERS ---
    private static string GetUserShellRegistryValue()
    {
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
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
        RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
        if (key == null)
        {
            key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath, true);
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
