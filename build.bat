@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:SoundDeck.ico /out:SoundDeck.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll SoundDeck.cs MicMix.cs ControlServer.cs Features.cs Scaling.cs Denoise.cs Updater.cs GoXlr.cs GoXlrLink.cs Streaming.cs StreamMix.cs Dynamics.cs DynamicsUi.cs Version.cs
if %errorlevel%==0 (echo Built SoundDeck.exe) else (echo Build failed & exit /b 1)
