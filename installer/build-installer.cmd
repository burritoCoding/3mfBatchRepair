@echo off
rem Builds dist\3mfBatchRepair-setup.exe: a single installer with the four tools inside.
rem It installs for the current user (no admin rights) and doubles as the uninstaller.
setlocal
set ROOT=%~dp0..
call "%ROOT%\build.cmd" || exit /b 1
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%ROOT%\dist" mkdir "%ROOT%\dist"
copy /y "%ROOT%\README.md" "%ROOT%\dist\README.txt" >nul
"%FW%\csc.exe" /nologo /codepage:65001 /optimize+ /platform:x64 /target:winexe /out:"%ROOT%\dist\3mfBatchRepair-setup.exe" ^
  /win32manifest:"%~dp0setup.manifest" /win32icon:"%ROOT%\src\app.ico" ^
  /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Drawing.dll" ^
  /resource:"%ROOT%\3mfBatchRepair.exe",3mfBatchRepair.exe ^
  /resource:"%ROOT%\3mfBatchRepair-gui.exe",3mfBatchRepair-gui.exe ^
  /resource:"%ROOT%\3mfMeshSplit.exe",3mfMeshSplit.exe ^
  /resource:"%ROOT%\3mfMeshSplit-gui.exe",3mfMeshSplit-gui.exe ^
  /resource:"%ROOT%\dist\README.txt",README.txt ^
  "%~dp0Setup.cs" "%ROOT%\src\ShellMenu.cs" "%ROOT%\src\Util.cs"
if errorlevel 1 exit /b 1
del "%ROOT%\dist\README.txt"
echo built dist\3mfBatchRepair-setup.exe
