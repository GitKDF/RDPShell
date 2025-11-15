# RDPShell

This small application allows you to setup a Windows user account that launches
directly into an RDP session on a remote PC.  It is a standalone application,
needing only RDPShell.exe to run.

This is not meant to be a secure way to have a remote PC session, but rather
a no-hassle, hard-to-mess-up RDP session.  The original use case for writing
it was two spouses that have their own PCs, one upstairs and one downstairs,
that occassionally wanted to log into their own from the other's physical PC.

Simply create a new local user, setup a .RDP file for the remote PC, and
install RDPShell.  Do this on each machine, and each spouse has access
to their own PC from the other's desk.

## Installation
Simply run RDPShell.exe (you may have to click 'More info' and 'Run anyway'
if Windows SmartScreen blocks it) while logged into the user account that
you want to run your RDP session and follow the prompts to install.
Files will be installed to the folder %USERPROFILE%\RDPShell (typically
C:\Users\<username>\RDPShell).

## Setup
This utility searches for an RDP file named 'RDPShell*.rdp' in the
install folder and launches the Remote Desktop Client (mstsc.exe)
using that file.  If no RDP file is found, it logs the user out.
If multiple matching files are found, it will use the first one it
finds, with no guarantee as to which that is, so have only one!

Feel free to add your own annotation to the filename after RDPShell,
e.g. the name of the computer you are connecting to.

You may want to edit the RDP file manually and change displayconnectionbar:i:1
to displayconnectionbar:i:0 to disable the connection bar.  It will still show
briefly upon connection, but then go away completely.  Ctrl+Alt+Break will
still toggle full screen mode, and closing the RDP window will trigger log off.

## Getting to the Desktop
To access the normal shell environment (explorer.exe) repeatedly press
the Windows key after entering your login credentials or clicking Sign in.
If the user does not have Administrative privileges, a UAC prompt will
ask you to provide them.

## Uninstallation:
Simply run the 'RDPShell.exe' file from any location while logged in with the
user it is installed for (by using the Windows key to get to the desktop).
The program will detect the installation and prompt you for uninstallation.
Note: You must log off and log back in for changes to the shell to take effect.

## Download
https://github.com/GitKDF/RDPShell/releases/latest

