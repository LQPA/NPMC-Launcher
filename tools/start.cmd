@echo off
rem Portable launcher for NPMC Launcher.
rem
rem Why this exists:
rem   Windows integrity level comes from the file/folder label, not from the
rem   signature. If the exe is copied out of a Low-integrity folder, Windows can
rem   start it at Low, where ShellExecute/Explorer and some file operations fail.
rem   cmd.exe itself starts at Medium, so this script can lift the exe label first
rem   and then launch it.
rem
rem Keep this file next to NPMC.Launcher.App.exe. Extra arguments are forwarded.

setlocal
set "APP=%~dp0NPMC.Launcher.App.exe"

if not exist "%APP%" (
    echo NPMC.Launcher.App.exe not found next to this script.
    pause
    exit /b 1
)

icacls "%APP%" /setintegritylevel Medium >nul 2>&1
start "" "%APP%" %*
