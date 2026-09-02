"""Unicode / AI-prompt round-trip tests: Chinese, emoji, Markdown, JSON, code fences,
CRLF/LF, tabs, quotes, backslashes — content must survive exactly."""
from __future__ import annotations

import json

from .conftest import auth_headers, make_clip, sha256_hex

REQUIRED_UNICODE_BLOCK = (
    "你好，这是一个跨设备剪贴板测试。\n"
    "第二行。\n"
    "第三行包含英文：Codex / DSH / DeepSeek.\n"
    "符号：`~!@#$%^&*()_+-={}[]`\n"
    "Emoji: 🐱🚀\n"
)

AI_PROMPT_BLOCK = '''你好，请帮我审查下面这段代码。

## 需求
1. 支持超长文本同步
2. 不依赖微信 \r\n
3. "双引号" 和 '单引号' 都要保留
4. 反斜杠：C:\\Users\\devuser\\Documents\\ClipBridge
5. Tab 分隔：列A\t列B\t列C

```python
def hello(name: str) -> str:
    return f"Hello, {name}!"  # 👋
```

末尾符号：{}[]()<>~!@#$%^&*()-=+_|\\/"'`;:,.?😀
'''

JSON_SAMPLE = json.dumps(
    {
        "prompt": "总结这段日志",
        "lines": ["第一行", "line two with \\ backslash", "emoji: 🤖"],
        "nested": {"k": "v", "list": [1, 2, 3]},
    },
    ensure_ascii=False,
)


def _assert_roundtrip(client, content: str, label: str):
    body = make_clip(content=content, device_id="iphone")
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 201, f"{label}: {r.text[:300]}"
    data = r.json()
    assert data["content_hash"] == sha256_hex(content), label
    assert data["size_bytes"] == len(content.encode("utf-8")), label
    got = client.get(f"/api/v1/clips/{data['clip_id']}", headers=auth_headers()).json()
    assert got["content"] == content, f"{label}: round-trip mismatch"


def test_required_unicode_block(client):
    _assert_roundtrip(client, REQUIRED_UNICODE_BLOCK, "required-unicode")


def test_ai_prompt_block_with_crlf_tabs_markdown(client):
    _assert_roundtrip(client, AI_PROMPT_BLOCK, "ai-prompt")


def test_json_sample(client):
    _assert_roundtrip(client, JSON_SAMPLE, "json")


def test_lf_only(client):
    _assert_roundtrip(client, "line1\nline2\nline3\n", "lf")


def test_crlf_only(client):
    _assert_roundtrip(client, "line1\r\nline2\r\nline3\r\n", "crlf")


def test_trailing_newlines_preserved(client):
    _assert_roundtrip(client, "end\n\n\n\n", "trailing-newlines")


def test_surrogate_pair_emoji_and_zwj(client):
    _assert_roundtrip(client, "family: 👨‍👩‍👧‍👦 flag: 🇨🇳 rocket: 🚀", "emoji")


def test_empty_string_allowed(client):
    _assert_roundtrip(client, "", "empty")


def test_whitespace_only(client):
    _assert_roundtrip(client, " \t\n\r\n ", "whitespace")
