Option Explicit

Dim shell, fileSystem, baseDirectory, executablePath, dataDirectory
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")
baseDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
executablePath = fileSystem.BuildPath(baseDirectory, "S880Controller.exe")
dataDirectory = fileSystem.BuildPath(baseDirectory, "data")
shell.Run """" & executablePath & """ --switch-usb --data-dir """ & dataDirectory & """", 0, False
