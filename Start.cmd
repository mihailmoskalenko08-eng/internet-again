@echo off
setlocal
set "internet_again_compiler=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "internet_again_exe=%~dp0InternetAgain.exe"
if exist "%internet_again_exe%" goto launch
if not exist "%internet_again_compiler%" (
    echo Windows x64 with .NET Framework 4.6 or later is required.
    pause
    exit /b 1
)
"%internet_again_compiler%" /nologo /target:winexe /platform:x64 /langversion:5 /optimize+ /unsafe /codepage:65001 /out:"%internet_again_exe%" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Runtime.Serialization.dll "%~dp0src\App.cs" "%~dp0src\Monitor.cs" "%~dp0src\Traffic.cs" "%~dp0src\Interface.cs"
if errorlevel 1 (
    pause
    exit /b 1
)
:launch
start "" "%internet_again_exe%"
