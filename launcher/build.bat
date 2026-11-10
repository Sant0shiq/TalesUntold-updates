@echo off
rem Builds TalesUntoldLauncher.exe with the C# compiler included in Windows (.NET Framework 4).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize /out:"%~dp0TalesUntoldLauncher.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "%~dp0Launcher.cs"
if errorlevel 1 (echo Build failed & pause & exit /b 1)
echo Built %~dp0TalesUntoldLauncher.exe
