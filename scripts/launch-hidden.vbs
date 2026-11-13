' Starts TranscodeServer.exe with a hidden console window (used by the startup shortcut).
Dim fso, sh, root
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
root = fso.GetParentFolderName(fso.GetParentFolderName(WScript.ScriptFullName))
sh.CurrentDirectory = root
sh.Run """" & root & "\TranscodeServer.exe""", 0, False
