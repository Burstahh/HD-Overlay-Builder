@echo off
setlocal
cd /d "%~dp0"

set "APP_EXE=HD Overlay Builder.exe"
set "OUT_DIR=%CD%\publish\HD Overlay Builder"
set "PROJECT_FILE=HDOverlayBuilder.csproj"

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

if not exist "%PROJECT_FILE%" (
  echo Project file not found: %PROJECT_FILE%
  pause
  exit /b 1
)

echo Cleaning previous build output...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
if exist "%OUT_DIR%" rmdir /s /q "%OUT_DIR%"

mkdir "%OUT_DIR%" >nul 2>nul

echo Building HD Overlay Builder .NET port v1.4.2...
dotnet restore "%PROJECT_FILE%"
if errorlevel 1 pause & exit /b 1

dotnet publish "%PROJECT_FILE%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:ApplicationIcon="Resources\CDTOB.ico" -o "%OUT_DIR%"
if errorlevel 1 pause & exit /b 1

if exist README.md (
  copy /y "README.md" "%OUT_DIR%\README.md" >nul
)

rem Keep the published end-user folder clean. Resources are embedded/compile-time only.
if exist "%OUT_DIR%\Resources" rmdir /s /q "%OUT_DIR%\Resources"

if not exist "%OUT_DIR%\%APP_EXE%" (
  echo Expected EXE not found: %OUT_DIR%\%APP_EXE%
  pause
  exit /b 1
)

echo.
echo Done. Output:
echo %OUT_DIR%\%APP_EXE%
if exist "%OUT_DIR%\README.md" echo README copied to: %OUT_DIR%\README.md
pause
