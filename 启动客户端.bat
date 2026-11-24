@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "CSPROJ=%ROOT%Client\Client.csproj"
set "CLIENT_DIR=%ROOT%Client\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64"
set "CLIENT_EXE=%CLIENT_DIR%\Client.exe"

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
    echo [ERROR] Client.exe not found after build: %CLIENT_EXE%
    pause
    exit /b 1
)

echo [OK] Launching Client.exe...
start "" /D "%CLIENT_DIR%" "%CLIENT_EXE%"
exit /b 0
