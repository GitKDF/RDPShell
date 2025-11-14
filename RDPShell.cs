// RDPShell.cs
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks; // New import for async task management
using System.Windows.Forms;
using Microsoft.Win32; // Used for SessionSwitch events and Registry
using System.Diagnostics; 
using System.Runtime.InteropServices; 
using System.ComponentModel; 
using System.Text; 

// NOTE: This program requires the System.Windows.Forms assembly reference for MessageBox.
// To compile as a non-console application (so no console window appears):
// csc /target:winexe /reference:System.Windows.Forms.dll RDPShell.cs

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

    // --- CONSTANTS AND CONFIGURATION ---
    private const string AppName = "RDPShell";
    private const string ShellFlag = "-shell";
    // FLAG: Used only to trigger the UAC prompt
    private const string AdminCheckFlag = "-admincheck"; 
    
    // DEBUG CONTROL FLAG: Set to 'false' to disable all file logging across the application.
    private const bool DEBUG_ENABLED = true;
    
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
    [STAThread] // Required for System.Windows.Forms.MessageBox
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
            MessageBox.Show($"FATAL ERROR: An unhandled exception occurred.\n\nDetails written to {LogFilePath}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                // ... (Win Key logic remains the same)
                LogDebugMessage("Windows key detected. Attempting admin credential check.");
                
                MessageBox.Show(
                    "Attempting to launch the desktop environment. You must provide administrative credentials to proceed.", 
                    AppName, 
                    MessageBoxButtons.OK, 
                    MessageBoxIcon.Information
                );

                if (AttemptAdminCredentialCheck())
                {
                    LogDebugMessage($"Admin check passed. Launching {DefaultShell}.");
                    subShellProcess = Process.Start(DefaultShell);
                }
                else
                {
                    LogDebugMessage("Admin check failed or canceled. Logging off.");
                    MessageBox.Show("Administrative credential check failed or was canceled. Exiting user session now.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Stop);
                }
            }
            else
            {
                // Win Key is not pressed (RDP Mode)
                LogDebugMessage("Windows key not detected. Attempting RDP mode.");
                
                string[] rdpFiles = Directory.GetFiles(InstallFolderPath, "RDPShell - *.rdp", SearchOption.TopDirectoryOnly);

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
                    MessageBox.Show(
                        $"No RDP file found in '{InstallFolderPath}' matching 'RDPShell - *.rdp'. Exiting user session now.",
                        AppName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }
            }
        }
        catch (Exception ex)
        {
            LogDebugMessage($"CRITICAL ERROR during shell launch: {ex.Message}");
            MessageBox.Show($"Critical Error during sub-shell launch: {ex.Message}. Logging off now.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                RDPSubShellProcess.Dispose();
                RDPSubShellProcess = null;
                LogDebugMessage("SessionSwitch event listener unregistered.");
            }
        }
        
        // 3. Exit the shell process.
        LogDebugMessage("Subshell exited. Initiating session logoff.");
        Process.Start("shutdown.exe", "/l /f");
        
        Application.Exit();
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
            CleanupCts?.Cancel();
            // Note: The task itself will exit gracefully upon cancellation.
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

    // Renamed the function to take the process ID directly
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

            // Added detailed logging to debug title matching
            LogDebugMessage($"[Enum] Found RDP Process Window (PID: {windowProcessId}). Title: '{windowTitle}'");

            if (windowTitle.ToString() == RDPDisconnectionDialogTitle)
            {
                LogDebugMessage($"[Enum] MATCHED blocking RDP window. Title: '{windowTitle}' - Closing.");
                PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return false;
            }
        }
        else
        {
            LogDebugMessage($"Window does not belong to RDP Process ({windowProcessId} =/= {ownerProcessId})");
        }
        
        return true;
    }


    // --- CREDENTIAL CHECK LOGIC ---

    // Helper function to force a UAC prompt and check if elevation was accepted.
    private static bool AttemptAdminCredentialCheck()
    {
        // Use the application's own executable path and the new flag
        ProcessStartInfo psi = new ProcessStartInfo(TargetExePath, AdminCheckFlag);
        psi.UseShellExecute = true; 
        psi.Verb = "runas"; // Requests elevation

        try
        {
            // Start the process, which will trigger the UAC prompt
            Process tempProcess = Process.Start(psi);
            
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
            MessageBox.Show($"Unexpected error during credential check: {ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
    // (Omitted for brevity, logic remains the same)
    private static void CheckAndManageInstallation()
    {
        LogDebugMessage("Entering CheckAndManageInstallation mode.");
        
        string currentShellValue = GetUserShellRegistryValue();
        string requiredShellValue = $"\"{TargetExePath}\" {ShellFlag}";
        
        if (currentShellValue.Equals(requiredShellValue, StringComparison.OrdinalIgnoreCase))
        {
            LogDebugMessage("Installation detected. Prompting for uninstall.");
            
            DialogResult result = MessageBox.Show(
                $"The {AppName} shell is currently installed for user {Environment.UserName}. Would you like to UNINSTALL {AppName} and revert to the default shell?",
                $"{AppName} Uninstall Detected",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                UninstallShell();
            }
            return; 
        }

        LogDebugMessage("Installation not detected. Prompting for install.");
        
        DialogResult installResult = MessageBox.Show(
            $"The {AppName} shell is currently NOT installed for user {Environment.UserName}. Would you like to INSTALL {AppName} as your default shell?",
            $"{AppName} Install Prompt",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question
        );

        if (installResult == DialogResult.Yes)
        {
            InstallShell(requiredShellValue);
        }
    }


    private static void InstallShell(string requiredShellValue)
    {
        try
            {
            LogDebugMessage("Starting installation process.");
            
            string currentExePath = Path.Combine(AppContext.BaseDirectory, AppName + ".exe"); 
            
            if (!Directory.Exists(InstallFolderPath))
            {
                LogDebugMessage($"Creating install directory: {InstallFolderPath}");
                Directory.CreateDirectory(InstallFolderPath);
            }

            if (!currentExePath.Equals(TargetExePath, StringComparison.OrdinalIgnoreCase))
            {
                LogDebugMessage($"Copying executable from {currentExePath} to {TargetExePath}.");
                MessageBox.Show(
                    $"The program will now copy the executable to the install folder: {InstallFolderPath}.",
                    $"{AppName} Install Copy Required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                
                File.Copy(currentExePath, TargetExePath, true);
            }

            LogDebugMessage($"Writing Readme to {ReadmePath}.");
            File.WriteAllText(ReadmePath, ReadmeFileText);

            LogDebugMessage("Setting registry Shell value.");
            SetUserShellRegistryValue(requiredShellValue);

            LogDebugMessage("Installation successful.");
            DialogResult viewReadme = MessageBox.Show(
                $"{AppName} installation successful! The new shell will take effect on your next login. \n\n" +
                $"Readme file created at: {ReadmePath}\n\n" +
                "Would you like to view the Readme file now?",
                $"{AppName} Installation Complete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information
            );

            if (viewReadme == DialogResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(ReadmePath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    LogDebugMessage($"Failed to launch Readme file: {ex.Message}");
                    MessageBox.Show($"Warning: Failed to automatically open the Readme file. You can find it at: {ReadmePath}", $"{AppName} Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            LogDebugMessage($"INSTALLATION FAILED: {ex.Message}");
            MessageBox.Show($"Installation FAILED: {ex.Message}", $"{AppName} Installation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void UninstallShell()
    {
        try
        {
            LogDebugMessage("Starting uninstallation process.");
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true))
            {
                if (key != null)
                {
                    key.DeleteValue("Shell", false);
                    
                    MessageBox.Show(
                        $"{AppName} uninstallation successful! The shell has been reverted to the default ({DefaultShell}). Please log off and log back on for changes to take effect.",
                        $"{AppName} Uninstallation Complete",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
            }
            
            try
            {
                if (File.Exists(LogFilePath))
                {
                    File.Delete(LogFilePath);
                }
                if (File.Exists(ReadmePath))
                {
                    File.Delete(ReadmePath);
                }
            }
            catch (Exception)
            {
            }
            LogDebugMessage("Uninstallation complete.");
        }
        catch (Exception ex)
        {
            LogDebugMessage($"UNINSTALLATION FAILED: {ex.Message}");
            MessageBox.Show($"Uninstallation FAILED: {ex.Message}", $"{AppName} Uninstallation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
