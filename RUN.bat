@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"

set "VERSION_FILE=%~dp0VERSION.txt"
set "SRC=%~dp0src\CatLayer.cs"
set "EXE=%~dp0CatLayer.exe"
set "BUILD_LOG=%~dp0CatLayer_build.log"
set "CRASH_LOG=%LOCALAPPDATA%\CatLayer\crash.log"

if not exist "%VERSION_FILE%" goto :MISSING_VERSION
set /p VERSION=<"%VERSION_FILE%"
if not defined VERSION goto :MISSING_VERSION

title CatLayer v%VERSION%
echo ==============================================
echo   CatLayer v%VERSION% Launcher
echo ==============================================
echo.

if not exist "%SRC%" goto :MISSING_SOURCE
if not exist "%~dp0INSTALL.bat" goto :MISSING_INSTALL

rem Always rebuild so an old CatLayer.exe can never be reused by mistake.
echo [1/3] Building the current CatLayer source...
> "%BUILD_LOG%" echo CatLayer v%VERSION% build log
>>"%BUILD_LOG%" echo Source: "%SRC%"
>>"%BUILD_LOG%" echo.
call "%~dp0INSTALL.bat" --build-only >>"%BUILD_LOG%" 2>&1
if errorlevel 1 goto :BUILD_FAILED
if not exist "%EXE%" goto :NO_EXE

echo [BUILD OK] CatLayer.exe created.
echo [2/3] Starting CatLayer...
start "" "%EXE%"
if errorlevel 1 goto :LAUNCH_FAILED

rem start.exe can report success even when the GUI process dies immediately.
rem Give CatLayer a moment to initialize, then verify that it is still alive.
echo [3/3] Checking startup...
timeout /t 1 /nobreak >nul
tasklist /FI "IMAGENAME eq CatLayer.exe" 2>nul | find /I "CatLayer.exe" >nul
if errorlevel 1 goto :APP_EXITED

exit /b 0

:MISSING_VERSION
echo [ERROR] VERSION.txt is missing or empty.
echo Please extract the complete CatLayer package before running RUN.bat.
goto :STOP

:MISSING_SOURCE
echo [ERROR] CatLayer source file was not found.
echo Please extract the entire ZIP to a normal folder before running RUN.bat.
echo Missing source file: "src\CatLayer.cs"
goto :STOP

:MISSING_INSTALL
echo [ERROR] INSTALL.bat was not found next to RUN.bat.
echo Please extract the entire ZIP before running CatLayer.
goto :STOP

:BUILD_FAILED
echo.
echo [ERROR] CatLayer build failed.
echo.
echo ---------------- build log ----------------
type "%BUILD_LOG%"
echo ---------------------------------------------
goto :STOP

:NO_EXE
echo [ERROR] Build returned successfully, but CatLayer.exe does not exist.
echo Build log: "CatLayer_build.log"
goto :STOP

:LAUNCH_FAILED
echo [ERROR] Windows could not start CatLayer.exe.
goto :STOP

:APP_EXITED
echo.
echo [ERROR] CatLayer.exe exited within 3 seconds of startup.
echo The launcher will stay open so the cause can be checked.
echo.
if exist "%CRASH_LOG%" (
  echo ---------------- crash.log ----------------
  type "%CRASH_LOG%"
  echo ---------------------------------------------
) else (
  echo No crash.log was created.
  echo Build log: "CatLayer_build.log"
)
goto :STOP

:STOP
echo.
echo This window will stay open so the error can be photographed.
pause
exit /b 1
