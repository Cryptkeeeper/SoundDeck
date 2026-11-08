@echo off
rem One-time: connect publish-update.bat to your GitHub so it uploads releases by itself.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer\connect-github.ps1"
