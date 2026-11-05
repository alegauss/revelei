@echo off
rem PB90: publishes Revelei and packs it as the Microsoft Store's MSIX.
rem
rem   build\build-msix.cmd              artifacts\Revelei.msix, unsigned: the Store signs it on upload
rem   build\build-msix.cmd -Register    also installs the layout on this machine (Developer Mode)
rem   build\build-msix.cmd -NoPublish   packs the artifacts\publish already there
setlocal

rem Before the loop below: shift moves %0 too, after which %~dp0 is no longer this folder.
set "HERE=%~dp0"
set "PUBLISH_FIRST=1"
set "ARGS="
:args
if "%~1"=="" goto run
if /i "%~1"=="-NoPublish" (set "PUBLISH_FIRST=") else (set "ARGS=%ARGS% %1")
shift
goto args

:run
if defined PUBLISH_FIRST (
    call "%HERE%publish.cmd"
    if errorlevel 1 (
        echo *** No package was built. ***
        exit /b 1
    )
)

echo.
echo === Packing the MSIX ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%build-msix.ps1" %ARGS%
if errorlevel 1 (
    echo *** The package was not built. ***
    exit /b 1
)
endlocal
