@echo off
chcp 65001 >nul
title Local IA Runner - Création de l'installateur

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0WinFormsApp\Installer\build-installer.ps1"
if errorlevel 1 (
    echo.
    echo La création de l'installateur a échoué.
    pause
    exit /b 1
)

start "" "%~dp0artifacts\installer\LocalIARunner-Setup-x64.exe"
