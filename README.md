# weixinORC — 微信客户管理与智能识别工作台

一套面向企业员工的 Windows 桌面微信客户管理与 OCR 辅助系统，采用 **WinUI 3（.NET 10）桌面客户端 + Go（Gin + SQLite）后端** 架构，内置 **RapidOCR PP-OCRv5 离线中文识别引擎**，支持按员工账号严格隔离客户与群组数据。

---

## 一、环境要求

- **操作系统**：Windows 10 (1809+) / Windows 11（x64）
- **后端**：Go `1.24+`（纯 Go SQLite 驱动，无需 CGO / GCC）
- **客户端**：.NET `10.0` SDK + Windows App Runtime（项目已配置非打包原生桌面运行模式）

---

## 二、快速启动

### 方式 A：一键脚本启动（推荐）
- **同时启动 Go 后端 + 客户端**：双击项目根目录的 `启动联调环境.bat`（自动检测端口是否已启动、等待后端就绪并拉起客户端；加 `--rebuild` 参数可强制重新编译客户端）
- **仅启动 Go 后端**：双击 `启动后端.bat`
- **仅启动客户端**：双击 `启动客户端.bat`（加 `--rebuild` 可强制重新编译）

默认服务地址：`http://127.0.0.1:28765`  
默认本地测试账号：`demo` / `demo123`

---

### 方式 B：手动启动 Go 后端

> **注意**：当前项目尚未接入正式外部认证源前，本地联调必须开启 `MOCK_AUTH=1` 并设置模拟账号密码，否则登录会提示「服务端尚未配置认证源」。

#### 1. Windows CMD
```cmd
cd Server
set GOOS=windows
set GOARCH=amd64
set MOCK_AUTH=1
set MOCK_ACCOUNT=demo
set MOCK_PASSWORD=demo123
go run .
```

#### 2. Windows PowerShell
```powershell
cd Server
$env:GOOS="windows"
$env:GOARCH="amd64"
$env:MOCK_AUTH="1"
$env:MOCK_ACCOUNT="demo"
$env:MOCK_PASSWORD="demo123"
go run .
```

#### 3. GoLand / VS Code 运行配置
在 Run Configuration 的 **Environment Variables（环境变量）** 中添加：
```text
MOCK_AUTH=1;MOCK_ACCOUNT=demo;MOCK_PASSWORD=demo123
```

#### 常用后端环境变量
| 变量名 | 默认值 | 说明 |
| --- | --- | --- |
| `SERVER_ADDR` | `127.0.0.1:28765` | 监听地址与端口（`MOCK_AUTH=1` 时仅允许绑定本机回环地址） |
| `DATABASE_PATH` | `data/weixinorc.db` | SQLite 数据库文件路径 |
| `SESSION_TTL_HOURS` | `168`（7 天） | 登录令牌有效期（小时，范围 `1~720`） |
| `MOCK_AUTH` | 关闭 | 设为 `1` 或 `true` 启用本地单账号模拟登录 |
| `MOCK_ACCOUNT` | 空 | 模拟登录账号（`MOCK_AUTH=1` 时必填） |
| `MOCK_PASSWORD` | 空 | 模拟登录密码（`MOCK_AUTH=1` 时必填） |

启动成功后终端会输出：
```text
server listening on 127.0.0.1:28765
```
可访问 `http://127.0.0.1:28765/healthz` 验证返回 `{"status":"ok"}`。

---

### 方式 C：手动编译与启动 WinUI 3 客户端

```powershell
# 编译客户端（自动部署 RapidOCR PP-OCRv5 离线模型）
dotnet build Client\Client.csproj -p:Platform=x64 -v minimal

# 直接启动可执行文件
.\Client\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Client.exe
```

也可以直接用 Visual Studio 打开 `Client\Client.slnx`，选择 `x64` 平台运行调试。

---

## 三、基本使用指南

### 1. 员工登录
- 启动客户端后先进入独立登录页，右上角绿点表示后端服务在线。
- 输入员工账号密码（本地联调可点击 **「填入本地测试账号」** 自动填入 `demo / demo123`），点击 **「立即登录」** 进入工作台。
- 如后端部署在其它端口或局域网地址，可展开「服务地址配置」修改并测试连通性。

### 2. 客户档案 & 群组档案（仿微信 IM 工作台）
- **左栏（列表）**：支持按备注、昵称、微信号、电话、群名快速搜索。
  - 点击左上角紫色 **`+`** 按钮弹出 **「截图识别导入」** 窗口。
  - 在微信中截取客户资料卡或群信息图复制到剪贴板，在工作台或弹窗内直接按 **`Ctrl+V`**（或点击上传图片），自动通过离线 RapidOCR 识别并预填昵称、微信号、地区、电话、个性签名、来源、添加时间等字段，人工核对后一键入库。
- **中栏（会话记录）**：
  - 支持展示和记录双方对话（左侧白色气泡为对方，右侧绿色气泡为我方）。
  - 聊天区与输入框之间支持**鼠标上下拖拽分隔条**自由调整输入框高度。
  - 点击输入区右上角 **「截图导入对话」**，粘贴整张微信聊天截图即可自动按绿色/白色气泡与位置区分我方/对方，过滤系统提示后核对并一键批量导入。
- **右栏（资料卡与员工备注）**：
  - 展示客户/群组完整资料及底部 **「员工跟进备注」** 专属卡片。
  - 点击右上角 **「编辑」** 可修改任意字段及员工备注，点击 **「保存」** 同步至 Go 后端；点击 **「删除」** 经二次确认后可移除档案及关联记录。

### 3. 微信窗口监控与 OCR
- 左侧导航切换到 **「微信OCR」**，可检测当前运行的微信（`WeChat` / `Weixin`）与企业微信（`WXWork`）窗口。
- 支持窗口显示/隐藏暂停控制、直接截取窗口画面并叠加显示 RapidOCR 文本框坐标，以及人工核验后直接入库。

---

## 四、项目目录结构

```text
weixinORC/
├── Client/                      # WinUI 3 (.NET 10) 桌面客户端
│   ├── Assets/OcrModels/        # RapidOCR PP-OCRv5 离线中文识别模型与字典
│   ├── Controls/                # 自定义控件（可拖拽分隔条等）
│   ├── Dialogs/                 # 客户/群组/聊天截图 OCR 导入弹窗
│   ├── Models/                  # 前后端 DTO 与界面模型
│   ├── Pages/                   # 登录页、工作台壳、客户/群组 IM、微信监控页
│   └── Services/                # Go API 客户端、Win32 窗口控制、RapidOCR 与解析器
├── Server/                      # Go (Gin + SQLite) 后端服务
│   ├── main.go                  # 配置加载与 HTTP 服务入口
│   └── internal/
│       ├── auth/                # 员工认证接口、Mock 实现与 Bearer 会话中间件
│       ├── store/               # SQLite 建表迁移、客户/群组/消息存储
│       └── api/                 # REST API 路由与处理器
├── doc/                         # 项目实施计划与需求核对文档
├── 启动后端.bat                  # 仅启动 Go 后端 (127.0.0.1:28765, demo/demo123)
├── 启动客户端.bat                # 仅启动 WinUI 3 客户端（支持 --rebuild）
└── 启动联调环境.bat              # 一键启动 Go 后端 + 等待就绪 + 启动客户端
```

---

## 五、常见问题

1. **登录提示「服务端尚未配置认证源」**  
   说明 Go 后端启动时没有设置 `MOCK_AUTH=1`、`MOCK_ACCOUNT`、`MOCK_PASSWORD`（参见第二节手动启动命令或直接双击 `启动联调环境.bat`）。
2. **`go run .` 提示 `executable file not found in %PATH%`**  
   本机 Go 全局环境可能设为了 `GOOS=linux`，在当前终端执行 `$env:GOOS="windows"; $env:GOARCH="amd64"`（或 `go env -w GOOS=windows GOARCH=amd64`）后再运行即可。
3. **修改并重启 Go 后端后客户端操作提示未登录**  
   当前本地会话令牌存放在内存中，重启后端后在客户端点右上角「退出登录」重新登录一次即可。
