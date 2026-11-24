@echo off
setlocal
set "ROOT=%~dp0"
echo Starting Go Server (http://127.0.0.1:28765, demo / demo123)...
start "WeixinORC Go Server" cmd /k "set GOOS=windows && set GOARCH=amd64 && set MOCK_AUTH=1 && set MOCK_ACCOUNT=demo && set MOCK_PASSWORD=demo123 && cd /d ""%ROOT%Server"" && go run ."
timeout /t 2 /nobreak >nul
call "%ROOT%Æô¶¯¿Í»§¶Ë.bat"
