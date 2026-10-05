@echo off
setlocal
cd /d "%~dp0"
dotnet build ImageRuntime\ImageRuntime.csproj -c Release --nologo
if errorlevel 1 exit /b 1
powershell -NoProfile -Command "$ErrorActionPreference = 'Stop'; $algorithm = [System.Security.Cryptography.SHA256]::Create(); try { $hash = [BitConverter]::ToString($algorithm.ComputeHash([IO.File]::ReadAllBytes('ImageRuntime/bin/Release/net10.0/ImageRuntime.dll'))).Replace('-', '').ToLowerInvariant(); if ($hash -ne '58937916bab2f2195f2e8a55f8c3486aca3bf1b6c5621d1ddfe3e5b0ffb3408a') { throw 'The rebuilt interpreter differs from the image-approved runtime. Do not remove the approval check.' } } finally { $algorithm.Dispose() }"
if errorlevel 1 exit /b 1
dotnet publish HoloPlayer\HoloPlayer.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o ..\Rebuilt
if errorlevel 1 exit /b 1
copy /y ..\..\game.tiff ..\Rebuilt\game.tiff >nul
if errorlevel 1 exit /b 1
copy /y ..\..\game.gif ..\Rebuilt\game.gif >nul
if errorlevel 1 exit /b 1
echo Rebuilt player: CSharpHarness\Rebuilt\HoloPlayer.exe
