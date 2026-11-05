@echo off
rem Build with the .NET Framework 4.0 csc (no .NET SDK required).
rem The workspace directory denies process execution, so the runnable copy
rem is deployed under LOCALAPPDATA.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found at %CSC%
  exit /b 1
)
if not exist "%~dp0..\bin" mkdir "%~dp0..\bin"
"%CSC%" /nologo /target:exe /platform:x64 /optimize+ /r:System.Web.Extensions.dll /out:"%~dp0..\bin\wpectl.exe" "%~dp0..\WpeCtl.cs"
if errorlevel 1 exit /b 1
echo built:    %~dp0..\bin\wpectl.exe

set DEST=%LOCALAPPDATA%\WpeCtl
if not exist "%DEST%" mkdir "%DEST%"
copy /y "%~dp0..\bin\wpectl.exe" "%DEST%\wpectl.exe" >nul
if errorlevel 1 (
  echo WARNING: copy to LOCALAPPDATA failed
  exit /b 1
)
echo deployed: %DEST%\wpectl.exe