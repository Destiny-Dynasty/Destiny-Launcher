@echo off
setlocal
cd /d "%~dp0.."
set "DESTINY_PREVIEW_DOTNET=%TEMP%\DestinyLauncher-dotnet-sdk-10\dotnet.exe"
if not exist "%DESTINY_PREVIEW_DOTNET%" set "DESTINY_PREVIEW_DOTNET=dotnet"
"%DESTINY_PREVIEW_DOTNET%" publish "DestinyLauncher.Linux\DestinyLauncher.Linux.csproj" -c Release -r win-x64 --self-contained true -o "artifacts\AvaloniaWindowsPreview"
if errorlevel 1 (
  echo Cierra la vista previa si sigue abierta y vuelve a intentarlo.
  pause
  exit /b 1
)
start "" "artifacts\AvaloniaWindowsPreview\DestinyLauncher.Linux.exe" "--data-root=%CD%\artifacts\AvaloniaUiPreviewData" %*

