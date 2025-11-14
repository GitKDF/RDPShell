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
    // --- NATIVE IMPORTS (P/Invoke) ---
    
    // User32 imports for Keyboard state and window manipulation
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey); 
    
    // Synchronous key state check (used for startup detection)
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey); 

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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
    
    // Virtual Key Codes (VKey) and Windows Messages
    private const int VK_LWIN = 0x5B; // Left Windows Key
    private const int VK_RWIN = 0x5C; // Right Windows Key
    private const uint WM_CLOSE = 0x0010;

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
    
    // MessageBox return codes
    private const int IDOK = 1;
    private const int IDYES = 6;
    private const int IDNO = 7;
    private const int IDCANCEL = 2; // Not used but good to have

    // --- CONSTANTS AND CONFIGURATION ---
    private const string AppName = "RDPShell";
    private const string ShellFlag = "-shell";
    // FLAG: Used only to trigger the UAC prompt
    private const string AdminCheckFlag = "-admincheck"; 
    
    // DEBUG CONTROL FLAG: Set to 'false' to disable all file logging across the application.
    private const bool DEBUG_ENABLED = false;
    
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
This utility has been installed as your custom Windows Shell.

Install Path: {InstallFolderPath}
User: {Environment.UserName}

Primary Function (On Login):
1. The program checks if the **Windows Key** is being held down or was pressed during the login sequence.
2. If the Windows Key is held/pressed: It requires administrative credentials. If accepted, it launches 
   the default Windows shell ({DefaultShell}). If canceled, it logs off.
3. If the Windows Key is NOT held/pressed: It searches for an RDP file named 'RDPShell*.rdp' 
   in the install folder and launches the Remote Desktop Client (mstsc.exe) 
   using that file. If no RDP file is found, it logs off.

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
    
    // --- MAIN ENTRY POINT ---
    public static void Main(string[] args)
    {
        try 
        {
            LogDebugMessage($"Application started. Arguments: {string.Join(" ", args)}");
            
            if (args.Length > 0 && args[0].Equals(ShellFlag, StringComparison.OrdinalIgnoreCase))
            {
                RunAsShell();
            }
            else if (args.Length > 0 && args[0].Equals(AdminCheckFlag, StringComparison.OrdinalIgnoreCase))
            {
                // New Mode: Immediately exit after being launched with runas verb (for UAC check)
                LogDebugMessage("AdminCheck mode triggered. Exiting successfully.");
                return;
            }
            else
            {
                CheckAndManageInstallation();
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
            Process.Start("shutdown.exe", "/l /f"); 
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
                LogDebugMessage("SessionSwitch event listener registered.");
            }

            // Simplified monitoring loop: block until the subshell process exits.
            try
            {
                subShellProcess.WaitForExit();
            }
            catch (InvalidOperationException)
            {
                // Process may have already exited and been disposed by an external event.
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
        Process.Start("shutdown.exe", "/l /f");
        
        Environment.Exit(0);  // Use Environment.Exit() now that Application.Exit() is unavailable
    }
    
    // --- ASYNCHRONOUS SESSION SWITCH HANDLER ---
    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Only interested if we are in RDP mode (RDPSubShellProcess is set)
        if (RDPSubShellProcess == null) return;

        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            LogDebugMessage("SessionSwitch: Workstation locked. Starting asynchronous cleanup loop.");
            
            // If a cleanup loop is already running, cancel and dispose of the old one first, just in case.
            CleanupCts?.Cancel();
            CleanupCts?.Dispose();

            CleanupCts = new CancellationTokenSource();
            
            // Start the polling loop on a background thread
            Task.Run(() => CleanupLoop(RDPSubShellProcess.Id, CleanupCts.Token));
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            LogDebugMessage("SessionSwitch: Workstation unlocked. Canceling asynchronous cleanup loop.");
            
            // Signal the background task to stop
            CleanupCts?.Cancel();   // The task itself will exit gracefully upon cancellation.
        }
    }

    // The polling loop for closing RDP dialogs when the system is locked.
    private static void CleanupLoop(int processId, CancellationToken token)
    {
        LogDebugMessage($"CleanupLoop started for PID: {processId}.");
        
        while (!token.IsCancellationRequested)
        {
            // Close any blocking windows found for the RDP process ID
            CloseBlockingWindowsById((uint)processId);
            
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
                LogDebugMessage($"CleanupLoop encountered unexpected error: {ex.Message}");
            }
        }
        
        LogDebugMessage("CleanupLoop stopped due to cancellation/unlock.");
    }
    
    // --- RDP WINDOW CLEANUP LOGIC ---
    private static void CloseBlockingWindowsById(uint subShellProcessId)
    {
        try
        {
            GCHandle gch = GCHandle.Alloc(subShellProcessId);
            
            try
            {
                // Enumerate all top-level windows
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
            Debug.WriteLine($"Error during window cleanup: {ex.Message}");
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

            if (windowTitle.ToString() == RDPDisconnectionDialogTitle)
            {
                LogDebugMessage($"[Enum] MATCHED blocking RDP window. Title: '{windowTitle}' - Initiating process kill for cleanup.");

                // AGGRESSIVE CLEANUP: Kill the subshell process to force immediate logoff
                if (RDPSubShellProcess != null)
                {
                    try
                    {
                        RDPSubShellProcess.Kill();
                        LogDebugMessage("RDP Subshell process killed successfully.");
                    }
                    catch (Exception ex)
                    {
                        // Process may already be exiting or access denied
                        LogDebugMessage($"Failed to kill RDP Subshell process: {ex.Message}");
                    }
                }
                
                // We stop enumeration after finding and attempting to kill the process.
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
            // Start the process, which will trigger the UAC prompt
            Process? tempProcess = Process.Start(psi);
            
            if (tempProcess != null)
            {
                // Wait for the tiny sub-process to exit. If UAC is accepted, it exits immediately.
                // If UAC is canceled, a Win32Exception (1223) is thrown, which we catch below.
                tempProcess.WaitForExit(); 
                tempProcess.Dispose();
            }
            // If we reach here, the process launched and exited successfully (UAC accepted).
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Error code 1223 means "The operation was canceled by the user." (UAC declined).
            return false;
        }
        catch (Exception ex)
        {
            // Use native message box for visibility in shell context
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
            Console.Write($"Would you like to UNINSTALL {AppName} and revert to the default shell? (press y, or any other key to cancel): ");
            
            string? input = Console.ReadLine()?.Trim().ToLower();

            if (input == "y")
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
        Console.Write($"Would you like to INSTALL {AppName} as your default shell? (press y, or any other key to cancel): ");

        string? installInput = Console.ReadLine()?.Trim().ToLower();

        if (installInput == "y")
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
                    Console.WriteLine($"   ! WARNING: Failed to copy executable: {copyEx.Message}");
                    Console.WriteLine("   ! The shell may not launch correctly if the current path is removed.");
                }
            }
            else
            {
                Console.WriteLine("2. Verifying current executable path.");
            }

            LogDebugMessage($"Writing Readme to {ReadmePath}.");
            Console.WriteLine($"3. Writing Readme file to: {ReadmePath}");
            File.WriteAllText(ReadmePath, ReadmeFileText);

            LogDebugMessage("Setting registry Shell value.");
            Console.WriteLine("4. Setting Windows registry key...");
            SetUserShellRegistryValue(requiredShellValue);

            Console.WriteLine("\n--- Installation Successful ---");
            Console.WriteLine($"{AppName} is successfully installed as your shell.");
            Console.WriteLine("The new shell will take effect on your next login.");
            Console.WriteLine($"Readme file created at: {ReadmePath}");

            // Show Readme inline
            Console.WriteLine("\n--- Readme Content ---");
            Console.WriteLine(ReadmeFileText);
            Console.WriteLine("----------------------\n");

            LogDebugMessage("Installation successful. Console displayed Readme.");
            
            Console.WriteLine("Press any key to exit.");
            Console.ReadKey(true);
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
            
            try
            {
                if (File.Exists(LogFilePath))
                {
                    File.Delete(LogFilePath);
                    Console.WriteLine("2. Log file deleted.");
                }
                if (File.Exists(ReadmePath))
                {
                    File.Delete(ReadmePath);
                    Console.WriteLine("3. Readme file deleted.");
                }
            }
            catch (Exception ex)
            {
                LogDebugMessage($"Failed to delete files during uninstall: {ex.Message}");
                Console.WriteLine($"   ! WARNING: Failed to delete log/readme files: {ex.Message}");
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
