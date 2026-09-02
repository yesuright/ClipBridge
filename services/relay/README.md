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

## 测试

```powershell
.\.venv\Scripts\python -m pytest
```

## 目录

```text
relay/
  app.py            FastAPI 应用（端点、鉴权、校验、大小限制）
  models.py         pydantic 请求/响应 schema
  storage.py        ClipStore 抽象 + SQLite / 内存实现
  security.py       SHA-256 + 常量时间 token 比较
  logging_setup.py  日志脱敏日志格式（禁止正文/token）
  config.py         .env + 环境变量配置
tests/              pytest：auth / API / 长文本 / Unicode / 存储 / 日志脱敏
```
