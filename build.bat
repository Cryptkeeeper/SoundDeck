@echo off
cd /d "%~dp0"
rem The Stream Deck plugin (built by Installer\build-installer.bat into dist\plugin.zip) is packed inside
rem SoundDeck.exe, so the Setup page can add it without a download. Without that file, SoundDeck builds without it.
set PLUGINRES=
if exist dist\plugin.zip set PLUGINRES=/resource:dist\plugin.zip,plugin.zip
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:SoundDeck.ico /out:SoundDeck.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll %PLUGINRES% SoundDeck.cs MicMix.cs ControlServer.cs Features.cs Scaling.cs Denoise.cs Updater.cs GoXlr.cs GoXlrLink.cs Streaming.cs StreamMix.cs Dynamics.cs DynamicsUi.cs PushToTalk.cs Headphones.cs WhatsNew.cs Theme.cs VoiceFx.cs Effects.cs Extras.cs Addons.cs SetupPage.cs Testing.cs Version.cs
if %errorlevel%==0 (echo Built SoundDeck.exe) else (echo Build failed & exit /b 1)
