@echo off
REM ============================================================================
REM  find_and_build.bat  -  finds your ATAS install, builds the correct variant
REM  (classic ATAS or ATAS X automatically), and installs the DLL. Double-click.
REM ============================================================================
setlocal enableextensions
cd /d "%~dp0"

echo Locating your ATAS installation...
set "ATAS_BASE="

for %%D in (
  "%LocalAppData%\ATAS Platform"
  "%ProgramFiles%\ATAS Platform"
  "%ProgramFiles(x86)%\ATAS Platform"
  "%AppData%\ATAS Platform"
  "%LocalAppData%\Programs\ATAS Platform"
  "%ProgramW6432%\ATAS Platform"
  "%ProgramFiles%\ATAS X"
  "%LocalAppData%\ATAS X"
) do if not defined ATAS_BASE if exist "%%~D\ATAS.Indicators.dll" set "ATAS_BASE=%%~D"

if not defined ATAS_BASE (
  echo Not in the usual folders. Searching your drives, please wait a minute...
  for %%V in (C D E F G) do if exist "%%V:\" call :scan %%V
)
goto :afterscan

:scan
for /f "delims=" %%F in ('dir /s /b "%~1:\ATAS.Indicators.dll" 2^>nul') do if not defined ATAS_BASE for %%G in ("%%F") do set "ATAS_BASE=%%~dpG"
exit /b

:afterscan
if not defined ATAS_BASE (
  echo.
  echo [X] Could not find ATAS.Indicators.dll anywhere.
  echo     Open ATAS, then open Task Manager, right-click the ATAS process,
  echo     choose "Open file location", copy that folder path and send it to Claude.
  echo.
  pause
  exit /b 1
)
if "%ATAS_BASE:~-1%"=="\" set "ATAS_BASE=%ATAS_BASE:~0,-1%"
echo Found ATAS at: "%ATAS_BASE%"

REM ATAS X needs the cross-platform (non-WPF) build so the indicator loads.
set "PLATARG="
echo "%ATAS_BASE%" | find /i "ATAS X" >nul && set "PLATARG=-p:Platform=Cross"
if defined PLATARG (echo Detected ATAS X -> building the cross-platform variant.) else (echo Detected classic ATAS -> building the standard variant.)
echo.

REM clean previous output so only the fresh build is installed
if exist "%~dp0bin" rmdir /s /q "%~dp0bin"

echo Building the indicator...
dotnet build -c Release %PLATARG% -p:ATAS_BASE="%ATAS_BASE%" > build_log.txt 2>&1
if errorlevel 1 (
  echo.
  echo [X] Build failed. Opening the log - please send build_log.txt to Claude.
  start "" notepad "build_log.txt"
  pause
  exit /b 1
)

REM install to every ATAS indicators folder we can find
set "OK="
call :install "%USERPROFILE%\Documents\ATAS\Indicators"
call :install "%USERPROFILE%\Documents\ATAS X\Indicators"
call :install "%LocalAppData%\ATAS X\Indicators"
if not defined OK (
  echo [X] Build succeeded but no DLL was produced. Opening the log.
  start "" notepad "build_log.txt"
  pause
  exit /b 1
)

echo.
echo ============================================================
echo  [OK] Installed OrderFlowAuctionSuite.dll.
echo  Now FULLY restart ATAS and add "Order Flow Auction Suite"
echo  (look under the Custom category).
echo ============================================================
echo.
pause
exit /b 0

:install
set "DEST=%~1"
if not exist "%DEST%" mkdir "%DEST%" 2>nul
for /r "%~dp0bin\Release" %%f in (OrderFlowAuctionSuite.dll) do (
  copy /y "%%f" "%DEST%" >nul 2>nul && (set "OK=1" & echo   installed to: %DEST%)
)
exit /b
