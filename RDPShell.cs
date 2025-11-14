// RDPShell.cs
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices; // Required for P/Invoke (DllImport)

// NOTE: This program requires the System.Windows.Forms assembly reference for MessageBox.
// To compile as a non-console application (so no console window appears):
// csc /target:winexe /reference:System.Windows.Forms.dll RDPShell.cs

public class RDPShell
{
    // --- NATIVE IMPORTS (P/Invoke) ---
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Virtual Key Codes (VKey)
    // 0x11 is VK_CONTROL, used for detecting if the Ctrl key is down globally.
    private const int VK_CONTROL = 0x11; 

    private static bool IsControlKeyDown()
    {
        // The high-order bit (0x8000) is set if the key is currently down.
        // GetAsyncKeyState returns a short (Int16), so we check the high bit.
        return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
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
    
    // Multi-line text for the Readme file
    private const string ReadmeFileText = 
$@"--- {AppName} Readme ---
This utility has been installed as your custom Windows Shell.

Install Path: {InstallFolderPath}
User: {Environment.UserName}

Primary Function (On Login):
1. The program checks if the Control (Ctrl) key is being held down.
2. If Ctrl is held: It launches the default Windows shell ({DefaultShell}).
3. If Ctrl is NOT held: It searches for an RDP file named 'RDPShell - *.rdp' 
   in the install folder and launches the Remote Desktop Client (mstsc.exe) 
   using that file.

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

        try
        {
            // 1. Conditional Launch based on Ctrl key state
            if (IsControlKeyDown())
            {
                // Ctrl is pressed: launch the default shell (Explorer)
                MessageBox.Show("Ctrl key detected during login. Launching default shell: explorer.exe", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                subShellProcess = Process.Start(DefaultShell);
            }
            else
            {
                // Ctrl is not pressed: try to launch RDP session
                string[] rdpFiles = Directory.GetFiles(InstallFolderPath, "RDPShell - *.rdp", SearchOption.TopDirectoryOnly);

                if (rdpFiles.Length == 1)
                {
                    // Found exactly one RDP file
                    // MessageBox.Show($"Ctrl key not detected. Launching RDP session using: {Path.GetFileName(rdpFiles[0])}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    subShellProcess = LaunchRDP(rdpFiles[0]);
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
                }
                else
                {
                    // No RDP file found (launch Explorer as a safe fallback)
                    MessageBox.Show(
                        $"Ctrl key not detected, but no RDP file found in '{InstallFolderPath}' matching 'RDPShell - *.rdp'. Launching {DefaultShell} as fallback.",
                        AppName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                    subShellProcess = Process.Start(DefaultShell);
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

        // 2. Monitor the Subshell Process
        if (subShellProcess != null)
        {
            try
            {
                // Wait for the launched process (explorer.exe or mstsc.exe) to exit
                subShellProcess.WaitForExit();
            }
            catch (Exception ex)
            {
                // Handle exceptions if monitoring fails
                MessageBox.Show($"Error monitoring sub-shell process: {ex.Message}. Exiting.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        // 3. Exit the shell process. This signals the OS that the user session is over.
        Application.Exit();
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
            
            // Optional cleanup (attempting to delete the running EXE's directory will likely fail, so we skip directory delete)
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
