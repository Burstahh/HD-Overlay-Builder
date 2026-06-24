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
echo Cleaning previous build output...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
if exist publish\win-x64 rmdir /s /q publish\win-x64
echo Building HD Overlay Builder .NET port v1.4...
dotnet restore
if errorlevel 1 pause & exit /b 1
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:ApplicationIcon=Resources\CDTOB.ico -o publish\win-x64
if errorlevel 1 pause & exit /b 1
echo.
echo Done. Output:
echo %cd%\publish\win-x64\HD Overlay Builder.exe
pause
