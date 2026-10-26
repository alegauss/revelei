@echo off
REM ==========================================================================
REM update-release.cmd - Sets Revelei's version and builds the release in
REM one step. After claude-tray's update-release.cmd.
REM   1) Updates the version in Directory.Build.props and app.manifest.
REM   2) Runs the tests (run-tests.cmd): no release comes out of a red build.
REM      The UI cases take over the desktop; run this in a guest, or run the
REM      suite there first (winwright's run-tests-vm.cmd -Tree <this repo>).
REM   3) Calls build-installer.cmd (publish + third-party notices + installer).
REM   4) [optional] With "upload" as the 2nd argument: commits the version,
REM      pushes, creates and pushes the tag vX.Y.Z and creates the GitHub
REM      release with the installer attached.
REM
REM Usage (from any folder):  build\update-release 0.1.0
REM                           build\update-release 0.1.0 upload
REM ==========================================================================
setlocal

REM This script lives in build\; the commit, the tag and the installer belong to the folder above.
cd /d "%~dp0.."

if "%~1"=="" (
    echo *** ERROR: give the version. ***
    echo Usage: build\update-release 0.1.0 [upload]
    exit /b 1
)

REM --- 1) The version, in both places that carry it -----------------------------
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update-release.ps1" -Version "%~1"
if errorlevel 1 (
    echo.
    echo *** ERROR: could not update the version. ***
    exit /b 1
)

REM --- 2) The tests, including the one that keeps both versions equal -----------
call "%~dp0..\run-tests.cmd"
if errorlevel 1 (
    echo.
    echo *** ERROR: the tests failed; no installer was built. ***
    exit /b 1
)

REM --- 3) Publish + installer ----------------------------------------------------
call "%~dp0build-installer.cmd"
if errorlevel 1 (
    echo.
    echo *** ERROR: could not build the installer. ***
    exit /b 1
)

set "SETUP=%CD%\artifacts\installer\Revelei-Setup-%~1.exe"
if not exist "%SETUP%" (
    echo *** ERROR: the expected installer does not exist: %SETUP% ***
    exit /b 1
)

REM --- 4) Upload (optional): commit + push + tag + GitHub release ----------------
if /i not "%~2"=="upload" goto :local

echo.
echo === Publishing release v%~1 on GitHub ===
echo.

REM Nothing to commit when the version already was this one (the first release, for example):
REM run-commit.cmd only runs when git sees a change.
git diff --quiet
if errorlevel 1 (
    call run-commit.cmd -m "chore: release v%~1"
    if errorlevel 1 (
        echo *** ERROR: could not commit the version. ***
        exit /b 1
    )
)

git push
if errorlevel 1 (
    echo *** ERROR: git push failed. ***
    exit /b 1
)

git tag "v%~1"
if errorlevel 1 (
    echo *** ERROR: could not create the tag v%~1 - it may already exist. ***
    exit /b 1
)
git push origin "v%~1"
if errorlevel 1 (
    echo *** ERROR: could not push the tag v%~1. ***
    exit /b 1
)

REM The release, with the installer attached and the notes GitHub generates.
gh release create "v%~1" "%SETUP%" --title "Revelei v%~1" --generate-notes
if errorlevel 1 (
    echo *** ERROR: could not create the GitHub release. ***
    exit /b 1
)

echo.
echo === Release v%~1 published ===
echo.
goto :end

:local
echo.
echo === Release v%~1 built locally ===
echo Installer: %SETUP%
echo Nothing was committed or pushed. To publish:  build\update-release %~1 upload
echo.

:end
endlocal
