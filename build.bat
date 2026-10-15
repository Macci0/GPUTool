@echo off
rem Builds GPUTool.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio or SDK required.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo csc.exe not found - .NET Framework 4.5 or newer is required.
    exit /b 1
)

if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:bin\GPUTool.exe src\GPUTool.cs
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)
echo.
echo Build OK: bin\GPUTool.exe
