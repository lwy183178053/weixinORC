@echo off
setlocal EnableExtensions
title WeixinORC Go Server (127.0.0.1:28765)

set "ROOT=%~dp0"
set "SERVER_DIR=%ROOT%Server"
set "SERVER_ADDR=127.0.0.1:28765"
set "HEALTH_URL=http://%SERVER_ADDR%/healthz"

if not exist "%SERVER_DIR%\main.go" (
    echo [ERROR] Server directory not found: %SERVER_DIR%
    pause
    exit /b 1
)

where go >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Go toolchain not found in PATH. Please install Go 1.24+ first.
    pause
    exit /b 1
)

curl.exe -fsS --max-time 1 "%HEALTH_URL%" >nul 2>&1
if not errorlevel 1 (
    echo [OK] WeixinORC Go Server is already running on http://%SERVER_ADDR%
    echo      Demo account: demo / demo123
    pause
    exit /b 0
)

set "GOOS=windows"
set "GOARCH=amd64"
set "MOCK_AUTH=1"
set "MOCK_ACCOUNT=demo"
set "MOCK_PASSWORD=demo123"

echo ============================================================
echo   WeixinORC Go Server
echo   Address : http://%SERVER_ADDR%
echo   Account : %MOCK_ACCOUNT% / %MOCK_PASSWORD%
echo ============================================================
echo.

cd /d "%SERVER_DIR%"
go run .
set "EXIT_CODE=%ERRORLEVEL%"
echo.
echo [INFO] Go Server exited with code %EXIT_CODE%.
pause
exit /b %EXIT_CODE%
