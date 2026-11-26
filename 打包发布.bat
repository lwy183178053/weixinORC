@echo off
chcp 65001 >nul
setlocal EnableExtensions

title 智美工作台 - 一键打包发布 (Client + Server)

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

set "CLIENT_CSPROJ=%ROOT%\Client\Client.csproj"
set "SERVER_DIR=%ROOT%\Server"
set "OUTPUT_DIR=%ROOT%\output"
set "OUT_CLIENT=%OUTPUT_DIR%\client"
set "OUT_SERVER=%OUTPUT_DIR%\server"
set "STAGE_CLIENT=%OUTPUT_DIR%\_client_stage"

echo ============================================================
echo   智美工作台 - 一键打包发布脚本
echo   输出目录: %OUTPUT_DIR%
echo     - 客户端: %OUT_CLIENT%
echo     - 服务端: %OUT_SERVER%
echo ============================================================
echo.

:: 1. 检查源文件与工具链
if not exist "%CLIENT_CSPROJ%" (
    echo [错误] 未找到客户端项目文件: %CLIENT_CSPROJ%
    pause
    exit /b 1
)
if not exist "%SERVER_DIR%\main.go" (
    echo [错误] 未找到服务端入口文件: %SERVER_DIR%\main.go
    pause
    exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [错误] 未在 PATH 中找到 dotnet SDK，请先安装 .NET 10 SDK。
    pause
    exit /b 1
)

where go >nul 2>&1
if errorlevel 1 (
    echo [错误] 未在 PATH 中找到 Go 工具链，请先安装 Go 1.24+。
    pause
    exit /b 1
)

:: 2. 检查目标进程是否占用输出文件
tasklist /FI "IMAGENAME eq Client.exe" 2>nul | find /I "Client.exe" >nul
if not errorlevel 1 (
    echo [提示] 检测到 Client.exe 正在运行，若占用 output\client 将导致覆盖失败。
    echo        建议先关闭已运行的客户端后再打包，或按任意键继续尝试...
    echo.
)

tasklist /FI "IMAGENAME eq weixinorc-server.exe" 2>nul | find /I "weixinorc-server.exe" >nul
if not errorlevel 1 (
    echo [错误] 检测到 weixinorc-server.exe 正在运行，请先关闭后再执行打包！
    pause
    exit /b 1
)

if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

:: ------------------------------------------------------------
:: 3. 编译并覆盖打包 Server -> output\server
:: ------------------------------------------------------------
echo [1/2] 正在编译 Go 服务端 (windows/amd64)...
if not exist "%OUT_SERVER%" mkdir "%OUT_SERVER%"
if not exist "%OUT_SERVER%\data" mkdir "%OUT_SERVER%\data"

pushd "%SERVER_DIR%"
set "GOOS=windows"
set "GOARCH=amd64"
set "CGO_ENABLED=0"
go build -trimpath -ldflags="-s -w" -o "%OUT_SERVER%\weixinorc-server.exe" .
set "GO_ERR=%ERRORLEVEL%"
popd

if not "%GO_ERR%"=="0" (
    echo.
    echo [错误] 服务端编译失败，退出码: %GO_ERR%
    if /i not "%~1"=="--no-pause" pause
    exit /b %GO_ERR%
)

:: 生成配套的一键启动服务端脚本（覆盖更新，保留 data 目录里的历史数据库）
(
    echo @echo off
    echo chcp 65001 ^>nul
    echo setlocal EnableExtensions
    echo title 智美工作台服务端 ^(127.0.0.1:28765^)
    echo.
    echo cd /d "%%~dp0"
    echo set "SERVER_ADDR=127.0.0.1:28765"
    echo set "DATABASE_PATH=data/weixinorc.db"
    echo set "MOCK_AUTH=1"
    echo set "MOCK_ACCOUNT=demo"
    echo set "MOCK_PASSWORD=demo123"
    echo.
    echo echo ============================================================
    echo echo   智美工作台服务端
    echo echo   服务地址: http://%%SERVER_ADDR%%
    echo echo   管理后台: http://%%SERVER_ADDR%%/admin
    echo echo   默认账号: %%MOCK_ACCOUNT%% / %%MOCK_PASSWORD%%
    echo echo ============================================================
    echo echo.
    echo.
    echo weixinorc-server.exe
    echo pause
) > "%OUT_SERVER%\启动服务端.bat"

echo [完成] 服务端已输出至: %OUT_SERVER%\weixinorc-server.exe
echo.

:: ------------------------------------------------------------
:: 4. 发布并覆盖打包 Client -> output\client
:: ------------------------------------------------------------
echo [2/2] 正在发布 WinUI 3 客户端 (Release / x64 独立运行包)...
if exist "%STAGE_CLIENT%" rmdir /s /q "%STAGE_CLIENT%"
mkdir "%STAGE_CLIENT%"

dotnet publish "%CLIENT_CSPROJ%" ^
    -c Release ^
    -p:Platform=x64 ^
    -r win-x64 ^
    --self-contained true ^
    -p:WindowsAppSDKSelfContained=true ^
    -p:PublishTrimmed=false ^
    -p:PublishReadyToRun=false ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -o "%STAGE_CLIENT%" ^
    -v minimal

if errorlevel 1 (
    echo.
    echo [错误] 客户端发布失败，请检查上方编译日志。
    if exist "%STAGE_CLIENT%" rmdir /s /q "%STAGE_CLIENT%"
    pause
    exit /b 1
)

:: 清理旧的 output\client 并用新发布的独立目录完整覆盖
if exist "%OUT_CLIENT%" (
    rmdir /s /q "%OUT_CLIENT%"
    if exist "%OUT_CLIENT%" (
        echo [错误] 无法清理旧目录 %OUT_CLIENT%，请确认没有正在运行的 Client.exe 占用文件。
        if exist "%STAGE_CLIENT%" rmdir /s /q "%STAGE_CLIENT%"
        pause
        exit /b 1
    )
)
move /Y "%STAGE_CLIENT%" "%OUT_CLIENT%" >nul
if errorlevel 1 (
    echo [错误] 移动客户端发布目录失败。
    pause
    exit /b 1
)

:: 清理非必要的调试符号文件（如第三方库附带的 .pdb）
del /s /q "%OUT_CLIENT%\*.pdb" >nul 2>&1

:: 5. 结果校验
if not exist "%OUT_CLIENT%\Client.exe" (
    echo [错误] 未找到发布产物: %OUT_CLIENT%\Client.exe
    pause
    exit /b 1
)
if not exist "%OUT_SERVER%\weixinorc-server.exe" (
    echo [错误] 未找到发布产物: %OUT_SERVER%\weixinorc-server.exe
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   打包发布成功！
echo   客户端: %OUT_CLIENT%\Client.exe
echo   服务端: %OUT_SERVER%\weixinorc-server.exe
echo           (可双击 %OUT_SERVER%\启动服务端.bat 启动)
echo   注: 重复运行本脚本会自动覆盖程序文件，并保留 output\server\data 数据。
echo ============================================================
echo.
if /i not "%~1"=="--no-pause" pause
exit /b 0
