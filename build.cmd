@echo off
setlocal

echo.
echo ================================================
echo        GIDE C# Build Script (Framework 4.8)
echo ================================================
echo.

set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set VERSION=1.0.0
set OUTPUT=dist\GIDE.exe

if not exist %CSC% (
    echo [!] .NET Framework 4.8 compiler not found.
    pause
    exit /b 1
)

if not exist dist mkdir dist

echo [+] Compiling GIDE.exe...

%CSC% /target:winexe /out:%OUTPUT% /optimize+ /platform:x64 ^
    /reference:System.dll ^
    /reference:System.Core.dll ^
    /reference:System.Net.Http.dll ^
    /reference:System.Web.Extensions.dll ^
    /reference:System.Management.dll ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Drawing.dll ^
    Program.cs ^
    GIDEClient.cs ^
    ToolExecutor.cs ^
    HistoryManager.cs ^
    ToolParser.cs ^
    Config.cs ^
    Installer.cs ^
    HardwareDetector.cs ^
    ModelManager.cs ^
    LocalModelEngine.cs ^
    RuntimeDependencyManager.cs ^
    UI.cs ^
    GIDESettingsForm.cs ^
    GIDEMainForm.cs

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [!] Build FAILED. See errors above.
    pause
    exit /b 1
)

echo [+] Build successful: %OUTPUT%

echo.
echo [+] Building installer...

set ISCC=
if exist "C:\Program Files\Inno Setup 7\ISCC.exe" (
    set ISCC="C:\Program Files\Inno Setup 7\ISCC.exe"
) else if exist "C:\Program Files (x86)\Inno Setup 7\ISCC.exe" (
    set ISCC="C:\Program Files (x86)\Inno Setup 7\ISCC.exe"
) else if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" (
    set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
) else if exist "C:\Program Files\Inno Setup 6\ISCC.exe" (
    set ISCC="C:\Program Files\Inno Setup 6\ISCC.exe"
)

if not defined ISCC (
    echo [!] Inno Setup not found. Install Inno Setup 6 or 7 to build the installer.
    pause
    exit /b 1
)

%ISCC% Installer\setup.iss
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [!] Installer build FAILED.
    pause
    exit /b 1
)

echo.
echo ================================================
echo   Build Complete!
echo   Installer: releases\%VERSION%\GIDE-Setup-%VERSION%.exe
echo ================================================