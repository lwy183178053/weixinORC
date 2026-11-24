@echo off
setlocal
set "ROOT=%~dp0"
set "CLIENT_DIR=%ROOT%Client\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64"
set "CLIENT_EXE=%CLIENT_DIR%\Client.exe"
if not exist "%CLIENT_EXE%" (
    echo Building WinUI 3 Client...
    dotnet build "%ROOT%Client\Client.csproj" -p:Platform=x64 -v minimal
    if errorlevel 1 (
        pause
        exit /b 1
    )
)
start "" /D "%CLIENT_DIR%" "%CLIENT_EXE%"
