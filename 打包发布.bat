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
set "OUT_ZIP=%OUTPUT_DIR%\智美工作台-发布包.zip"
set "OUT_ZIP_TMP=%OUTPUT_DIR%\_release_pack.zip.tmp"
set "ZIP_ROOT=智美工作台"

echo ============================================================
echo   智美工作台 - 一键打包发布脚本
echo   输出目录: %OUTPUT_DIR%
echo     - 客户端: %OUT_CLIENT%
echo     - 服务端: %OUT_SERVER%
echo     - 压缩包: %OUT_ZIP%
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
echo [1/3] 正在编译 Go 服务端 (windows/amd64)...
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
echo [2/3] 正在发布 WinUI 3 客户端 (Release / x64 独立运行包)...
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

:: 复制 App-Local VC++ x64 运行库（供 onnxruntime / SilkCodec 在纯净 Windows 客户机免安装运行）
powershell -NoProfile -Command "$crt = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio','C:\Program Files (x86)\Microsoft Visual Studio' -Recurse -Directory -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\' -and $_.FullName -notmatch 'onecore|spectre' } | Sort-Object FullName -Descending | Select-Object -First 1; if ($crt) { foreach ($f in @('vcruntime140.dll','vcruntime140_1.dll','vcruntime140_threads.dll','msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','msvcp140_atomic_wait.dll','msvcp140_codecvt_ids.dll')) { $src = Join-Path $crt.FullName $f; if (Test-Path $src) { Copy-Item $src $env:OUT_CLIENT -Force } }; Write-Host ('[完成] 已附带 App-Local VC++ 运行库: ' + $crt.FullName) } else { Write-Host '[提示] 未检测到本机 VS VC++ Redist 目录，目标机若缺运行库需安装 VC++ 2015-2022 Redistributable (x64)。' }"

:: 5. 结果校验
if not exist "%OUT_CLIENT%\Client.exe" (
    echo [错误] 未找到发布产物: %OUT_CLIENT%\Client.exe
    if /i not "%~1"=="--no-pause" pause
    exit /b 1
)
if not exist "%OUT_CLIENT%\Client.pri" if not exist "%OUT_CLIENT%\resources.pri" (
    echo [错误] 缺少 WinUI 资源索引文件 ^(Client.pri / resources.pri^)，客户端启动将因无法加载 XAML 而闪退！
    echo        请确认 Client.csproj 已启用 EnableMsixTooling=true。
    if /i not "%~1"=="--no-pause" pause
    exit /b 1
)
if not exist "%OUT_CLIENT%\Assets\OcrModels\ch_PP-OCRv5_rec_mobile.onnx" (
    echo [错误] 缺少本地 OCR 模型文件: %OUT_CLIENT%\Assets\OcrModels\ch_PP-OCRv5_rec_mobile.onnx
    if /i not "%~1"=="--no-pause" pause
    exit /b 1
)
if not exist "%OUT_SERVER%\weixinorc-server.exe" (
    echo [错误] 未找到发布产物: %OUT_SERVER%\weixinorc-server.exe
    if /i not "%~1"=="--no-pause" pause
    exit /b 1
)

:: ------------------------------------------------------------
:: 6. 压缩为单个便携 ZIP 发布包 -> output\智美工作台-发布包.zip
::    (自动跳过本机 output\server\data 中的数据库与缓存，保持干净空目录)
:: ------------------------------------------------------------
echo.
echo [3/3] 正在生成便携 ZIP 压缩包...
powershell -NoProfile -Command "$ErrorActionPreference = 'Stop'; Add-Type -AssemblyName System.IO.Compression; Add-Type -AssemblyName System.IO.Compression.FileSystem; $clientDir = (Resolve-Path $env:OUT_CLIENT).Path; $serverDir = (Resolve-Path $env:OUT_SERVER).Path; $tmpZip = $env:OUT_ZIP_TMP; $finalZip = $env:OUT_ZIP; $rootName = $env:ZIP_ROOT; if (Test-Path $tmpZip) { Remove-Item $tmpZip -Force }; $fs = [System.IO.File]::Open($tmpZip, [System.IO.FileMode]::Create); try { $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create, $false, [System.Text.Encoding]::UTF8); try { foreach ($f in Get-ChildItem $clientDir -Recurse -File) { $rel = $f.FullName.Substring($clientDir.Length + 1).Replace('\', '/'); [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, ($rootName + '/client/' + $rel), [System.IO.Compression.CompressionLevel]::Optimal) }; foreach ($f in Get-ChildItem $serverDir -Recurse -File) { $rel = $f.FullName.Substring($serverDir.Length + 1).Replace('\', '/'); if ($rel -like 'data/*') { continue }; [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, ($rootName + '/server/' + $rel), [System.IO.Compression.CompressionLevel]::Optimal) }; [void]$zip.CreateEntry($rootName + '/server/data/') } finally { $zip.Dispose() } } finally { $fs.Dispose() }; Move-Item $tmpZip $finalZip -Force; $mb = (Get-Item $finalZip).Length / 1MB; Write-Host ('[OK] ZIP generated: {0} ({1:N1} MB)' -f $finalZip, $mb)"
if errorlevel 1 (
    echo [错误] ZIP 压缩包生成失败，请检查磁盘空间或是否被其他程序占用: %OUT_ZIP%
    if exist "%OUT_ZIP_TMP%" del /q "%OUT_ZIP_TMP%" >nul 2>&1
    if /i not "%~1"=="--no-pause" pause
    exit /b 1
)

echo.
echo ============================================================
echo   打包发布成功！
echo   客户端: %OUT_CLIENT%\Client.exe
echo   服务端: %OUT_SERVER%\weixinorc-server.exe
echo           (可双击 %OUT_SERVER%\启动服务端.bat 启动)
echo   压缩包: %OUT_ZIP%
echo           (可直接复制该 ZIP 到客户电脑解压即用，不含本机测试数据)
echo   注: 重复运行本脚本会自动覆盖程序文件与压缩包，并保留 output\server\data 数据。
echo ============================================================
echo.
if /i not "%~1"=="--no-pause" pause
exit /b 0
