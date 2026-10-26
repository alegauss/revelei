@echo off
setlocal enabledelayedexpansion

rem Builds Revelei (Debug) and starts it, to try the app by hand.
rem
rem   .\run.cmd                  a menu of manual test scenarios, each on a test image
rem   .\run.cmd [app options]    starts the app straight away with those options, e.g.
rem                              .\run.cmd --open photo.jpg --provider cpu
rem
rem Call it as .\run.cmd: with NoDefaultCurrentDirectoryInExePath set, a bare "run.cmd" finds
rem another run.cmd on the PATH (nvm ships one) before this one.
rem
rem The models export-and-quantize.py left in .cache\models are used unless --models is given;
rem without that folder the app falls back to %LOCALAPPDATA%\Revelei\models. The real test
rem images come with scripts\download_models.py (.cache\weights\test-images); the fixtures are
rem committed (tests\fixtures\images). App options: docs\COMMAND-LINE.md.

pushd "%~dp0"

echo Building Revelei (Debug)
dotnet build src\Revelei.App\Revelei.App.csproj --configuration Debug --nologo -v quiet -nodeReuse:false
if errorlevel 1 (
    echo.
    echo BUILD FAILED - the app was not started.
    popd
    exit /b 1
)

set "EXE=%CD%\src\Revelei.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Revelei.exe"
set "REAL=.cache\weights\test-images"
set "FIX=tests\fixtures\images"
set "OWN_MODELS="
if exist ".cache\models" set "OWN_MODELS=.cache\models"

if not "%~1"=="" goto :direct

set "PROVIDER=auto"
set "QUALITY=standard"
set "EXPORT=off"

:menu
cls
echo  Revelei - manual test scenarios
echo  ==================================
echo.
echo  Real models (scripts\download_models.py and export-and-quantize.py)
echo    1  Upscale           0014.jpg, a small noisy photo (179 x 179)
echo    2  Restore faces     Blake_Lively.jpg, two faces side by side
echo    3  Aligned face      Adele_crop.png, a 512 crop already aligned
echo    4  Remove background example_input.jpg, two giraffes against the sky
echo    5  Remove noise      building.png, a real noisy photo (SCUNet)
echo.
echo  Repository fixtures
echo    6  EXIF orientation  rotated-exif6.jpg, must open upright (8 x 16)
echo    7  Transparency      alpha-lossless.webp, a warning when exporting JPEG
echo    8  Colour profile    p3-swatch.png, Display P3 converted to sRGB
echo    9  Broken profile    broken-profile.png, must open without converting
echo   10  Camera details    exif-camera.jpg, "Keep camera details"
echo.
echo  Other
echo   11  Tiny models (offline): rotated-exif6.jpg on cases\fixtures\models
echo   12  No models: the panel says which model is missing
echo   13  Missing file: the first screen says it did not open
echo   14  Invalid argument: the app refuses and exits with code 2
echo   15  Empty screen, no image
echo.
echo  Options:  P provider [!PROVIDER!]   Q quality [!QUALITY!]   E export when done [!EXPORT!]
echo            S stop
echo.
set "PICK="
set /p "PICK=Choose: "
if not defined PICK goto :menu

set "IMG="
set "MODELS=!OWN_MODELS!"
set "EXTRA="
if /i "!PICK!"=="S" goto :done
if /i "!PICK!"=="P" goto :provider
if /i "!PICK!"=="Q" goto :quality
if /i "!PICK!"=="E" goto :export
if "!PICK!"=="1" set "IMG=%REAL%\0014.jpg"
if "!PICK!"=="2" set "IMG=%REAL%\Blake_Lively.jpg"
if "!PICK!"=="3" set "IMG=%REAL%\Adele_crop.png"
if "!PICK!"=="4" set "IMG=%REAL%\example_input.jpg"
if "!PICK!"=="5" set "IMG=%REAL%\building.png"
if "!PICK!"=="6" set "IMG=%FIX%\rotated-exif6.jpg"
if "!PICK!"=="7" set "IMG=%FIX%\alpha-lossless.webp"
if "!PICK!"=="8" set "IMG=%FIX%\p3-swatch.png"
if "!PICK!"=="9" set "IMG=%FIX%\broken-profile.png"
if "!PICK!"=="10" set "IMG=%FIX%\exif-camera.jpg"
if "!PICK!"=="11" set "IMG=%FIX%\rotated-exif6.jpg"
if "!PICK!"=="11" set "MODELS=cases\fixtures\models"
if "!PICK!"=="12" set "IMG=%FIX%\rotated-exif6.jpg"
if "!PICK!"=="12" set "MODELS=%FIX%"
if "!PICK!"=="13" set "IMG=%FIX%\missing.png"
if "!PICK!"=="14" set "EXTRA=--provider gpu"
if "!PICK!"=="15" goto :launch

if defined EXTRA goto :launch
if not defined IMG goto :unknown
rem Scenario 13 opens a file that is not there on purpose; any other missing image needs the downloads.
if "!PICK!"=="13" goto :launch
if not exist "!IMG!" goto :absent
goto :launch

:unknown
echo  Unknown option: !PICK!
pause
goto :menu

:absent
echo.
echo  !IMG! does not exist. Run these first:
echo    .venv\Scripts\python scripts\download_models.py
echo    .venv\Scripts\python scripts\export-and-quantize.py
pause
goto :menu

:launch
rem Scenario 14 passes its own bad --provider, so the toggles are left out for it.
set "ARGS=--provider !PROVIDER! --quality !QUALITY!"
if defined EXTRA set "ARGS=!EXTRA!"
if defined MODELS set ARGS=!ARGS! --models "!MODELS!"
if defined IMG set ARGS=!ARGS! --open "!IMG!"
rem --export never overwrites, so each run gets a fresh name under artifacts\manual (git-ignored).
if /i "!EXPORT!"=="on" if defined IMG if not exist "artifacts\manual" mkdir "artifacts\manual"
if /i "!EXPORT!"=="on" if defined IMG set ARGS=!ARGS! --export "artifacts\manual\result-!RANDOM!!RANDOM!.png"
echo.
echo  Starting: Revelei.exe !ARGS!
start "Revelei" "%EXE%" !ARGS!
timeout /t 2 >nul 2>&1
goto :menu

:provider
if /i "!PROVIDER!"=="auto" (set "PROVIDER=dml") else if /i "!PROVIDER!"=="dml" (set "PROVIDER=cpu") else (set "PROVIDER=auto")
goto :menu

:quality
if /i "!QUALITY!"=="standard" (set "QUALITY=high") else (set "QUALITY=standard")
goto :menu

:export
if /i "!EXPORT!"=="off" (set "EXPORT=on") else (set "EXPORT=off")
goto :menu

:direct
set "MODELS_ARG="
echo %* | findstr /i /c:"--models" >nul
if errorlevel 1 if defined OWN_MODELS set "MODELS_ARG=--models !OWN_MODELS!"
echo Starting %EXE% !MODELS_ARG! %*
start "Revelei" "%EXE%" !MODELS_ARG! %*

:done
popd
endlocal
