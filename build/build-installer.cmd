@echo off
rem PB35: publishes Revelei self-contained and builds its per-user installer with Inno Setup.
rem
rem   build\build-installer.cmd
rem
rem 1. publish.cmd: the self-contained publish, the notices and the staged models in
rem    artifacts\publish, the same folder the Store's MSIX is packed from (build-msix.cmd, PB90).
rem 2. ISCC compiles build\installer.iss into artifacts\installer\Revelei-Setup-<version>.exe.
setlocal

cd /d "%~dp0.."

if exist "%CD%\artifacts\installer" rd /s /q "%CD%\artifacts\installer"

call "%~dp0publish.cmd"
if errorlevel 1 (
    echo *** No installer was built. ***
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
