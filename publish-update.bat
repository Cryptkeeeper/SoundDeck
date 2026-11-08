@echo off
rem Makes a new SoundDeck release for GitHub.  Example:  publish-update.bat 1.1.0 "Added a new soundboard page"
if "%~1"=="" (
  echo Usage: publish-update.bat VERSION "what changed"
  echo Example: publish-update.bat 1.1.0 "Fixed the mic meter"
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer\publish-update.ps1" -Version %1 -Notes "%~2"
pause
