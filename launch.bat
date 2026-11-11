@echo off
chcp 65001 >nul
title Local AI Assistant - Launch

cd /d "%~dp0"

echo ============================================
echo  Local AI Assistant - Lancement
echo ============================================
echo.

echo Verification de Python...
python --version >nul 2>&1
if errorlevel 1 (
    echo ERREUR: Python non trouve dans PATH
    echo Installez Python 3.10+ depuis https://python.org
    echo Assurez-vous de cocher "Add to PATH" lors de l'installation
    pause
    exit /b 1
) else (
    for /f "tokens=1" %%i in ('python --version') do set PY_VERSION=%%i
    echo OK - Python detecte: %PY_VERSION%
)

echo.
echo Verification de l'executable...
if not exist "WinFormsApp\bin\Release\net10.0-windows\Local IA Runner.exe" (
    echo ERREUR: Executable non trouve
    echo Lancez compile.bat d'abord pour compiler le projet
    pause
    exit /b 1
)

echo.
echo Lancement de Local IA Runner (llama.cpp)...
echo.

start "" "WinFormsApp\bin\Release\net10.0-windows\Local IA Runner.exe"

echo Application lancee.
echo.
pause
