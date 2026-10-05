@echo off
setlocal
cd /d "%~dp0"
if not exist "CSharpHarness\HoloPlayer.exe" (
  echo Extract the complete repository ZIP before opening Play.cmd.
  pause
  exit /b 1
)
"%~dp0CSharpHarness\HoloPlayer.exe" "%~dp0game.tiff"
if errorlevel 1 pause
