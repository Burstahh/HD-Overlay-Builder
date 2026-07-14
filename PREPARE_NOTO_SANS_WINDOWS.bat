@echo off
setlocal
cd /d "%~dp0"

set FONT_ZIP=Noto_Sans.zip
set FONT_DIR=Resources\Fonts

if not exist "%FONT_ZIP%" (
  echo Noto_Sans.zip was not found beside this script.
  echo Skipping local Noto Sans prep. The app can still use installed Noto Sans or fall back to Segoe UI.
  exit /b 0
)

where powershell >nul 2>nul
if errorlevel 1 (
  echo PowerShell was not found. Cannot extract Noto Sans fonts.
  exit /b 1
)

if not exist "%FONT_DIR%" mkdir "%FONT_DIR%"

echo Preparing Noto Sans font resources for embedded EXE build...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$root=(Get-Location).Path;" ^
  "$zip=Join-Path $root 'Noto_Sans.zip';" ^
  "$dest=Join-Path $root 'Resources\Fonts';" ^
  "Add-Type -AssemblyName System.IO.Compression.FileSystem;" ^
  "$archive=[System.IO.Compression.ZipFile]::OpenRead($zip);" ^
  "$names=@('NotoSans-Regular.ttf','NotoSans-Bold.ttf','NotoSans-Italic.ttf','NotoSans-BoldItalic.ttf');" ^
  "foreach($name in $names){" ^
  "  $entry=$archive.Entries | Where-Object { $_.FullName -eq ('static/' + $name) -or $_.FullName -eq $name } | Select-Object -First 1;" ^
  "  if(-not $entry){ throw ('Missing font in zip: ' + $name) }" ^
  "  $target=Join-Path $dest $name;" ^
  "  [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true);" ^
  "  Write-Host ('Prepared ' + $name);" ^
  "}" ^
  "$license=$archive.Entries | Where-Object { $_.FullName -eq 'OFL.txt' } | Select-Object -First 1;" ^
  "if($license){ [System.IO.Compression.ZipFileExtensions]::ExtractToFile($license, (Join-Path $dest 'OFL_NOTO_SANS.txt'), $true); Write-Host 'Prepared OFL_NOTO_SANS.txt'; }" ^
  "$archive.Dispose();"
if errorlevel 1 exit /b 1

echo Noto Sans font resources are ready. Run BUILD_WINDOWS.bat or BUILD_RELEASE_WINDOWS.bat next.
exit /b 0
