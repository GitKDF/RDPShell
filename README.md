# RDPShell

This small application allows you to set up a Windows user account that launches
directly into an RDP session on a remote PC. It is a standalone application,
needing only **RDPShell.exe** to run.

This is designed to be a no-hassle, hard-to-mess-up RDP session. The original use case for writing
it was two spouses who have their own PCs, one upstairs and one downstairs,
and occasionally wanted to log into their own desktop from the other's physical PC.

Simply create a new local user, set up a **.RDP file** for the remote PC, and
install RDPShell. Do this on each machine, and each spouse has access
to their own PC from the other's desk.

## Installation and Management

Simply run **RDPShell.exe** (you may have to click 'More info' and 'Run anyway'
if Windows SmartScreen blocks it) while logged into the user account that
you want to run your RDP session and follow the prompts.

Files will be installed to the folder `%USERPROFILE%\RDPShell` (typically
`C:\Users\<username>\RDPShell`).

The application will prompt you to:

1. **Install** the shell if it's not detected.

2. **Uninstall** the shell if it is currently installed.

3. **Change settings**, specifically the Task Manager visibility.

## RDP Session Setup

This utility searches for an RDP file named **'RDPShell\*.rdp'** in the
install folder and launches the Remote Desktop Client (`mstsc.exe`)
using that file. If no RDP file is found, it logs the user out.

* **Naming:** Feel free to add your own annotation to the filename after `RDPShell`,
  e.g., `RDPShell_MyPC.rdp`.\
    **Note:** You should have only one matching RDP file in the folder; if more than one exists the first one found will be used.

* **Connection Bar:** You may want to edit the RDP file manually and change `displayconnectionbar:i:1`
  to `displayconnectionbar:i:0` to disable the connection bar. It will still show
  briefly upon connection, but then disappear. `Ctrl+Alt+Break` will
  still toggle full screen mode, and closing the RDP window will trigger log off.

## Pre-launch Script

You may create either a PowerShell or a Batch script that will be run before starting
the RDP session by creating the file RDPShell.(ps1|bat) in the installation folder.
If a script is found, it will be executed and the exit-code checked.  This can be used
for adding any necessary setup conditions, e.g. enabling a wifi or VPN connection.
Exit your script with a non-zero exit code to trigger a failure and logoff condition.
This scriupt should be able to run headless with no user input, as it will be hidden.
Note: There is a hard-coded timeoue of 60 seconds for the pre-launch script to execute.

## Getting to the Local Desktop

To access the normal shell environment (`explorer.exe`) instead of the RDP session, **repeatedly press the Windows key** after entering your login credentials or clicking 'Sign in'.

If the user does not have Administrative privileges, a UAC prompt will appear asking you to provide credentials to continue to the desktop.

## Task Manager Control

During installation, or by running **RDPShell.exe** again and selecting the change option, you can choose to **suppress (hide)** or **enable (show)** the Task Manager option on the **Ctrl+Alt+Del** security screen.

This setting is useful in dedicated RDP environments to minimize the local options available to the user.

* **Note:** Changing this setting requires Administrator credentials and a UAC prompt.

## A Note About Security

While the option has been included to disable the Task Manager button on the
**Ctrl+Alt+Del** screen and launching any other process would be difficult,
this utility is not intended to be used as a secure evirnoment on something like a
public facing terminal.  ***Use at your own risk.***
## Uninstallation

Simply run the **'RDPShell.exe'** file again while logged in with the
user it is installed for (by using the Windows key to get to the desktop).
The program will detect the installation and prompt you for uninstallation.

* **Note:** You must log off and log back in for changes to the shell to take effect.


## Download

https://github.com/GitKDF/RDPShell/releases/latest
