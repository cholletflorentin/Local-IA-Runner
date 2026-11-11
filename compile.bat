@echo off
chcp 65001 >nul
title Local IA Runner - Compilation
cd /d "%~dp0"
set "APP_VERSION=%~1"
if not defined APP_VERSION set "APP_VERSION=1.0.0"

echo ============================================
echo  Local IA Runner - Compilation
echo ============================================
echo  Version : %APP_VERSION%
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERREUR: .NET SDK non trouve dans PATH.
    echo Installez le SDK .NET 10 depuis https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)

echo Generation de l'application et de l'installateur...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0WinFormsApp\Installer\build-installer.ps1" -AppVersion "%APP_VERSION%"
if errorlevel 1 (
    echo.
    echo ERREUR: La compilation de l'installateur a echoue.
    echo Verifiez que Inno Setup 7 est installe et consultez les erreurs ci-dessus.
    pause
    exit /b 1
)

echo.
echo Generation de la version portable...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0WinFormsApp\Installer\build-portable.ps1" -AppVersion "%APP_VERSION%"
if errorlevel 1 (
    echo.
    echo ERREUR: La generation de la version portable a echoue.
    pause
    exit /b 1
)

echo.
echo ============================================
echo  Compilation terminee avec SUCCES !
echo ============================================
echo.
echo Installateur : artifacts\installer\LocalIARunner-Setup-x64.exe
echo Version portable : artifacts\portable\LocalIARunner-Portable-x64-%APP_VERSION%\Local IA Runner.exe
echo Archive portable : artifacts\portable\LocalIARunner-Portable-x64-%APP_VERSION%.zip
echo.
pause
