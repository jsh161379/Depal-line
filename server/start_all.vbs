' start_all.vbs - start every server program with NO window
' Check status in the admin program. Stop with stop_all.bat
' MySQL password must be saved once:  setx AA_DB_PASSWORD yourpassword
' Logs: logs\<name>.log
Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
d = fso.GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = d
opts = ""
For Each a In WScript.Arguments
  opts = opts & " " & a
Next
sh.Run "python """ & d & "\run_all.py"" --hidden" & opts, 0, False
