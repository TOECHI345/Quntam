@echo off
REM ============================================================================
REM  find_and_build.bat  -  finds your ATAS install, builds the indicator,
REM  and installs the DLL. No paths to edit. Double-click it.
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
echo.

echo Building the indicator...
dotnet build -c Release -p:ATAS_BASE="%ATAS_BASE%" > build_log.txt 2>&1
if errorlevel 1 (
  echo.
  echo [X] Build failed. Opening the log - please send build_log.txt to Claude.
  start "" notepad "build_log.txt"
  pause
  exit /b 1
)

set "DEST=%USERPROFILE%\Documents\ATAS\Indicators"
if not exist "%DEST%" mkdir "%DEST%"
set "OK="
for /r "%~dp0bin\Release" %%f in (OrderFlowAuctionSuite.dll) do copy /y "%%f" "%DEST%" >nul && set "OK=1"
if not defined OK (
  echo [X] Build said success but no DLL was found. Opening the log.
  start "" notepad "build_log.txt"
  pause
  exit /b 1
)

echo.
echo ============================================================
echo  [OK] Installed OrderFlowAuctionSuite.dll to:
echo       %DEST%
echo  Now fully restart ATAS and add "Order Flow Auction Suite".
echo ============================================================
echo.
pause
