# Contributing to ClipBridge

Thanks for your interest in improving ClipBridge. This document is the minimum you need
to get a development environment running and a change accepted.

## Ground rules

- **Windows-only client, Python relay.** The client is C#/.NET 10 (WinForms) and only runs
  on Windows; the relay is Python and cross-platform in principle, but the tests and
  scripts assume Windows PowerShell 5.1.
- **Security invariants are non-negotiable:**
  - never log clipboard content, tokens or personal paths (both components enforce and
    test this);
  - never put the token on a command line or in a URL;
  - never add an outbound service, telemetry or a cloud dependency — the relay is the
    only network peer and it runs locally;
  - never weaken authentication in tests (wrong tokens must stay 401).
- **No surprise scope.** Bug fixes, portability work, docs and tests are welcome. New
  user-facing features (image support, E2E encryption, installers…) should start as an
  issue first.
- The proxy handling in the client is a **known, deliberate open decision** — see
  [README Known issues](README.md#known-issues) and `docs/PORTABILITY.md §3.6`. Do not
  "fix" it in passing; open an issue to discuss it.

## Setup

Prerequisites: .NET 10 SDK, Python 3.12+ (3.13 tested), Windows.

```powershell
# Relay — from the repo root
cd services\relay
python -m venv .venv
.\.venv\Scripts\pip install -r requirements.txt -r requirements-dev.txt
Copy-Item .env.example .env          # set CLIPBRIDGE_TOKEN
.\.venv\Scripts\python run.py
cd ..\..                             # back to the repo root

# Client — from the repo root
dotnet run --project apps\windows\src\ClipBridge.Win
```

Full environment-variable reference and dev scripts: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Before you open a pull request

Run the relevant layers of the test pyramid and paste the real results (counts, and the
failure output if any) into the PR description:

```powershell
cd services\relay
.\.venv\Scripts\python -m pytest
cd ..\..

# from the repo root:
dotnet test apps\windows\tests\ClipBridge.Core.Tests
powershell -ExecutionPolicy Bypass -File scripts\secret-scan.ps1
```

If your change affects the packaged product or the scripts, also build and verify an
isolated artifact (never overwrite a running installation):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-daily.ps1 -OutputDir artifacts\pr-check
powershell -ExecutionPolicy Bypass -File scripts\verify-daily.ps1 -Artifact artifacts\pr-check
powershell -ExecutionPolicy Bypass -File scripts\verify-portability.ps1 -Artifact artifacts\pr-check
```

## Conventions

- **Commits**: short, imperative, conventional-commit style prefixes are used in this
  repo (`feat:`, `fix:`, `test:`, `docs:`, `chore:`). One logical change per commit.
- **Tests**: bug fixes should come with a regression test; new behaviour needs coverage in
  the layer that owns it (relay → pytest, core logic → xunit, packaged flows → verify
  scripts).
- **Docs**: user-visible behaviour changes update the relevant doc page in the same PR.
  English lives in `README.md`; the Chinese mirror is `README.zh-CN.md` — keep both in
  sync when they overlap.
- **Privacy**: do not include real tokens, personal paths, device ids or screenshots of
  real clipboard content in issues, PRs, docs or test fixtures.

## Reporting bugs

Please include: Windows version, how you launch ClipBridge, `ClipBridge.exe --doctor`
output (it redacts the token), and the relevant lines from
`%LOCALAPPDATA%\ClipBridge\logs\` (they never contain clipboard content or tokens).
For iPhone-side problems, also state the iOS device model and the shortcut error screen.
