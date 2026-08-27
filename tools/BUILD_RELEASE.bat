@echo off
setlocal EnableExtensions EnableDelayedExpansion
for %%I in ("%~dp0..") do set "DATA=%%~fI"
for %%I in ("%DATA%\..") do set "ROOT=%%~fI"
cd /d "%ROOT%"

set "VERSION_FILE=%DATA%\VERSION.txt"
if not exist "%VERSION_FILE%" (
  echo [ERROR] Missing VERSION.txt.
  pause
  exit /b 1
)
set /p VERSION=<"%VERSION_FILE%"
if not defined VERSION (
  echo [ERROR] VERSION.txt is empty.
  pause
  exit /b 1
)

set "RELEASE_DIR=%ROOT%\release"
set "PAYLOAD_DIR=%TEMP%\CatLayer_release_%VERSION%_%RANDOM%_%RANDOM%"
set "UPDATE_ZIP=%RELEASE_DIR%\CatLayer_v%VERSION%.zip"
set "SHA_FILE=%RELEASE_DIR%\SHA256.txt"

if exist "%PAYLOAD_DIR%" rmdir /S /Q "%PAYLOAD_DIR%" >nul 2>nul
if not exist "%RELEASE_DIR%" mkdir "%RELEASE_DIR%" >nul 2>nul
if exist "%UPDATE_ZIP%" del /Q "%UPDATE_ZIP%" >nul 2>nul
if exist "%SHA_FILE%" del /Q "%SHA_FILE%" >nul 2>nul

cls
echo ==============================================
echo   CatLayer v%VERSION% GitHub Release Builder
echo ==============================================
echo.
echo [1/4] Building CatLayer.exe and Updater.exe ...
call "%ROOT%\INSTALL.bat" --build-only
if errorlevel 1 goto :FAIL

if not exist "%DATA%\CatLayer.exe" (
  echo [ERROR] CatLayer.exe was not created.
  goto :FAIL
)
if not exist "%DATA%\Updater.exe" (
  echo [ERROR] Updater.exe was not created.
  goto :FAIL
)

echo [2/4] Preparing update payload ...
mkdir "%PAYLOAD_DIR%" >nul 2>nul
copy /Y "%DATA%\CatLayer.exe" "%PAYLOAD_DIR%\CatLayer.exe" >nul || goto :FAIL
copy /Y "%DATA%\Updater.exe" "%PAYLOAD_DIR%\Updater.exe" >nul || goto :FAIL
copy /Y "%VERSION_FILE%" "%PAYLOAD_DIR%\VERSION.txt" >nul || goto :FAIL
if exist "%DATA%\CatLayer.ico" copy /Y "%DATA%\CatLayer.ico" "%PAYLOAD_DIR%\CatLayer.ico" >nul
if exist "%DATA%\Microsoft.Web.WebView2.Core.dll" copy /Y "%DATA%\Microsoft.Web.WebView2.Core.dll" "%PAYLOAD_DIR%\Microsoft.Web.WebView2.Core.dll" >nul
if exist "%DATA%\Microsoft.Web.WebView2.WinForms.dll" copy /Y "%DATA%\Microsoft.Web.WebView2.WinForms.dll" "%PAYLOAD_DIR%\Microsoft.Web.WebView2.WinForms.dll" >nul
if exist "%DATA%\runtimes\*" xcopy /E /I /Y "%DATA%\runtimes\*" "%PAYLOAD_DIR%\runtimes" >nul
if exist "%DATA%\assets\*" xcopy /E /I /Y "%DATA%\assets\*" "%PAYLOAD_DIR%\assets" >nul
if exist "%DATA%\obs\*" xcopy /E /I /Y "%DATA%\obs\*" "%PAYLOAD_DIR%\obs" >nul
if exist "%DATA%\design\*" xcopy /E /I /Y "%DATA%\design\*" "%PAYLOAD_DIR%\design" >nul
if exist "%DATA%\websdk\*" xcopy /E /I /Y "%DATA%\websdk\*" "%PAYLOAD_DIR%\websdk" >nul
if exist "%DATA%\examples\" (
  robocopy "%DATA%\examples" "%PAYLOAD_DIR%\examples" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
  if errorlevel 8 goto :FAIL
)
for %%F in (README_KO.txt EDIT_GUIDE_KO.txt TERMS_KO.txt RELEASE_NOTES_v%VERSION%.txt) do (
  if exist "%DATA%\%%F" copy /Y "%DATA%\%%F" "%PAYLOAD_DIR%\%%F" >nul
)

rem Validate the exact payload required by the installed updater before zipping.
if not exist "%PAYLOAD_DIR%\CatLayer.exe" (
  echo [ERROR] Payload is missing CatLayer.exe.
  goto :FAIL
)
if not exist "%PAYLOAD_DIR%\Updater.exe" (
  echo [ERROR] Payload is missing Updater.exe.
  goto :FAIL
)
if not exist "%PAYLOAD_DIR%\VERSION.txt" (
  echo [ERROR] Payload is missing VERSION.txt.
  goto :FAIL
)
set "PS_EXAMPLES=%PAYLOAD_DIR%\examples"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $files=@(Get-ChildItem -LiteralPath $env:PS_EXAMPLES -File | Where-Object { $_.Extension -ieq '.catlayerweb' }); Write-Host ('[CHECK] Default widgets found: ' + $files.Count); if ($files.Count -ne 5) { exit 17 }"
if errorlevel 17 (
  echo [ERROR] Expected exactly 5 default .catlayerweb widgets.
  goto :FAIL
)
if errorlevel 1 goto :FAIL

echo [3/4] Creating update ZIP ...
set "PS_PAYLOAD=%PAYLOAD_DIR%"
set "PS_ZIP=%UPDATE_ZIP%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; Compress-Archive -Path (Join-Path $env:PS_PAYLOAD '*') -DestinationPath $env:PS_ZIP -CompressionLevel Optimal -Force"
if errorlevel 1 goto :FAIL
if not exist "%UPDATE_ZIP%" (
  echo [ERROR] Update ZIP was not created.
  goto :FAIL
)

echo [4/4] Creating SHA256.txt ...
set "PS_SHA=%SHA_FILE%"
set "PS_NAME=CatLayer_v%VERSION%.zip"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $h=(Get-FileHash -Algorithm SHA256 -LiteralPath $env:PS_ZIP).Hash.ToLowerInvariant(); Set-Content -LiteralPath $env:PS_SHA -Encoding ASCII -NoNewline -Value ($h + '  ' + $env:PS_NAME)"
if errorlevel 1 goto :FAIL
if not exist "%SHA_FILE%" (
  echo [ERROR] SHA256.txt was not created.
  goto :FAIL
)

rmdir /S /Q "%PAYLOAD_DIR%" >nul 2>nul

echo.
echo ==============================================
echo   RELEASE FILES READY
echo ==============================================
echo.
echo Upload BOTH files to the GitHub Release assets:
echo   1. release\CatLayer_v%VERSION%.zip
echo   2. release\SHA256.txt
echo.
echo Release tag must be: v%VERSION%
echo Repository expected by CatLayer: Cats0911/CatLayer
echo Draft / Pre-release must be OFF for automatic update.
echo.
start "" "%RELEASE_DIR%"
pause
exit /b 0

:FAIL
rmdir /S /Q "%PAYLOAD_DIR%" >nul 2>nul
echo.
echo [ERROR] GitHub release package creation failed.
pause
exit /b 1
