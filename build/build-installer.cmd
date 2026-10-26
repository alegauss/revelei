@echo off
rem PB35: publishes Revelei self-contained and builds its per-user installer with Inno Setup.
rem
rem   build\build-installer.cmd
rem
rem 1. dotnet publish: Release, win-x64, self-contained, single file, ReadyToRun. The natives
rem    (ONNX Runtime, DirectML, WPF's, SkiaSharp's) stay beside the exe rather than being
rem    extracted at start: a single-file publish of native ONNX Runtime binaries is where this
rem    breaks, and the PB5 spike ran DirectML both ways.
rem 2. The packages' own license and notice files into artifacts\publish\licenses.
rem 3. PB51: the FP16 model set from .cache\models into artifacts\publish\models (stage-models.ps1),
rem    which the app reads before %LOCALAPPDATA%\Revelei\models. A missing model fails the build.
rem 4. ISCC compiles build\installer.iss into artifacts\installer\Revelei-Setup-<version>.exe.
setlocal

cd /d "%~dp0.."

set "PUBLISH=%CD%\artifacts\publish"

rem Nothing may be running from the folder this is about to empty and rewrite: the guard names the
rem pid and the path, and stops nothing on its own.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0guard-publish.ps1" -PublishDir "%PUBLISH%"
if errorlevel 1 exit /b 1

if exist "%PUBLISH%" rd /s /q "%PUBLISH%"
if exist "%CD%\artifacts\installer" rd /s /q "%CD%\artifacts\installer"

echo === Publishing Revelei (Release, win-x64, self-contained) ===
dotnet publish src\Revelei.App\Revelei.App.csproj --configuration Release --runtime win-x64 ^
    --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true ^
    -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=none --output "%PUBLISH%" --nologo
if errorlevel 1 (
    echo *** Publish failed; no installer was built. ***
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
    echo *** Staging the models failed; no installer was built. ***
    exit /b 1
)

rem Inno Setup 6, machine-wide or per user, or on PATH.
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do if not defined ISCC set "ISCC=%%I"
if not defined ISCC (
    echo *** ISCC.exe not found. Install Inno Setup 6: https://jrsoftware.org/isdl.php ***
    exit /b 1
)

echo.
echo === Building the installer ===
"%ISCC%" /Q "%~dp0installer.iss"
if errorlevel 1 (
    echo *** The installer did not compile. ***
    exit /b 1
)

for %%F in ("%CD%\artifacts\installer\Revelei-Setup-*.exe") do echo Installer: %%F
endlocal
