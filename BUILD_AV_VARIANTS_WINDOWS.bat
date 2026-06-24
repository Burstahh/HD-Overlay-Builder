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

if exist PREPARE_NOTO_SANS_WINDOWS.bat (
  call PREPARE_NOTO_SANS_WINDOWS.bat
  if errorlevel 1 pause ^& exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK was not found.
  pause
  exit /b 1
)

where powershell >nul 2>nul
if errorlevel 1 (
  echo PowerShell was not found. Cannot create ZIP packages.
  pause
  exit /b 1
)

echo Cleaning previous AV variant output...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
if exist av_variants rmdir /s /q av_variants
mkdir av_variants

echo Restoring packages...
dotnet restore
if errorlevel 1 pause & exit /b 1

echo.
echo [1/3] Building single-file self-contained, untrimmed, uncompressed...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:ApplicationIcon=Resources\CDTOB.ico -o av_variants\singlefile_selfcontained
if errorlevel 1 pause & exit /b 1

echo.
echo [2/3] Building one-folder self-contained...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -p:ApplicationIcon=Resources\CDTOB.ico -o av_variants\onefolder_selfcontained
if errorlevel 1 pause & exit /b 1

echo.
echo [3/3] Building framework-dependent one-folder...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false -p:ApplicationIcon=Resources\CDTOB.ico -o av_variants\onefolder_frameworkdependent
if errorlevel 1 pause & exit /b 1

echo Creating ZIP files for AV/Nexus testing...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path 'av_variants\singlefile_selfcontained\*' -DestinationPath 'av_variants\HD_Overlay_Builder_v1.4_singlefile_selfcontained.zip' -Force; Compress-Archive -Path 'av_variants\onefolder_selfcontained\*' -DestinationPath 'av_variants\HD_Overlay_Builder_v1.4_onefolder_selfcontained.zip' -Force; Compress-Archive -Path 'av_variants\onefolder_frameworkdependent\*' -DestinationPath 'av_variants\HD_Overlay_Builder_v1.4_onefolder_frameworkdependent.zip' -Force; Get-ChildItem 'av_variants' -Filter '*.zip' | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash + '  ' + $_.Name } | Set-Content 'av_variants\SHA256SUMS.txt'"

echo.
echo Done. AV test variants created in:
echo %cd%\av_variants
echo.
type av_variants\SHA256SUMS.txt
echo.
pause
