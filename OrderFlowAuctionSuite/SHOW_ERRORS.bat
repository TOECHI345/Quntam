@echo off
REM ============================================================================
REM  SHOW_ERRORS.bat  -  builds the project, saves the full output to
REM  build_log.txt, and opens it in Notepad so you can copy/upload the errors.
REM  Put this next to OrderFlowAuctionSuite.csproj and double-click it.
REM ============================================================================
cd /d "%~dp0"
echo Building and capturing output... please wait.
dotnet build -c Release > build_log.txt 2>&1
echo Done. Opening the log...
start "" notepad "build_log.txt"
