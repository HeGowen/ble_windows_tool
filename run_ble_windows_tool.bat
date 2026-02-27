@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0ble_windows_tool.exe" (
  echo ble_windows_tool.exe not found in current folder.
  echo Please download the packaged release zip and keep files together.
  pause
  exit /b 1
)
"%~dp0ble_windows_tool.exe"
echo.
echo Process exited with code %errorlevel%
pause
endlocal
