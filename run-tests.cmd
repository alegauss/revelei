@echo off
setlocal enabledelayedexpansion

rem The one command whose exit code is the suite's verdict: it builds Revelei.slnx, then runs
rem every test application under tests\ and fails on a red one, a missing one, or none at all.
rem UI cases are not part of it; they run separately through winwright.
rem
rem Usage:  run-tests.cmd [Configuration]        default Debug

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

rem How long one test may run before it is taken as hung, ended and named.
if "%REVELEI_HANG%"=="" set "REVELEI_HANG=10m"

pushd "%~dp0"

set "REPORTS=%CD%\TestResults\reports"
if exist "%REPORTS%" rd /s /q "%REPORTS%"
mkdir "%REPORTS%" 2>nul

echo Building Revelei.slnx (%CONFIG%)
dotnet build Revelei.slnx --configuration %CONFIG% --nologo -v quiet -nodeReuse:false
if errorlevel 1 (
    echo.
    echo BUILD FAILED - no tests were run.
    popd
    exit /b 1
)

set /a RAN=0
set /a FAILED=0
set "BROKEN="

rem The *.Tests folders and the winwright UI cases (PB38), which skip on a desk that cannot be
rem observed; tests\fixtures holds data and is not a test application.
for /d %%P in (tests\*.Tests tests\*.Cases) do (
    set "APP=%%P\bin\%CONFIG%\net10.0-windows10.0.19041.0\%%~nxP.exe"
    echo.
    echo === %%~nxP ===
    if exist "!APP!" (
        "!APP!" --results-directory "%REPORTS%" --hangdump --hangdump-timeout %REVELEI_HANG% --hangdump-type Mini
        if errorlevel 1 (
            set /a FAILED+=1
            set "BROKEN=!BROKEN! %%~nxP"
        )
        set /a RAN+=1
    ) else (
        echo   no test application at !APP!
        set /a FAILED+=1
        set "BROKEN=!BROKEN! %%~nxP^(missing^)"
    )
)

rem The model scripts (PB7 onward) are Python and tested with the standard library's unittest.
rem Python is required to generate models, not to build the app, so a desk without it says so
rem out loud and the run still fails: a skipped suite is not a green one.
rem The export tests (PB8) need torch: .venv holds scripts\requirements.txt when the desk has made
rem it, and without it those tests are skipped and unittest's summary counts them.
echo.
echo === scripts\tests (Python) ===
set "PY=python"
if exist ".venv\Scripts\python.exe" set "PY=.venv\Scripts\python.exe"
where python >nul 2>&1
if errorlevel 1 if not exist ".venv\Scripts\python.exe" (
    echo   python not found on PATH - the model script tests did not run
    set /a FAILED+=1
    set "BROKEN=!BROKEN! scripts\tests^(no python^)"
    goto :scripts_done
)
"!PY!" -W ignore::DeprecationWarning -m unittest discover -s scripts\tests
if errorlevel 1 (
    set /a FAILED+=1
    set "BROKEN=!BROKEN! scripts\tests"
)
set /a RAN+=1
:scripts_done

echo.

rem Nothing found is not a pass: a suite that silently shrinks to zero is what this guards against.
if %RAN%==0 (
    echo NO TEST APPLICATIONS FOUND under tests\ for configuration %CONFIG%.
    popd
    exit /b 1
)

if %FAILED%==0 (
    echo All %RAN% test suites passed.
    popd
    exit /b 0
)

echo %FAILED% of %RAN% test suites failed:!BROKEN!
popd
exit /b 1
