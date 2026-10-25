' ============================================================
'  Launch NPMC Launcher with no console window.
'
'  Why this exists:
'    launch.cmd runs the app through dotnet.exe, which is a
'    console-subsystem program, so a black console window sits
'    behind the launcher for its whole lifetime.
'
'    Running dotnet.exe from WScript with a hidden window (0)
'    removes that window without changing anything about how the
'    app itself starts.
'
'  Why the dotnet host at all:
'    the self-built, unsigned apphost is lowered to Low integrity
'    on this machine, which blocks opening Explorer. dotnet.exe is
'    Microsoft-signed, so the same managed code runs at Medium.
'
'  Usage: double-click this file. Command-line arguments are
'         forwarded to the launcher.
' ============================================================

Option Explicit

Dim shell, fso, root, dll, args, i, command

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

root = fso.GetParentFolderName(WScript.ScriptFullName)
dll = fso.BuildPath(root, "src\NPMC.Launcher.App\bin\Release\net8.0\NPMC.Launcher.App.dll")

If Not fso.FileExists(dll) Then
    MsgBox "NPMC Launcher is not built yet." & vbCrLf & vbCrLf & _
           "Run this first:" & vbCrLf & _
           "    dotnet build NPMC.Launcher.sln -c Release", _
           vbExclamation, "NPMC Launcher"
    WScript.Quit 1
End If

' Forward any arguments the user passed to this script
args = ""
For i = 0 To WScript.Arguments.Count - 1
    args = args & " """ & WScript.Arguments(i) & """"
Next

command = "dotnet """ & dll & """" & args

' 0 = hidden window, False = do not wait for exit
shell.Run command, 0, False
