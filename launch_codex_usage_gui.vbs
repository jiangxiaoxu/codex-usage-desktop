Option Explicit

Dim fileSystem
Dim shell
Dim baseDirectory

Set fileSystem = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

baseDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
If Not fileSystem.FileExists(baseDirectory & "\package.json") Then
    MsgBox "Electron project files were not found.", vbCritical, "Codex Usage Desktop"
    WScript.Quit 1
End If

shell.CurrentDirectory = baseDirectory
shell.Run "cmd.exe /c npm run package:portable:restart", 0, False
