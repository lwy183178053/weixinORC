@echo off
setlocal EnableExtensions
title WeixinORC Dev Launcher

set "ROOT=%~dp0"
set "SERVER_DIR=%ROOT%Server"
set "CSPROJ=%ROOT%Client\Client.csproj"
set "CLIENT_DIR=%ROOT%Client\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64"
set "CLIENT_EXE=%CLIENT_DIR%\Client.exe"
set "SERVER_ADDR=127.0.0.1:28765"
set "HEALTH_URL=http://%SERVER_ADDR%/healthz"

echo ============================================================
echo   WeixinORC Local Dev Environment Launcher
echo   Backend : http://%SERVER_ADDR%  ^(demo / demo123^)
echo ============================================================
echo.

curl.exe -fsS --max-time 1 "%HEALTH_URL%" >nul 2>&1
if not errorlevel 1 (
    echo [OK] Go backend is already running on http://%SERVER_ADDR%
    goto :launch_client
)

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

echo [1/2] Starting Go backend in a new window...
set "GOOS=windows"
set "GOARCH=amd64"
set "MOCK_AUTH=1"
set "MOCK_ACCOUNT=demo"
set "MOCK_PASSWORD=demo123"
start "WeixinORC Go Server" /D "%SERVER_DIR%" cmd /k "go run ."

echo [INFO] Waiting for backend health check ^(first run may take 15-30s^)...
set /a WAIT_SEC=0
:wait_server
curl.exe -fsS --max-time 1 "%HEALTH_URL%" >nul 2>&1
if not errorlevel 1 (
    echo [OK] Go backend is ready!
    goto :launch_client
)
set /a WAIT_SEC+=1
if %WAIT_SEC% GEQ 30 (
    echo [WARN] Backend did not respond within 30s. Check the Go Server window for errors.
    echo [INFO] Proceeding to launch client anyway...
    goto :launch_client
)
timeout /t 1 /nobreak >nul
goto :wait_server

:launch_client
echo.
echo [2/2] Preparing WinUI 3 Client...
set "NEED_BUILD=0"
if /i "%~1"=="--rebuild" set "NEED_BUILD=1"
if not exist "%CLIENT_EXE%" set "NEED_BUILD=1"

if "%NEED_BUILD%"=="1" (
    if not exist "%CSPROJ%" (
        echo [ERROR] Client project not found: %CSPROJ%
        pause
        exit /b 1
    )

    where dotnet >nul 2>&1
    if errorlevel 1 (
        echo [ERROR] .NET SDK ^(dotnet^) not found in PATH. Please install .NET 10 SDK first.
        pause
        exit /b 1
    )

    echo [INFO] Building WinUI 3 Client ^(x64^)...
    dotnet build "%CSPROJ%" -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -v minimal
    if errorlevel 1 (
        echo.
        echo [ERROR] Client build failed. Check the messages above.
        pause
        exit /b 1
    )
)

if not exist "%CLIENT_EXE%" (
    echo [ERROR] Client.exe not found: %CLIENT_EXE%
    pause
    exit /b 1
)

echo [OK] Launching Client.exe...
start "" /D "%CLIENT_DIR%" "%CLIENT_EXE%"
exit /b 0
