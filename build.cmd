@echo off
rem Builds the 3mfBatchRepair tools with the C# compiler that ships with Windows (.NET Framework 4.x):
rem   3mfBatchRepair.exe        command-line repair
rem   3mfBatchRepair-gui.exe    repair window, and the target of the right-click menu
rem Compiling the repair tools needs the Windows 10 SDK's union Windows.winmd; running them needs nothing extra.
rem An exe that's in use (e.g. a batch still running) is reported and skipped; the others still build.
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WINMD=
for /d %%D in ("%ProgramFiles(x86)%\Windows Kits\10\UnionMetadata\10.*") do if exist "%%D\Windows.winmd" set WINMD=%%D\Windows.winmd
if not defined WINMD (
  echo Windows 10 SDK not found: need "Windows Kits\10\UnionMetadata\<version>\Windows.winmd" to compile.
  exit /b 1
)
set SRC=%~dp0src
set CSC="%FW%\csc.exe" /nologo /codepage:65001 /optimize+ /platform:x64 /win32icon:"%SRC%\app.ico"
set IO=/r:"%FW%\System.IO.Compression.dll" /r:"%FW%\System.IO.Compression.FileSystem.dll"
set WINRT=/r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" /r:"%FW%\System.Threading.Tasks.dll" /r:"%WINMD%"
set REPAIR="%SRC%\Engine.cs" "%SRC%\MeshIO.cs" "%SRC%\ShellMenu.cs" "%SRC%\Util.cs"
set FAILED=

%CSC% /target:exe /out:"%~dp03mfBatchRepair.exe" %IO% %WINRT% "%SRC%\Cli.cs" %REPAIR%
if errorlevel 1 set FAILED=%FAILED% 3mfBatchRepair.exe

%CSC% /target:winexe /out:"%~dp03mfBatchRepair-gui.exe" %IO% %WINRT% /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Drawing.dll" "%SRC%\Gui.cs" "%SRC%\GuiKit.cs" %REPAIR%
if errorlevel 1 set FAILED=%FAILED% 3mfBatchRepair-gui.exe

if defined FAILED (
  echo not built:%FAILED%
  exit /b 1
)
echo built 3mfBatchRepair.exe and 3mfBatchRepair-gui.exe
