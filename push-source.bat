@echo off
rem Uploads SoundDeck's source code to GitHub.  Example:  push-source.bat "Added streamer mode"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer\push-source.ps1" -Message "%~1"
pause
