# ClipBridge Relay

Python 3 + FastAPI + SQLite(标准库) 的 clip 中继服务。详见根目录 `docs/`。

## 快速开始

```powershell
python -m venv .venv
.\.venv\Scripts\pip install -r requirements.txt     # 运行依赖
.\.venv\Scripts\pip install -r requirements-dev.txt # 测试依赖（可选）

Copy-Item .env.example .env    # 修改 CLIPBRIDGE_TOKEN
.\.venv\Scripts\python run.py  # http://127.0.0.1:8787, Swagger: /docs
```

没有 token 会拒绝启动。token 生成：`python -c "import secrets; print(secrets.token_hex(32))"`

## LAN 模式（iPhone/iPad Shortcuts）

默认绑定 `127.0.0.1`（仅本机）。让手机访问：

```powershell
$env:CLIPBRIDGE_BIND_HOST = '0.0.0.0'
.\.venv\Scripts\python run.py
```

或一条命令同时启动 relay + 客户端：
`powershell -ExecutionPolicy Bypass -File ..\..\scripts\start-lan-dev.ps1`

> LAN 明文 HTTP 仅限可信私有网络：token 是明文传输，**不要**端口转发/暴露公网，
> 生产/远程必须 HTTPS 或隧道（见 `docs/SECURITY.md`）。启动时会打印醒目警告。

Milestone 2 新增 API：`POST /api/v1/clips/shortcut`（Shortcut 只需
`{"device_id":"iphone","content":"..."}`，relay 生成 clip_id/hash/created_at）与
`GET /api/v1/clips/latest?exclude_device_id=<id>`（排除本设备来源，见 `docs/PROTOCOL.md`）。

## 测试

```powershell
.\.venv\Scripts\python -m pytest
```

## 目录

```text
relay/
  app.py            FastAPI 应用（端点、鉴权、校验、大小限制、exclude 查询）
  models.py         pydantic 请求/响应 schema（含 Shortcut 简化创建）
  storage.py        ClipStore 抽象 + SQLite / 内存实现（含 latest_excluding）
  security.py       SHA-256 + 常量时间 token 比较
  logging_setup.py  日志脱敏日志格式（禁止正文/token）
  config.py         .env + 环境变量配置（CLIPBRIDGE_BIND_HOST）
tests/              pytest：auth / API / 长文本 / Unicode / 存储 / 日志脱敏 / Shortcut API / exclude
```
