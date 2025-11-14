// RDPShell.cs
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices; // Required for P/Invoke (DllImport, Marshal, GCHandle)
using System.ComponentModel; // Required for Win32Exception
using System.Text; // Required for StringBuilder

// NOTE: This program requires the System.Windows.Forms assembly reference for MessageBox.
// To compile as a non-console application (so no console window appears):
// csc /target:winexe /reference:System.Windows.Forms.dll RDPShell.cs

public class RDPShell
{
    // --- NATIVE IMPORTS (P/Invoke) ---
    
    // User32 imports for Keyboard state and window manipulation
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

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

    // Delegate for the EnumWindows callback
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // Wtsapi32 imports for session information
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, 
        int SessionId, 
        WTS_INFO_CLASS WTSInfoClass, 
        out IntPtr ppBuffer, 
        out int pBytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    // Enums for WTSQuerySessionInformation
    private enum WTS_INFO_CLASS
    {
        WTSInitialProgram,
        WTSApplicationName,
        WTSWorkingDirectory,
        WTSOEMId,
        WTSSessionId,
        WTSUserName,
        WTSWinStationName,
        WTSConnectState, // We need this one
        WTSClientBuildNumber
    }

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,              // The session is active (logged in, unlocked).
        WTSConnected,           // The session is connected.
        WTSConnectQuery,        
        WTSShadow,              
        WTSDisconnected,        // The session is disconnected (often reported when locked locally).
        WTSIdle,                
        WTSListen,              
        WTSReset,               
        WTSDown,                
        WTSInit                 
    }
    
    // Virtual Key Codes (VKey) and Windows Messages
    private const int VK_CONTROL = 0x11; 
    private const uint WM_CLOSE = 0x0010;
    // Standard Windows Dialog Class Name
    private const string StandardDialogClassName = "#32770"; 

    private static bool IsControlKeyDown()
    {
        // The high-order bit (0x8000) is set if the key is currently down.
        return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
    }

    // Helper function to check if the current user session is locked.
    private static bool IsSessionLocked()
    {
        IntPtr pBuffer = IntPtr.Zero;
        int bytesReturned;
        int sessionId = Process.GetCurrentProcess().SessionId;

        // Query the session connection state for the current session ID
        if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTS_INFO_CLASS.WTSConnectState, out pBuffer, out bytesReturned) && bytesReturned > 0)
        {
            try
            {
                // WTS_CONNECTSTATE_CLASS is an int (4 bytes)
                WTS_CONNECTSTATE_CLASS state = (WTS_CONNECTSTATE_CLASS)Marshal.ReadInt32(pBuffer);
                
                // When a local console session is locked (Win+L), it often reports as WTSDisconnected.
                return state == WTS_CONNECTSTATE_CLASS.WTSDisconnected;
            }
            finally
            {
                WTSFreeMemory(pBuffer);
            }
        }
        return false;
    }


    // --- CONSTANTS AND CONFIGURATION ---
    private const string AppName = "RDPShell";
    private const string ShellFlag = "-shell";
    // Per-user registry path for the shell override
    private const string RegistryKeyPath = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon"; 
    private static readonly string UserProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string InstallFolderPath = Path.Combine(UserProfilePath, AppName);
    private static readonly string TargetExePath = Path.Combine(InstallFolderPath, AppName + ".exe");
    private static readonly string ReadmePath = Path.Combine(InstallFolderPath, "readme.txt");
    private const string DefaultShell = "explorer.exe";
    private const string RDPDisconnectionDialogTitle = "Remote Desktop Connection"; 

    
    // Multi-line text for the Readme file
    private const string ReadmeFileText = 
$@"--- {AppName} Readme ---
This utility has been installed as your custom Windows Shell.

Install Path: {InstallFolderPath}
User: {Environment.UserName}

Primary Function (On Login):
1. The program checks if the Control (Ctrl) key is being held down.
2. If Ctrl is held: It requires administrative credentials. If accepted, it launches 
   the default Windows shell ({DefaultShell}). If canceled, it logs off.
3. If Ctrl is NOT held: It searches for an RDP file named 'RDPShell - *.rdp' 
   in the install folder and launches the Remote Desktop Client (mstsc.exe) 
   using that file. If no RDP file is found, it logs off.

To Uninstall:
Simply run the '{AppName}.exe' file from any location (e.g., double-click it).
The program will detect the installation and prompt you for uninstallation.
Note: You must log off and log back in for changes to the shell to take effect.";
    

    // --- MAIN ENTRY POINT ---
    [STAThread] // Required for System.Windows.Forms.MessageBox
    public static void Main(string[] args)
    {
        // Check if the application is running in 'Shell Mode' via the flag
        if (args.Length > 0 && args[0].Equals(ShellFlag, StringComparison.OrdinalIgnoreCase))
        {
            // PART 1: SHELL MODE (Launched by Winlogon)
            RunAsShell();
        }
        else
        {
            // PART 2: INSTALLER/UNINSTALLER MODE (Interactive)
            CheckAndManageInstallation();
        }
    }

    // --- SHELL MODE LOGIC ---
    private static void RunAsShell()
    {
        Process subShellProcess = null;
        bool rdpMode = false; // Flag to track if we launched mstsc.exe

        try
        {
            // 1. Conditional Launch based on Ctrl key state
            if (IsControlKeyDown())
            {
                // Ctrl is pressed - force credential check before launching desktop
                MessageBox.Show(
                    "Attempting to launch the desktop environment. You must provide administrative credentials to proceed.", 
                    AppName, 
                    MessageBoxButtons.OK, 
                    MessageBoxIcon.Information
                );

                if (AttemptAdminCredentialCheck())
                {
                    // Credentials provided successfully (UAC accepted)
                    subShellProcess = Process.Start(DefaultShell);
                }
                else
                {
                    // Credentials check failed (UAC cancelled or failure)
                    MessageBox.Show("Administrative credential check failed or was canceled. Exiting user session now.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Stop);
                }
            }
            else
            {
                // Ctrl is not pressed (RDP Mode)
                string[] rdpFiles = Directory.GetFiles(InstallFolderPath, "RDPShell - *.rdp", SearchOption.TopDirectoryOnly);

                if (rdpFiles.Length == 1)
                {
                    // Found exactly one RDP file
                    subShellProcess = LaunchRDP(rdpFiles[0]);
                    rdpMode = true;
                }
                else if (rdpFiles.Length > 1)
                {
                    // Found multiple RDP files (use the first one and warn)
                    MessageBox.Show(
                        $"Multiple RDP files found. Using the first one: {Path.GetFileName(rdpFiles[0])}. Launching mstsc.exe...",
                        AppName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                    subShellProcess = LaunchRDP(rdpFiles[0]);
                    rdpMode = true;
                }
                else
                {
                    // No RDP file found - Log off immediately (per user request)
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
            // Catch errors during launch (e.g., file not found, permission issues)
            MessageBox.Show($"Critical Error during sub-shell launch: {ex.Message}. Logging off now.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Process.Start("shutdown.exe", "/l /f"); // Force logoff
            return; 
        }

        // 2. Monitor the Subshell Process (Only if a process was started)
        if (subShellProcess != null)
        {
            // Use an active monitoring loop instead of blocking WaitForExit()
            while (true)
            {
                // Check if the process has exited
                try
                {
                    if (subShellProcess.HasExited) break;
                }
                catch (InvalidOperationException)
                {
                    // Catch exception if the process exits between checking HasExited and the loop start
                    break;
                }

                // If in RDP mode and the session is locked, actively close any blocking dialog windows.
                if (rdpMode && IsSessionLocked())
                {
                    CloseBlockingWindows(subShellProcess);
                }
                
                // Wait briefly to avoid high CPU usage
                Thread.Sleep(200);
            }
        }
        
        // 3. Exit the shell process. This signals the OS that the user session is over.
        Application.Exit();
    }
    
    // --- RDP WINDOW CLEANUP LOGIC ---

    // Static callback used by EnumWindows to check if a window belongs to our RDP process 
    // AND if it is a standard Windows dialog box (#32770).
    private static bool EnumWindowCallback(IntPtr hWnd, IntPtr lParam)
    {
        uint ownerProcessId = (uint)GCHandle.FromIntPtr(lParam).Target;
        uint windowProcessId;
        
        // Get the process ID of the window
        GetWindowThreadProcessId(hWnd, out windowProcessId);
        
        if (windowProcessId == ownerProcessId)
        {
            // Window is owned by mstsc.exe. Now check if it's a dialog.
            StringBuilder className = new StringBuilder(256);
            GetClassName(hWnd, className, className.Capacity);

            if (className.ToString() == StandardDialogClassName)
            {
                // Found a window owned by mstsc.exe with the standard dialog class name. Close it.
                PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            
                // Return false to stop enumeration after closing one, as this should unblock mstsc.exe.
                return false;
            }
        }

        // Continue enumeration
        return true;
    }
    
    // Looks for any standard dialog box (#32770) owned by the target process and closes it.
    private static void CloseBlockingWindows(Process subShellProcess)
    {
        if (subShellProcess == null) return;
        
        try
        {
            // Use a GCHandle to safely pass the process ID to the static callback method
            GCHandle gch = GCHandle.Alloc((uint)subShellProcess.Id);
            
            try
            {
                // Enumerate all top-level windows
                EnumWindows(EnumWindowCallback, GCHandle.ToIntPtr(gch));
            }
            finally
            {
                if (gch.IsAllocated)
                {
                    gch.Free();
                }
            }
        }
        catch (Exception ex)
        {
            // Log or handle P/Invoke errors during cleanup
            Debug.WriteLine($"Error during window cleanup: {ex.Message}");
        }
    }

    // --- CREDENTIAL CHECK LOGIC ---

    // Helper function to force a UAC prompt and check if elevation was accepted.
    private static bool AttemptAdminCredentialCheck()
    {
        // We use cmd.exe as a harmless utility to launch elevated.
        ProcessStartInfo psi = new ProcessStartInfo("cmd.exe");
        psi.UseShellExecute = true; 
        psi.Verb = "runas"; // THIS triggers the UAC prompt

        try
        {
            // Start the elevated process (UAC succeeds)
            Process tempProcess = Process.Start(psi);
            
            // Immediately close the elevated process we just started, 
            // as its only purpose was to check credentials.
            if (tempProcess != null)
            {
                // Give it a tiny moment to start before killing it.
                Thread.Sleep(100); 
                if (!tempProcess.HasExited)
                {
                    tempProcess.Kill();
                }
                tempProcess.Dispose();
            }
            return true; // UAC accepted, process started
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 (The operation was canceled by the user) is the standard
            // error code when the user clicks 'No' or 'Cancel' on the UAC prompt.
            return false; // UAC was canceled
        }
        catch (Exception ex)
        {
            // Catch other unexpected errors during the check
            MessageBox.Show($"Unexpected error during credential check: {ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
    
    // Helper to launch the RDP client securely
    private static Process LaunchRDP(string rdpFilePath)
    {
        // Use ProcessStartInfo to specify mstsc.exe and the arguments
        ProcessStartInfo psi = new ProcessStartInfo("mstsc.exe", $"\"{rdpFilePath}\"");
        psi.UseShellExecute = true; // Use shell execution for system commands like mstsc
        return Process.Start(psi);
    }


    // --- INSTALLER/UNINSTALLER LOGIC ---
    private static void CheckAndManageInstallation()
    {
        string currentShellValue = GetUserShellRegistryValue();
        string requiredShellValue = $"\"{TargetExePath}\" {ShellFlag}";
        
        // 1. CHECK FOR UNINSTALL
        if (currentShellValue.Equals(requiredShellValue, StringComparison.OrdinalIgnoreCase))
        {
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

        // 2. CHECK FOR INSTALL
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


    // --- INSTALLATION PROCESS ---
    private static void InstallShell(string requiredShellValue)
    {
        try
        {
            string currentExePath = Assembly.GetExecutingAssembly().Location;
            
            // 2a. Ensure the installation folder exists
            if (!Directory.Exists(InstallFolderPath))
            {
                Directory.CreateDirectory(InstallFolderPath);
            }

            // 2b. Copy the executable file if not running from the target path
            if (!currentExePath.Equals(TargetExePath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    $"The program will now copy the executable to the install folder: {InstallFolderPath}.",
                    $"{AppName} Install Copy Required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                
                File.Copy(currentExePath, TargetExePath, true);
            }

            // 2c. Write the README file
            File.WriteAllText(ReadmePath, ReadmeFileText);

            // 2d. Edit the Registry
            SetUserShellRegistryValue(requiredShellValue);

            // 2e. Show successful install message
            DialogResult viewReadme = MessageBox.Show(
                $"{AppName} installation successful! The new shell will take effect on your next login. \n\n" +
                $"Readme file created at: {ReadmePath}\n\n" +
                "Would you like to view the Readme file now?",
                $"{AppName} Installation Complete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information
            );

            // 2f. Launch Readme if requested
            if (viewReadme == DialogResult.Yes)
            {
                Process.Start(ReadmePath);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Installation FAILED: {ex.Message}", $"{AppName} Installation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // --- UNINSTALLATION PROCESS ---
    private static void UninstallShell()
    {
        try
        {
            // Revert the shell setting (delete the per-user key)
            RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            if (key != null)
            {
                // Deleting the "Shell" value in HKCU reverts to the HKLM shell (which is usually explorer.exe)
                key.DeleteValue("Shell", false);
                key.Close();

                MessageBox.Show(
                    $"{AppName} uninstallation successful! The shell has been reverted to the default ({DefaultShell}). Please log off and log back on for changes to take effect.",
                    $"{AppName} Uninstallation Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            
            try
            {
                if (File.Exists(ReadmePath))
                {
                    File.Delete(ReadmePath);
                }
            }
            catch (Exception)
            {
                // Ignore file cleanup errors
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Uninstallation FAILED: {ex.Message}", $"{AppName} Uninstallation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }


    // --- REGISTRY HELPERS ---
    private static string GetUserShellRegistryValue()
    {
        RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
        if (key != null)
        {
            string shellValue = key.GetValue("Shell") as string;
            key.Close();
            return shellValue ?? string.Empty;
        }
        return string.Empty;
    }

    private static void SetUserShellRegistryValue(string value)
    {
        // Open the key with write permissions
        RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
        if (key == null)
        {
            // If the key doesn't exist, create it (shouldn't happen on modern Windows)
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
