@echo off
rem ============================================================
rem  Launch NPMC Launcher through the dotnet host.
rem
rem  Why not run NPMC.Launcher.App.exe directly:
rem    On this machine the self-built, unsigned apphost is lowered
rem    to Low integrity. A Low-integrity process cannot open
rem    Explorer, cannot ShellExecute, and cannot start child
rem    shells - the folder buttons all fail and an error dialog
rem    appears. Signing the exe does not change this (verified).
rem
rem    dotnet.exe is Microsoft-signed, so the very same managed
rem    code runs at Medium integrity and everything works.
rem
rem  Usage: double-click this file, or run it with extra args.
rem ============================================================

setlocal
set "APP=%~dp0src\NPMC.Launcher.App\bin\Release\net8.0\NPMC.Launcher.App.dll"

if not exist "%APP%" (
    echo NPMC Launcher has not been built yet.
    echo Run this first:
    echo     dotnet build NPMC.Launcher.sln -c Release
    echo.
    pause
    exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET runtime was not found on PATH.
    echo Install the .NET 8 Desktop Runtime, then try again.
    echo.
    pause
    exit /b 1
)

dotnet "%APP%" %*
