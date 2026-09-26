@echo off
REM ============================================================================
REM  Over Power Buy/Sell - one-click build for classic ATAS (Windows)
REM
REM  Prereq (once): install the .NET SDK from https://dotnet.microsoft.com/download
REM                 (pick the version matching ATAS - usually .NET 8 or .NET 10).
REM
REM  Then: keep this .bat next to OverPowerBuySell.cs and OverPowerBuySell.csproj
REM        and double-click it. It builds the DLL and copies it into your ATAS
REM        indicators folder. Restart ATAS afterwards and add "Over Power Buy/Sell".
REM ============================================================================
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo [X] .NET SDK not found. Install it first: https://dotnet.microsoft.com/download
  echo.
  pause
  exit /b 1
)

echo Building OverPowerBuySell (Release)...
dotnet build -c Release
if errorlevel 1 (
  echo.
  echo [X] BUILD FAILED. If the error mentions the target framework, open
  echo     OverPowerBuySell.csproj in Notepad and change net10.0 to net8.0
  echo     (check OFT.Platform.runtimeconfig.json in your ATAS folder for "tfm").
  echo.
  pause
  exit /b 1
)

set "DEST=%USERPROFILE%\Documents\ATAS\Indicators"
if not exist "%DEST%" mkdir "%DEST%"

set "FOUND="
for /r "%~dp0bin\Release" %%f in (OverPowerBuySell.dll) do (
  copy /y "%%f" "%DEST%" >nul
  set "FOUND=1"
)

if not defined FOUND (
  echo [X] Built, but OverPowerBuySell.dll was not found under bin\Release.
  pause
  exit /b 1
)

echo.
echo [OK] DLL copied to: %DEST%
echo      Restart ATAS, then add "Over Power Buy/Sell" to your chart.
echo.
pause
