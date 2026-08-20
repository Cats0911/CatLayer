@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "APPNAME=CatLayer"
set "VERSION_FILE=%~dp0VERSION.txt"
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
set "SRC=%~dp0src\CatLayer.cs"
set "UNINSTALL_SRC=%~dp0src\Uninstall.cs"
set "UPDATER_SRC=%~dp0src\Updater.cs"
set "ICON=%~dp0CatLayer.ico"
set "LOCAL_EXE=%~dp0CatLayer.exe"
set "LOCAL_UPDATER_EXE=%~dp0Updater.exe"
set "BUILD_EXE=%TEMP%\CatLayer_build_%RANDOM%_%RANDOM%.exe"
set "BUILD_UNINSTALL_EXE=%TEMP%\CatLayer_uninstall_%RANDOM%_%RANDOM%.exe"
set "BUILD_UPDATER_EXE=%TEMP%\CatLayer_updater_%RANDOM%_%RANDOM%.exe"
set "INSTALL_ROOT=%LOCALAPPDATA%\CatLayer"
set "INSTALL_DIR=%LOCALAPPDATA%\CatLayer\App"
set "INSTALLED_EXE=%LOCALAPPDATA%\CatLayer\App\CatLayer.exe"
set "UNINSTALL_EXE=%LOCALAPPDATA%\CatLayer\App\Uninstall.exe"
set "INSTALLED_UPDATER_EXE=%LOCALAPPDATA%\CatLayer\App\Updater.exe"
set "WEBVIEW2_VERSION=1.0.4129.50"
set "WEBVIEW_DIR=%~dp0lib\webview2"
set "WEBVIEW_CORE=%~dp0lib\webview2\Microsoft.Web.WebView2.Core.dll"
set "WEBVIEW_WINFORMS=%~dp0lib\webview2\Microsoft.Web.WebView2.WinForms.dll"
set "WEBVIEW_NATIVE_ROOT=%~dp0lib\webview2\native"
set "WINDOWSBASE_REF="
set "PRESENTATIONCORE_REF="
set "SYSTEMXAML_REF="
set "IOCOMPRESSION_REF="
set "IOCOMPRESSIONFS_REF="
set "BUILD_ONLY=0"
if /I "%~1"=="--build-only" set "BUILD_ONLY=1"

if "%BUILD_ONLY%"=="0" (
  title CatLayer v%VERSION% Installer
  echo ==============================================
  echo   CatLayer v%VERSION% Installer
  echo   .NET Framework 4.x / WinForms / csc.exe
  echo ==============================================
  echo.
)

call :ENSURE_WEBVIEW2_SDK
if errorlevel 1 goto :FAIL
call :FIND_CSC
if errorlevel 1 goto :FAIL
call :FIND_WPF_REFS
if errorlevel 1 goto :FAIL
call :BUILD_MAIN
if errorlevel 1 goto :FAIL
call :BUILD_UPDATER
if errorlevel 1 goto :FAIL
if "%BUILD_ONLY%"=="1" goto :SUCCESS_BUILD_ONLY
call :BUILD_UNINSTALL
if errorlevel 1 goto :FAIL
call :INSTALL_FILES
if errorlevel 1 goto :FAIL
call :REGISTER
if errorlevel 1 goto :FAIL
call :SHORTCUTS
if errorlevel 1 goto :FAIL

echo.
echo [OK] CatLayer v%VERSION% installed.
echo Install folder: "%INSTALL_DIR%"
echo Uninstaller:   "%UNINSTALL_EXE%"
echo.
echo The app is now listed in Windows Installed apps / Control Panel.
echo.
start "" "%INSTALLED_EXE%"
exit /b 0

:SUCCESS_BUILD_ONLY
if not exist "%LOCAL_EXE%" (
  echo [ERROR] CatLayer.exe was not created.
  exit /b 1
)
if not exist "%LOCAL_UPDATER_EXE%" (
  echo [ERROR] Updater.exe was not created.
  exit /b 1
)
echo [BUILD OK] "%LOCAL_EXE%"
echo [BUILD OK] "%LOCAL_UPDATER_EXE%"
exit /b 0



:ENSURE_WEBVIEW2_SDK
if exist "%WEBVIEW_CORE%" if exist "%WEBVIEW_WINFORMS%" if exist "%WEBVIEW_NATIVE_ROOT%\x64\WebView2Loader.dll" if exist "%WEBVIEW_NATIVE_ROOT%\x86\WebView2Loader.dll" if exist "%WEBVIEW_NATIVE_ROOT%\arm64\WebView2Loader.dll" exit /b 0
echo [WEB] Preparing Microsoft WebView2 SDK %WEBVIEW2_VERSION% ...
if not exist "%WEBVIEW_DIR%" mkdir "%WEBVIEW_DIR%" >nul 2>nul
if not exist "%~dp0tools\Prepare-WebView2.ps1" (
  echo [ERROR] Missing tools\Prepare-WebView2.ps1.
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Prepare-WebView2.ps1" -Version "%WEBVIEW2_VERSION%" -Destination "%WEBVIEW_DIR%"
if errorlevel 1 (
  echo [ERROR] Microsoft WebView2 SDK download/extraction failed.
  echo Internet access is required once for the first WebView2 SDK setup.
  exit /b 1
)
if not exist "%WEBVIEW_CORE%" exit /b 1
if not exist "%WEBVIEW_WINFORMS%" exit /b 1
if not exist "%WEBVIEW_NATIVE_ROOT%\x64\WebView2Loader.dll" exit /b 1
if not exist "%WEBVIEW_NATIVE_ROOT%\x86\WebView2Loader.dll" exit /b 1
if not exist "%WEBVIEW_NATIVE_ROOT%\arm64\WebView2Loader.dll" exit /b 1
exit /b 0
:COPY_WEBVIEW2_FILES
set "WV_TARGET=%~1"
if not defined WV_TARGET exit /b 1
if not exist "%WV_TARGET%" mkdir "%WV_TARGET%" >nul 2>nul
copy /Y "%WEBVIEW_CORE%" "%WV_TARGET%\Microsoft.Web.WebView2.Core.dll" >nul
if errorlevel 1 goto :WV_COPY_FAIL
copy /Y "%WEBVIEW_WINFORMS%" "%WV_TARGET%\Microsoft.Web.WebView2.WinForms.dll" >nul
if errorlevel 1 goto :WV_COPY_FAIL
for %%A in (x86 x64 arm64) do (
  if not exist "%WEBVIEW_NATIVE_ROOT%\%%A\WebView2Loader.dll" (
    echo [ERROR] WebView2Loader.dll for %%A was not found.
    exit /b 1
  )
  if not exist "%WV_TARGET%\runtimes\win-%%A\native" mkdir "%WV_TARGET%\runtimes\win-%%A\native" >nul 2>nul
  copy /Y "%WEBVIEW_NATIVE_ROOT%\%%A\WebView2Loader.dll" "%WV_TARGET%\runtimes\win-%%A\native\WebView2Loader.dll" >nul
  if errorlevel 1 goto :WV_COPY_FAIL
)
rem Do not place one architecture-specific loader in the app root for an AnyCPU build.
rem WebView2 resolves the correct loader from runtimes\win-<arch>\native.
if exist "%WV_TARGET%\WebView2Loader.dll" del /Q "%WV_TARGET%\WebView2Loader.dll" >nul 2>nul
exit /b 0

:WV_COPY_FAIL
echo [ERROR] Could not copy WebView2 dependency files to "%WV_TARGET%".
exit /b 1

:FIND_CSC
set "CSC="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not defined CSC (
  echo [ERROR] .NET Framework 4.x csc.exe was not found.
  exit /b 1
)
exit /b 0

:FIND_WPF_REFS
set "WINDOWSBASE_REF="
set "PRESENTATIONCORE_REF="
set "SYSTEMXAML_REF="
set "IOCOMPRESSION_REF="
set "IOCOMPRESSIONFS_REF="

rem Direct csc.exe builds do not always resolve WPF assemblies by short name.
rem Find the installed .NET Framework reference assemblies explicitly.
for %%V in (v4.8 v4.7.2 v4.7.1 v4.7 v4.6.2 v4.6.1 v4.6 v4.5.2 v4.5.1 v4.5 v4.0) do (
  if not defined WINDOWSBASE_REF if exist "%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\%%V\WindowsBase.dll" set "WINDOWSBASE_REF=%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\%%V\WindowsBase.dll"
  if not defined PRESENTATIONCORE_REF if exist "%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\%%V\PresentationCore.dll" set "PRESENTATIONCORE_REF=%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\%%V\PresentationCore.dll"
  if not defined SYSTEMXAML_REF if exist "%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\%%V\System.Xaml.dll" set "SYSTEMXAML_REF=%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\%%V\System.Xaml.dll"
  if not defined IOCOMPRESSION_REF if exist "%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\%%V\System.IO.Compression.dll" set "IOCOMPRESSION_REF=%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\%%V\System.IO.Compression.dll"
  if not defined IOCOMPRESSIONFS_REF if exist "%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\%%V\System.IO.Compression.FileSystem.dll" set "IOCOMPRESSIONFS_REF=%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\%%V\System.IO.Compression.FileSystem.dll"
)

rem Fallback to the desktop framework WPF folder when no reference pack path was found.
if not defined WINDOWSBASE_REF if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" set "WINDOWSBASE_REF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll"
if not defined WINDOWSBASE_REF if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF\WindowsBase.dll" set "WINDOWSBASE_REF=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF\WindowsBase.dll"
if not defined PRESENTATIONCORE_REF if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll" set "PRESENTATIONCORE_REF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll"
if not defined PRESENTATIONCORE_REF if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF\PresentationCore.dll" set "PRESENTATIONCORE_REF=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF\PresentationCore.dll"
if not defined SYSTEMXAML_REF if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Xaml.dll" set "SYSTEMXAML_REF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Xaml.dll"
if not defined SYSTEMXAML_REF if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\System.Xaml.dll" set "SYSTEMXAML_REF=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\System.Xaml.dll"
if not defined IOCOMPRESSION_REF if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.IO.Compression.dll" set "IOCOMPRESSION_REF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.IO.Compression.dll"
if not defined IOCOMPRESSION_REF if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\System.IO.Compression.dll" set "IOCOMPRESSION_REF=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\System.IO.Compression.dll"
if not defined IOCOMPRESSIONFS_REF if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.IO.Compression.FileSystem.dll" set "IOCOMPRESSIONFS_REF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.IO.Compression.FileSystem.dll"
if not defined IOCOMPRESSIONFS_REF if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\System.IO.Compression.FileSystem.dll" set "IOCOMPRESSIONFS_REF=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\System.IO.Compression.FileSystem.dll"

if not defined WINDOWSBASE_REF (
  echo [ERROR] WindowsBase.dll reference assembly was not found.
  echo Install/enable the Windows .NET Framework 4.x desktop components.
  exit /b 1
)
if not defined PRESENTATIONCORE_REF (
  echo [ERROR] PresentationCore.dll reference assembly was not found.
  echo Install/enable the Windows .NET Framework 4.x desktop components.
  exit /b 1
)
if not defined SYSTEMXAML_REF (
  echo [ERROR] System.Xaml.dll reference assembly was not found.
  echo Install/enable the Windows .NET Framework 4.x desktop components.
  exit /b 1
)
if not defined IOCOMPRESSION_REF (
  echo [ERROR] System.IO.Compression.dll reference assembly was not found.
  exit /b 1
)
if not defined IOCOMPRESSIONFS_REF (
  echo [ERROR] System.IO.Compression.FileSystem.dll reference assembly was not found.
  exit /b 1
)
exit /b 0
:BUILD_MAIN
if not exist "%SRC%" (
  echo [ERROR] Missing source file: "src\CatLayer.cs"
  exit /b 1
)
if "%BUILD_ONLY%"=="0" (echo [1/6] Building CatLayer.exe ...) else (echo [BUILD] Compiling CatLayer.exe ...)
if exist "%BUILD_EXE%" del /Q "%BUILD_EXE%" >nul 2>nul
if exist "%ICON%" (
  "%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /main:CatLayer.Program /win32icon:"%ICON%" /out:"%BUILD_EXE%" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:"%WINDOWSBASE_REF%" /reference:"%PRESENTATIONCORE_REF%" /reference:"%SYSTEMXAML_REF%" /reference:"%WEBVIEW_CORE%" /reference:"%WEBVIEW_WINFORMS%" "%SRC%"
) else (
  "%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /main:CatLayer.Program /out:"%BUILD_EXE%" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:"%WINDOWSBASE_REF%" /reference:"%PRESENTATIONCORE_REF%" /reference:"%SYSTEMXAML_REF%" /reference:"%WEBVIEW_CORE%" /reference:"%WEBVIEW_WINFORMS%" "%SRC%"
)
if errorlevel 1 (
  echo [ERROR] CatLayer.exe compile failed.
  exit /b 1
)
rem Build succeeded. Close the current CatLayer only when the new EXE is ready to replace it.
taskkill /IM CatLayer.exe /F >nul 2>nul
copy /Y "%BUILD_EXE%" "%LOCAL_EXE%" >nul
if errorlevel 1 (
  echo [ERROR] Could not write CatLayer.exe.
  exit /b 1
)
call :COPY_WEBVIEW2_FILES "%~dp0"
if errorlevel 1 exit /b 1
del /Q "%BUILD_EXE%" >nul 2>nul
exit /b 0


:BUILD_UPDATER
if not exist "%UPDATER_SRC%" (
  echo [ERROR] Missing source file: "src\Updater.cs"
  exit /b 1
)
if "%BUILD_ONLY%"=="0" (echo [2/6] Building Updater.exe ...) else (echo [BUILD] Compiling Updater.exe ...)
if exist "%BUILD_UPDATER_EXE%" del /Q "%BUILD_UPDATER_EXE%" >nul 2>nul
if exist "%ICON%" (
  "%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /main:CatLayerUpdater.Program /win32icon:"%ICON%" /out:"%BUILD_UPDATER_EXE%" /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:"%IOCOMPRESSION_REF%" /reference:"%IOCOMPRESSIONFS_REF%" "%UPDATER_SRC%"
) else (
  "%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /main:CatLayerUpdater.Program /out:"%BUILD_UPDATER_EXE%" /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:"%IOCOMPRESSION_REF%" /reference:"%IOCOMPRESSIONFS_REF%" "%UPDATER_SRC%"
)
if errorlevel 1 (
  echo [ERROR] Updater.exe compile failed.
  exit /b 1
)
if not exist "%BUILD_UPDATER_EXE%" (
  echo [ERROR] Updater.exe was not created.
  exit /b 1
)
copy /Y "%BUILD_UPDATER_EXE%" "%LOCAL_UPDATER_EXE%" >nul
if errorlevel 1 (
  echo [ERROR] Could not write Updater.exe.
  exit /b 1
)
del /Q "%BUILD_UPDATER_EXE%" >nul 2>nul
exit /b 0
:BUILD_UNINSTALL
if not exist "%UNINSTALL_SRC%" (
  echo [ERROR] Missing source file: "src\Uninstall.cs"
  exit /b 1
)
echo [3/6] Building Uninstall.exe ...
if exist "%BUILD_UNINSTALL_EXE%" del /Q "%BUILD_UNINSTALL_EXE%" >nul 2>nul
if exist "%ICON%" (
  "%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /main:CatLayerUninstall.Program /win32icon:"%ICON%" /out:"%BUILD_UNINSTALL_EXE%" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "%UNINSTALL_SRC%"
) else (
  "%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /main:CatLayerUninstall.Program /out:"%BUILD_UNINSTALL_EXE%" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "%UNINSTALL_SRC%"
)
if errorlevel 1 (
  echo [ERROR] Uninstall.exe compile failed.
  exit /b 1
)
if not exist "%BUILD_UNINSTALL_EXE%" (
  echo [ERROR] Uninstall.exe was not created.
  exit /b 1
)
exit /b 0

:INSTALL_FILES
echo [4/6] Installing files ...
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%" >nul 2>nul
if not exist "%INSTALL_DIR%\assets" mkdir "%INSTALL_DIR%\assets" >nul 2>nul
if not exist "%INSTALL_DIR%\obs" mkdir "%INSTALL_DIR%\obs" >nul 2>nul
if not exist "%INSTALL_DIR%\examples" mkdir "%INSTALL_DIR%\examples" >nul 2>nul
copy /Y "%LOCAL_EXE%" "%INSTALLED_EXE%" >nul
if errorlevel 1 (
  echo [ERROR] Could not install CatLayer.exe.
  exit /b 1
)
copy /Y "%LOCAL_UPDATER_EXE%" "%INSTALLED_UPDATER_EXE%" >nul
if errorlevel 1 (
  echo [ERROR] Could not install Updater.exe.
  exit /b 1
)
copy /Y "%BUILD_UNINSTALL_EXE%" "%UNINSTALL_EXE%" >nul
if errorlevel 1 (
  echo [ERROR] Could not install Uninstall.exe.
  exit /b 1
)
del /Q "%BUILD_UNINSTALL_EXE%" >nul 2>nul
if exist "%ICON%" copy /Y "%ICON%" "%INSTALL_DIR%\CatLayer.ico" >nul 2>nul
copy /Y "%VERSION_FILE%" "%INSTALL_DIR%\VERSION.txt" >nul
if errorlevel 1 (
  echo [ERROR] Could not install VERSION.txt.
  exit /b 1
)
if exist "%~dp0*.txt" copy /Y "%~dp0*.txt" "%INSTALL_DIR%\" >nul 2>nul
if exist "%~dp0assets\*" xcopy /E /I /Y "%~dp0assets\*" "%INSTALL_DIR%\assets" >nul
if exist "%~dp0obs\CatLayer_OBS_Bridge.lua" copy /Y "%~dp0obs\CatLayer_OBS_Bridge.lua" "%INSTALL_DIR%\obs\CatLayer_OBS_Bridge.lua" >nul 2>nul
if exist "%~dp0examples\*" xcopy /E /I /Y "%~dp0examples\*" "%INSTALL_DIR%\examples" >nul
call :COPY_WEBVIEW2_FILES "%INSTALL_DIR%"
if errorlevel 1 exit /b 1
if not exist "%UNINSTALL_EXE%" (
  echo [ERROR] Installed Uninstall.exe is missing.
  exit /b 1
)
exit /b 0

:REGISTER
echo [5/6] Registering uninstaller ...
"%UNINSTALL_EXE%" --register
if errorlevel 1 (
  echo [ERROR] Windows uninstall registration failed.
  exit /b 1
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\CatLayer" /v UninstallString >nul 2>nul
if errorlevel 1 (
  echo [ERROR] Windows uninstall entry was not found after registration.
  exit /b 1
)
reg add "HKCU\Software\Classes\.catlayerpreset" /ve /d "CatLayer.Preset" /f >nul 2>nul
reg add "HKCU\Software\Classes\CatLayer.Preset" /ve /d "CatLayer 프리셋" /f >nul 2>nul
reg add "HKCU\Software\Classes\CatLayer.Preset\DefaultIcon" /ve /d "\"%INSTALLED_EXE%\",0" /f >nul 2>nul
reg add "HKCU\Software\Classes\CatLayer.Preset\shell\open\command" /ve /d "\"%INSTALLED_EXE%\" \"%%1\"" /f >nul 2>nul
reg add "HKCU\Software\Classes\.catlayergroup" /ve /d "CatLayer.Group" /f >nul 2>nul
reg add "HKCU\Software\Classes\CatLayer.Group" /ve /d "CatLayer 그룹" /f >nul 2>nul
reg add "HKCU\Software\Classes\CatLayer.Group\DefaultIcon" /ve /d "\"%INSTALLED_EXE%\",0" /f >nul 2>nul
reg add "HKCU\Software\Classes\CatLayer.Group\shell\open\command" /ve /d "\"%INSTALLED_EXE%\" \"%%1\"" /f >nul 2>nul
reg add "HKCU\Software\Classes\.catlayerweb" /ve /d "CatLayer.WebPackage" /f >nul 2>nul
reg add "HKCU\Software\Classes\CatLayer.WebPackage" /ve /d "CatLayer Web" /f >nul 2>nul
reg add "HKCU\Software\Classes\CatLayer.WebPackage\DefaultIcon" /ve /d "\"%INSTALLED_EXE%\",0" /f >nul 2>nul
reg add "HKCU\Software\Classes\CatLayer.WebPackage\shell\open\command" /ve /d "\"%INSTALLED_EXE%\" \"%%1\"" /f >nul 2>nul
exit /b 0

:SHORTCUTS
echo [6/6] Creating shortcuts ...
set "TARGET=%INSTALLED_EXE%"
set "WORKDIR=%INSTALL_DIR%"
set "UNINSTALL_TARGET=%UNINSTALL_EXE%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$w=New-Object -ComObject WScript.Shell;$d=[Environment]::GetFolderPath('Desktop');$p=[Environment]::GetFolderPath('Programs');$s=$w.CreateShortcut((Join-Path $d 'CatLayer.lnk'));$s.TargetPath=$env:TARGET;$s.WorkingDirectory=$env:WORKDIR;$s.IconLocation=$env:TARGET+',0';$s.Save();$s=$w.CreateShortcut((Join-Path $p 'CatLayer.lnk'));$s.TargetPath=$env:TARGET;$s.WorkingDirectory=$env:WORKDIR;$s.IconLocation=$env:TARGET+',0';$s.Save();$s=$w.CreateShortcut((Join-Path $p 'Uninstall CatLayer.lnk'));$s.TargetPath=$env:UNINSTALL_TARGET;$s.WorkingDirectory=$env:WORKDIR;$s.IconLocation=$env:UNINSTALL_TARGET+',0';$s.Save()"
if errorlevel 1 (
  echo [ERROR] Shortcut creation failed.
  exit /b 1
)
exit /b 0

:FAIL
echo.
echo [ERROR] Installation failed. Check the message above.
if "%BUILD_ONLY%"=="0" pause
exit /b 1
