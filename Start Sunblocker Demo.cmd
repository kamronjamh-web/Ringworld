@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\run_sunblocker_demo.ps1" %*
if errorlevel 1 (
  echo.
  echo Sunblocker demo launcher failed.
  pause
)
