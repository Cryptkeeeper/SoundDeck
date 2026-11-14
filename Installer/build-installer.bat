@echo off
rem Builds dist\SoundDeckSetup.exe: SoundDeck.exe, the Stream Deck plugin and RNNoise (noise removal) are packed inside it.
rem RNNoise comes from ..\redist (rnnoise_mono.dll from the official v1.10 release, plus its licenses); the build stops
rem if that file isn't the exact official one. VB-CABLE is NOT packed (its license doesn't allow it); the installer
rem downloads it from vb-audio.com.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set STAGE=%~dp0stage
set PLUG=%STAGE%\plugin

if not exist ..\redist\rnnoise_mono.dll (echo Missing ..\redist\rnnoise_mono.dll: take vst\rnnoise_mono.dll from https://github.com/werman/noise-suppression-for-voice/releases/download/v1.10/win-rnnoise.zip & exit /b 1)
if not exist ..\redist\RNNoise-LICENSE.txt (echo Missing ..\redist\RNNoise-LICENSE.txt & exit /b 1)
powershell -NoProfile -Command "if ((Get-FileHash '..\redist\rnnoise_mono.dll' -Algorithm SHA256).Hash -ne '7DEBDFC4843461C47B23AC7A736B4663FE581643563AE764CDE395E006004080') { exit 1 }"
if errorlevel 1 (echo ..\redist\rnnoise_mono.dll isn't the official v1.10 file & exit /b 1)

rem 1. Stream Deck plugin (no sounddeck_path.txt; whoever installs it writes that).
rem    It comes first because SoundDeck.exe packs dist\plugin.zip inside itself (see ..\build.bat).
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%PLUG%"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:"%PLUG%\SoundDeckPlugin.exe" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Drawing.dll ..\StreamDeckPlugin\SoundDeckPlugin.cs
if errorlevel 1 (echo Plugin build failed & exit /b 1)
copy /y ..\StreamDeckPlugin\manifest.json "%PLUG%\" >nul
copy /y ..\StreamDeckPlugin\pi.html "%PLUG%\" >nul
copy /y ..\StreamDeckPlugin\pi-pick.html "%PLUG%\" >nul
powershell -NoProfile -ExecutionPolicy Bypass -File ..\StreamDeckPlugin\make-images.ps1 "%PLUG%\images"
powershell -NoProfile -Command "Compress-Archive -Path '%PLUG%\*' -DestinationPath '%STAGE%\plugin.zip' -Force"
if not exist "%STAGE%\plugin.zip" (echo Plugin zip failed & exit /b 1)
if not exist ..\dist mkdir ..\dist
copy /y "%STAGE%\plugin.zip" ..\dist\plugin.zip >nul

rem 2. SoundDeck itself
call ..\build.bat < nul
if errorlevel 1 (echo SoundDeck build failed & exit /b 1)
cd /d "%~dp0"
if not exist ..\SoundDeck.exe (echo SoundDeck build failed & exit /b 1)

rem 3. The installer (..\Addons.cs is shared with SoundDeck.exe: add-on downloads and how their installers are run)
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:..\SoundDeck.ico /out:..\dist\SoundDeckSetup.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /resource:..\SoundDeck.exe,SoundDeck.exe /resource:..\SoundDeck.ico,SoundDeck.ico /resource:"%STAGE%\plugin.zip",plugin.zip ^
  /resource:..\redist\rnnoise_mono.dll,rnnoise_mono.dll /resource:..\redist\RNNoise-LICENSE.txt,RNNoise-LICENSE.txt ^
  Setup.cs ..\Addons.cs
if errorlevel 1 (echo Installer build failed & exit /b 1)
rmdir /s /q "%STAGE%"
echo Built ..\dist\SoundDeckSetup.exe
