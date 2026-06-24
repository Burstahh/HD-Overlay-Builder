@echo off
setlocal
cd /d "%~dp0"

set APP_EXE=HD Overlay Builder.exe

REM Friendly warning when the previous EXE is still running and locked.
tasklist /FI "IMAGENAME eq %APP_EXE%" 2>nul | find /I "%APP_EXE%" >nul
if not errorlevel 1 (
  echo %APP_EXE% is currently running.
  echo Close the app before building, then run this script again.
  pause
  exit /b 1
)
set VERSION=1.4
set PUBLIC_ZIP=HD_Overlay_Builder_v1.4.zip
set SOURCE_ZIP=HD_Overlay_Builder_v1.4_SOURCE.zip

if exist PREPARE_NOTO_SANS_WINDOWS.bat (
  call PREPARE_NOTO_SANS_WINDOWS.bat
  if errorlevel 1 pause ^& exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK was not found.
  echo Install a recent .NET SDK. Your dotnet-sdk-10.x install is fine for this source package.
  pause
  exit /b 1
)

where powershell >nul 2>nul
if errorlevel 1 (
  echo PowerShell was not found. Cannot create ZIP packages.
  pause
  exit /b 1
)

echo Cleaning previous output...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
if exist publish\win-x64 rmdir /s /q publish\win-x64
if exist dist rmdir /s /q dist
mkdir dist

echo.
echo Building HD Overlay Builder v%VERSION%...
dotnet restore
if errorlevel 1 pause & exit /b 1

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:ApplicationIcon=Resources\CDTOB.ico -o publish\win-x64
if errorlevel 1 pause & exit /b 1

if not exist "publish\win-x64\%APP_EXE%" (
  echo Expected EXE not found: publish\win-x64\%APP_EXE%
  pause
  exit /b 1
)

echo Creating public ZIP...
mkdir dist\public
copy /y "publish\win-x64\%APP_EXE%" "dist\public\%APP_EXE%" >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path 'dist\public\*' -DestinationPath ('dist\' + '%PUBLIC_ZIP%') -Force"
if errorlevel 1 pause & exit /b 1

echo Creating source ZIP...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root=(Get-Location).Path; $stage=Join-Path $env:TEMP ('HD_Upscale_Overlay_Builder_SOURCE_' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force -Path $stage | Out-Null; $names=@('Core','Documentos en Español','English Documents','Resources','app.manifest','AppSetting.cs','BUILD_AV_VARIANTS_WINDOWS.bat','BUILD_RELEASE_WINDOWS.bat','BUILD_WINDOWS.bat','PREPARE_NOTO_SANS_WINDOWS.bat','HDOverlayBuilder.csproj','Localization.cs','MainForm.cs','Program.cs','README.md','THIRD_PARTY_NOTICES.md','.gitignore'); foreach($name in $names){ $src=Join-Path $root $name; if(Test-Path $src){ Copy-Item $src (Join-Path $stage $name) -Recurse -Force } }; if(Test-Path (Join-Path $stage 'Resources\Fonts')){ Get-ChildItem (Join-Path $stage 'Resources\Fonts') -Filter '*.ttf' -Recurse | Remove-Item -Force }; Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $root ('dist\' + '%SOURCE_ZIP%')) -Force; Remove-Item $stage -Recurse -Force"
if errorlevel 1 pause & exit /b 1

echo Writing SHA256SUMS.txt...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem 'dist' -File | Where-Object { $_.Name -like '*.zip' } | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash + '  ' + $_.Name } | Set-Content 'dist\SHA256SUMS.txt'"

echo.
echo Done. Packages created in:
echo %cd%\dist
echo.
type dist\SHA256SUMS.txt
echo.
pause
