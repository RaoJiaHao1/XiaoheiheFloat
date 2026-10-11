@echo off
rem ============================================================
rem  XiaoheiheFloat launcher (ASCII + CRLF on purpose: cmd.exe
rem  parses .bat bytes with the active code page, non-ASCII text
rem  inside a batch file can break parsing).
rem  Dependencies: .NET 10 Desktop Runtime + Edge WebView2 Runtime.
rem ============================================================
setlocal
cd /d "%~dp0"

set "APP=bin\Release\net10.0-windows\XiaoheiheFloat.exe"
if not exist "%APP%" set "APP=dist\XiaoheiheFloat.exe"

if not exist "%APP%" (
  echo [ERROR] XiaoheiheFloat.exe not found.
  echo Build it first:  dotnet build -c Release
  pause
  exit /b 1
)

if not defined WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS set "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--autoplay-policy=no-user-gesture-required"

start "" "%APP%" %*
exit /b 0
