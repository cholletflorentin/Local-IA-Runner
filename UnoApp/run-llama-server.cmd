@echo off
setlocal
set "SERVER="
for /r "%~dp0llama-bin" %%F in (llama-server.exe) do if not defined SERVER set "SERVER=%%F"
if not defined SERVER echo llama-server.exe est introuvable dans le dossier llama-bin.
if not defined SERVER exit /b 1
"%SERVER%" %*
exit /b %ERRORLEVEL%
