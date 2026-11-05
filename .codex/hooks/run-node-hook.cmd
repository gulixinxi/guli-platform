@echo off
setlocal
set "SCRIPT=%~dp0%~1"
if not exist "%SCRIPT%" exit /b 2
if exist "C:\Program Files\nodejs\node.exe" (
  "C:\Program Files\nodejs\node.exe" "%SCRIPT%"
) else (
  node "%SCRIPT%"
)
exit /b %ERRORLEVEL%
