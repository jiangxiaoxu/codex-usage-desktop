Option Explicit

Dim fileSystem
Dim shell
Dim baseDirectory
Dim packagedExecutable
Dim portableExecutable
Dim candidate
Dim command

Set fileSystem = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

baseDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
packagedExecutable = baseDirectory & "\release\win-unpacked\Codex Usage Desktop.exe"
portableExecutable = ""

For Each candidate In fileSystem.GetFolder(baseDirectory & "\release").Files
    If LCase(fileSystem.GetExtensionName(candidate.Name)) = "exe" And LCase(Left(candidate.Name, 20)) = "codex usage desktop " Then
        If portableExecutable = "" Then
            portableExecutable = candidate.Path
        ElseIf candidate.DateLastModified > fileSystem.GetFile(portableExecutable).DateLastModified Then
            portableExecutable = candidate.Path
        End If
    End If
Next

If portableExecutable <> "" Then
    command = Chr(34) & portableExecutable & Chr(34)
    shell.Run command, 0, False
    WScript.Quit 0
End If

If fileSystem.FileExists(packagedExecutable) Then
    command = Chr(34) & packagedExecutable & Chr(34)
    shell.Run command, 0, False
    WScript.Quit 0
End If

If Not fileSystem.FileExists(baseDirectory & "\package.json") Then
    MsgBox "Electron project files were not found.", vbCritical, "Codex Usage Desktop"
    WScript.Quit 1
End If

shell.CurrentDirectory = baseDirectory
command = "cmd.exe /c npm run start"
shell.Run command, 0, False
