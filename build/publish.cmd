@echo off
rem The publish both packages are made from: the Inno Setup installer (build-installer.cmd, PB35)
rem and the Store's MSIX (build-msix.cmd, PB90). Writes artifacts\publish.
rem
rem 1. dotnet publish: Release, win-x64, self-contained, single file, ReadyToRun. The natives
rem    (ONNX Runtime, DirectML, WPF's, SkiaSharp's) stay beside the exe rather than being
rem    extracted at start: a single-file publish of native ONNX Runtime binaries is where this
rem    breaks, and the PB5 spike ran DirectML both ways.
rem 2. The packages' own license and notice files into artifacts\publish\licenses.
rem 3. PB51: the FP16 model set from .cache\models into artifacts\publish\models (stage-models.ps1),
rem    which the app reads before %LOCALAPPDATA%\Revelei\models. A missing model fails the build.
setlocal

cd /d "%~dp0.."

set "PUBLISH=%CD%\artifacts\publish"

rem Nothing may be running from the folder this is about to empty and rewrite: the guard names the
rem pid and the path, and stops nothing on its own.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0guard-publish.ps1" -PublishDir "%PUBLISH%"
if errorlevel 1 exit /b 1

if exist "%PUBLISH%" rd /s /q "%PUBLISH%"

echo === Publishing Revelei (Release, win-x64, self-contained) ===
dotnet publish src\Revelei.App\Revelei.App.csproj --configuration Release --runtime win-x64 ^
    --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true ^
    -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=none --output "%PUBLISH%" --nologo
if errorlevel 1 (
    echo *** Publish failed. ***
    exit /b 1
)

echo.
echo === Collecting third-party notices ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect-notices.ps1" -Out "%PUBLISH%\licenses"
if errorlevel 1 (
    echo *** Collecting the notices failed. ***
    exit /b 1
)

echo.
echo === Staging the models ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stage-models.ps1" -Out "%PUBLISH%\models"
if errorlevel 1 (
    echo *** Staging the models failed. ***
    exit /b 1
)
endlocal
