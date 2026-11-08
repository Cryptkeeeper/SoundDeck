@echo off
rem Builds the SoundDeck Stream Deck plugin and installs it (restarts the Stream Deck app).
cd /d "%~dp0"
set OUT=%~dp0build\com.sounddeck.controls.sdPlugin
set DEST=%APPDATA%\Elgato\StreamDeck\Plugins\com.sounddeck.controls.sdPlugin
if not exist "%OUT%" mkdir "%OUT%"

"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /out:"%OUT%\SoundDeckPlugin.exe" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Drawing.dll SoundDeckPlugin.cs
if errorlevel 1 (echo Build failed & pause & exit /b 1)
copy /y manifest.json "%OUT%\" >nul
copy /y pi.html "%OUT%\" >nul
copy /y pi-pick.html "%OUT%\" >nul
powershell -NoProfile -ExecutionPolicy Bypass -File make-images.ps1 "%OUT%\images"
for %%I in ("%~dp0..\SoundDeck.exe") do echo %%~fI> "%OUT%\sounddeck_path.txt"

taskkill /im StreamDeck.exe /f >nul 2>&1
ping -n 3 127.0.0.1 >nul
if exist "%DEST%" rmdir /s /q "%DEST%"
xcopy /e /i /y /q "%OUT%" "%DEST%" >nul
start "" "C:\Program Files\Elgato\StreamDeck\StreamDeck.exe"
echo Installed. Stream Deck is restarting.
