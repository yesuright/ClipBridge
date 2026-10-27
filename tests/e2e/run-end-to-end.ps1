# ClipBridge full-stack end-to-end verification (Milestone 2).
# Verifies:
#   A. Windows clipboard -> relay
#   B. relay (simulated iPhone) -> Windows clipboard
#   C. loop prevention (no re-upload of applied remote text)
#   D. long text round-trips (100 KB, 1 MB, ~2 MiB boundary)
#   E. Unicode / emoji / CRLF / Markdown / JSON round-trips
#   F. simulated iPhone/iPad Shortcut flows (Milestone 2):
#      simplified POST /clips/shortcut, GET latest?exclude_device_id,
#      requester exclusion, unicode + 200 KB via shortcut path, loop prevention
#
# Requires: .NET 8 SDK, Python 3 + services/relay/.venv (pip install -r requirements-dev.txt)
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File tests/e2e/run-end-to-end.ps1

param(
    [string]$ProjectDir = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$RelayPort = '8787',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$results = [System.Collections.Generic.List[object]]::new()

function Pass($name, $detail = '') {
    $results.Add([pscustomobject]@{ Name = $name; Status = 'PASS'; Detail = $detail })
    Write-Host "[PASS] $name $detail" -ForegroundColor Green
}
function Fail($name, $detail = '') {
    $results.Add([pscustomobject]@{ Name = $name; Status = 'FAIL'; Detail = $detail })
    Write-Host "[FAIL] $name $detail" -ForegroundColor Red
}
function Assert-True([bool]$condition, $name, $detail = '') {
    if ($condition) { Pass $name $detail } else { Fail $name $detail }
}

function Sha256Hex([string]$text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
}

function Invoke-Relay($method, $path, $body = $null) {
    $headers = @{ Authorization = "Bearer $script:Token" }
    try {
        if ($null -ne $body) {
            return Invoke-RestMethod -Method $method -Uri "http://127.0.0.1:$RelayPort$path" `
                -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ($body | ConvertTo-Json -Depth 5)
        }
        return Invoke-RestMethod -Method $method -Uri "http://127.0.0.1:$RelayPort$path" -Headers $headers
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        if ($status) { return [pscustomobject]@{ __http_status = [int]$status } }
        throw
    }
}

function Wait-Until([scriptblock]$condition, [int]$timeoutSec = 20, [int]$intervalMs = 500) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        try { if (& $condition) { return $true } } catch { }
        Start-Sleep -Milliseconds $intervalMs
    }
    return $false
}

function Write-Utf8NoBom([string]$path, [string]$content) {
    [System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))
}

function New-Filler([int]$bytes) {
    $line = 'The quick brown fox jumps over the lazy dog. 0123456789`n'
    $sb = [System.Text.StringBuilder]::new([int]($bytes * 1.2))
    while ($sb.Length -lt $bytes) { [void]$sb.Append($line) }
    return $sb.ToString().Substring(0, $bytes)
}

function MakeClipBody([string]$content, [string]$deviceId, [string]$createdAt = '2025-06-01T12:00:00.000Z') {
    # clip_id with fresh GUID (client-generated per protocol)
    $hash = Sha256Hex $content
    return [ordered]@{
        clip_id      = [guid]::NewGuid().ToString()
        device_id    = $deviceId
        content_type = 'text/plain'
        content      = $content
        content_hash = $hash
        created_at   = $createdAt
        origin       = 'shortcut'
    }
}

# ------------------------------------------------------------------ preflight
Write-Host "=== ClipBridge Milestone 2 E2E ===" -ForegroundColor Cyan
if (-not (Test-Path $ProjectDir)) { Write-Host "Project dir missing: $ProjectDir"; exit 1 }

# Prefer the user-installed SDK (e.g. %USERPROFILE%\.dotnet\dotnet.exe), then PATH.
$dotnet = $null
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" }
if (-not $dotnet) { $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (-not $dotnet) { Fail 'preflight: dotnet SDK' 'not found'; Write-Host 'Install the .NET 8 SDK'; exit 1 }

$relayDir = Join-Path $ProjectDir 'services\relay'
$venvPython = Join-Path $relayDir '.venv\Scripts\python.exe'
if (-not (Test-Path $venvPython)) {
    Fail 'preflight: relay venv' "missing: $venvPython (run: pip install -r requirements-dev.txt)"
    exit 1
}

$script:Token = 'e2e-' + [guid]::NewGuid().ToString('N')
$script:E2eDb = 'data/e2e-clipbridge-' + [guid]::NewGuid().ToString('N') + '.db'
$relayEnvPath = Join-Path $relayDir '.env'
$relayEnvBackup = $null
if (Test-Path $relayEnvPath) { $relayEnvBackup = Get-Content $relayEnvPath -Raw }
@"
CLIPBRIDGE_HOST=127.0.0.1
CLIPBRIDGE_PORT=$RelayPort
CLIPBRIDGE_TOKEN=$script:Token
CLIPBRIDGE_DB_PATH=$script:E2eDb
CLIPBRIDGE_LOG_LEVEL=WARNING
CLIPBRIDGE_DEDUP_WINDOW_SECONDS=15
"@ | Set-Content -Path $relayEnvPath -Encoding UTF8

$clientConfigDir = Join-Path $env:APPDATA 'ClipBridge'
$clientConfigPath = Join-Path $clientConfigDir 'config.json'
$clientConfigBackup = $null
if (Test-Path $clientConfigPath) { $clientConfigBackup = Get-Content $clientConfigPath -Raw }
New-Item -ItemType Directory -Force -Path $clientConfigDir | Out-Null
@{ RelayUrl = "http://127.0.0.1:$RelayPort"; DeviceId = 'windows-main';
   DeviceToken = $script:Token; PollIntervalMs = 1000 } |
    ConvertTo-Json | ForEach-Object { Write-Utf8NoBom $clientConfigPath $_ }

$relayProcess = $null
$clientProcess = $null
try {
    # ------------------------------------------------------------------ relay
    $relayProcess = Start-Process -FilePath $venvPython -ArgumentList 'run.py' `
        -WorkingDirectory $relayDir -PassThru -WindowStyle Hidden
    $health = Wait-Until { (Invoke-RestMethod "http://127.0.0.1:$RelayPort/api/v1/health" -TimeoutSec 2).status -eq 'ok' } 25 500
    Assert-True ($health) 'relay starts and health check passes'
    $unauth401 = $false
    try {
        Invoke-RestMethod -Method GET -Uri "http://127.0.0.1:$RelayPort/api/v1/clips/latest" -TimeoutSec 5 | Out-Null
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -eq 401) { $unauth401 = $true }
    }
    Assert-True ($unauth401) 'relay: unauthenticated request rejected'

    # ------------------------------------------------------------ windows client
    if (-not $SkipBuild) {
        & $dotnet build (Join-Path $ProjectDir 'apps\windows\ClipBridge.sln') -c Debug --nologo -v q | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail 'windows client build'; exit 1 }
    }
    $clientExe = Join-Path $ProjectDir 'apps\windows\src\ClipBridge.Win\bin\Debug\net8.0-windows\ClipBridge.exe'
    if (-not (Test-Path $clientExe)) { Fail 'preflight: client exe' "missing $clientExe"; exit 1 }
    $clientProcess = Start-Process -FilePath $clientExe -PassThru
    Start-Sleep -Seconds 4

    # ==================================================== A. Windows -> Relay
    $textA = 'Hello from ClipBridge'
    Set-Clipboard -Value $textA
    $okA = Wait-Until {
        $latest = Invoke-Relay 'GET' '/api/v1/clips/latest'
        $latest.content -eq $textA
    } 20 500
    Assert-True ($okA) 'A: Windows -> Relay (copy uploads)'
    $latestA = Invoke-Relay 'GET' '/api/v1/clips/latest'
    Assert-True ($latestA.device_id -eq 'windows-main') 'A: source device recorded'
    Assert-True ($latestA.content_hash -eq (Sha256Hex $textA)) 'A: content hash matches'

    # =================================================== B. Relay -> Windows
    $textB = 'Hello from iPhone'
    $bodyB = MakeClipBody $textB 'iphone' '2025-06-01T13:00:00.000Z'
    $respB = Invoke-Relay 'POST' '/api/v1/clips' $bodyB
    Assert-True ($respB.clip_id -eq $bodyB.clip_id) 'B: remote POST accepted'
    $okB = Wait-Until { (Get-Clipboard) -eq $textB } 20 500
    Assert-True ($okB) 'B: Relay -> Windows (clipboard updated)'

    # ==================================================== C. Loop prevention
    Start-Sleep -Seconds 5   # several poll cycles
    $history = (Invoke-Relay 'GET' '/api/v1/clips?limit=50')
    $echoes = @($history.clips | Where-Object { $_.device_id -eq 'windows-main' -and $_.content -eq $textB })
    Assert-True ($echoes.Count -eq 0) 'C: loop prevention (no re-upload of remote text)'
    $stillOk = Wait-Until { (Get-Clipboard) -eq $textB } 5 500
    Assert-True ($stillOk) 'C: clipboard content stable after echo suppression'

    # ============================================================ D. Long text
    foreach ($size in @(100KB, 1MB)) {
        $content = New-Filler $size
        Set-Clipboard -Value $content
        $okUp = Wait-Until {
            $latest = Invoke-Relay 'GET' '/api/v1/clips/latest'
            $latest.content_hash -eq (Sha256Hex $content)
        } 30 1000
        Assert-True ($okUp) "D: $([int]$size/1024) KB Windows -> Relay round-trip"

        $body = MakeClipBody $content 'iphone'
        $null = Invoke-Relay 'POST' '/api/v1/clips' $body
        $okDown = Wait-Until {
            $cb = Get-Clipboard
            $cb -and $cb.Length -eq $size -and (Sha256Hex $cb) -eq (Sha256Hex $content)
        } 30 1000
        Assert-True ($okDown) "D: $([int]$size/1024) KB Relay -> Windows round-trip"
    }

    # ~2 MiB boundary: 2MiB-1 succeeds through the full stack; 2MiB+1 is rejected 413
    $nearMax = New-Filler (2MB - 1)
    Set-Clipboard -Value $nearMax
    $okNear = Wait-Until {
        $latest = Invoke-Relay 'GET' '/api/v1/clips/latest'
        $latest.content_hash -eq (Sha256Hex $nearMax)
    } 40 1000
    Assert-True ($okNear) 'D: ~2 MiB Windows -> Relay round-trip (2MiB-1)'

    $overSize = New-Filler (2MB + 1)
    $respOver = Invoke-Relay 'POST' '/api/v1/clips' (MakeClipBody $overSize 'iphone')
    Assert-True ($respOver.__http_status -eq 413) 'D: >2 MiB rejected with 413 at relay'
    $clientAlive = -not $clientProcess.HasExited
    Assert-True ($clientAlive) 'D: client survives 2 MiB boundary traffic'

    # ================================================================= E. Unicode
    $unicode = "你好，这是一个跨设备剪贴板测试。`n第二行。`n第三行包含英文：Codex / DSH / DeepSeek.`n符号：``~!@#`$%^&*()_+-={}[]``nEmoji: 🐱🚀`n"
    Set-Clipboard -Value $unicode
    $okU = Wait-Until {
        $latest = Invoke-Relay 'GET' '/api/v1/clips/latest'
        $latest.content_hash -eq (Sha256Hex $unicode)
    } 20 500
    Assert-True ($okU) 'E: Chinese/emoji/symbols Windows -> Relay round-trip'

    $bodyU = MakeClipBody $unicode 'ipad'
    $null = Invoke-Relay 'POST' '/api/v1/clips' $bodyU
    $okDownU = Wait-Until { (Get-Clipboard) -eq $unicode } 20 500
    Assert-True ($okDownU) 'E: Chinese/emoji/symbols Relay -> Windows round-trip'

    $mixed = "CRLF`r`n`nLF`n`tTab`n" + '```' + "json`n{`"k`":`"v`"}``n" + '```' + "`nbackslash \\ and ""quote"""
    Set-Clipboard -Value $mixed
    $okM = Wait-Until {
        $latest = Invoke-Relay 'GET' '/api/v1/clips/latest'
        $latest.content -eq $mixed
    } 20 500
    Assert-True ($okM) 'E: CRLF/LF/tabs/Markdown/JSON/quotes/backslashes round-trip'

    # ======================================= F. Simulated iPhone/iPad Shortcut flows (M2)
    # The real iPhone can't be driven from the PC, so we simulate exactly what the
    # Apple Shortcuts do over HTTP:
    #   Send  = POST /api/v1/clips/shortcut {"device_id","content"}
    #   Get   = GET  /api/v1/clips/latest?exclude_device_id=<self>
    #         or GET  /api/v1/clips?limit=10&exclude_device_id=<self>

    function Send-Shortcut($deviceId, $content) {
        $headers = @{ Authorization = "Bearer $script:Token" }
        return Invoke-RestMethod -Method POST -Uri "http://127.0.0.1:$RelayPort/api/v1/clips/shortcut" `
            -Headers $headers -ContentType 'application/json; charset=utf-8' `
            -Body (@{ device_id = $deviceId; content = $content } | ConvertTo-Json -Depth 5)
    }

    # F1: iPhone "Send to ClipBridge" (simplified endpoint) -> Windows clipboard
    $f1Text = 'F1: Hello from simulated iPhone (shortcut send)'
    $f1 = Send-Shortcut 'iphone' $f1Text
    Assert-True ($f1.clip_id -and $f1.origin -eq 'shortcut') 'F1: shortcut send accepted (server-generated record)'
    Assert-True ($f1.content_hash -eq (Sha256Hex $f1Text)) 'F1: server computed canonical hash'
    $okF1 = Wait-Until { (Get-Clipboard) -eq $f1Text } 20 500
    Assert-True ($okF1) 'F1: iphone shortcut send -> Windows clipboard'

    # F2: requester exclusion — iphone-equivalent GET must NOT return its own record
    $f2 = Invoke-Relay 'GET' '/api/v1/clips/latest?exclude_device_id=iphone'
    Assert-True ($f2.device_id -ne 'iphone') "F2: latest?exclude_device_id=iphone excludes own records (got $($f2.device_id))"
    $f2list = Invoke-Relay 'GET' '/api/v1/clips?limit=10&exclude_device_id=iphone'
    Assert-True (($f2list.clips | Where-Object { $_.device_id -eq 'iphone' }).Count -eq 0) 'F2: recent?exclude_device_id=iphone excludes own records'

    # F3: Windows copy -> iphone-equivalent GET sees it as newest "other device" clip
    $f3Text = 'F3: Windows -> relay, expected by iPhone GET'
    Set-Clipboard -Value $f3Text
    $okF3up = Wait-Until {
        $latest = Invoke-Relay 'GET' '/api/v1/clips/latest'
        $latest.content_hash -eq (Sha256Hex $f3Text)
    } 20 500
    Assert-True ($okF3up) 'F3: Windows copy uploaded'
    $f3get = Invoke-Relay 'GET' '/api/v1/clips/latest?exclude_device_id=iphone'
    Assert-True ($f3get.device_id -eq 'windows-main' -and $f3get.content -eq $f3Text) 'F3: iphone-equivalent GET gets Windows copy (exclusion working)'

    # F4: iPad send -> Windows, then iPad-equivalent GET skips iPad's own record
    $f4Text = "F4: iPad says 你好 🐱`nsecond line"
    $f4 = Send-Shortcut 'ipad' $f4Text
    Assert-True ($f4.size_bytes -eq [System.Text.Encoding]::UTF8.GetByteCount($f4Text)) 'F4: ipad shortcut send accepted (unicode size)'
    $okF4 = Wait-Until { (Get-Clipboard) -eq $f4Text } 20 500
    Assert-True ($okF4) 'F4: ipad shortcut send -> Windows clipboard (unicode)'
    $f4get = Invoke-Relay 'GET' '/api/v1/clips/latest?exclude_device_id=ipad'
    Assert-True ($f4get.device_id -ne 'ipad') "F4: latest?exclude_device_id=ipad excludes own records (got $($f4get.device_id))"
    $f4list = Invoke-Relay 'GET' '/api/v1/clips?limit=10&exclude_device_id=ipad'
    Assert-True (($f4list.clips | Where-Object { $_.device_id -eq 'ipad' }).Count -eq 0) 'F4: recent?exclude_device_id=ipad excludes own records'

    # F5: long text through the simplified shortcut endpoint (200 KB)
    $f5Text = New-Filler (200KB)
    $f5 = Send-Shortcut 'iphone' $f5Text
    Assert-True ($f5.content_hash -eq (Sha256Hex $f5Text)) 'F5: 200 KB shortcut send hash round-trip'
    $okF5 = Wait-Until {
        $cb = Get-Clipboard
        $cb -and $cb.Length -eq (200KB) -and (Sha256Hex $cb) -eq (Sha256Hex $f5Text)
    } 30 1000
    Assert-True ($okF5) 'F5: 200 KB shortcut send -> Windows clipboard'

    # F6: no echo — after shortcut sends, Windows must NOT re-upload remote text
    Start-Sleep -Seconds 5   # several poll cycles
    $f6History = (Invoke-Relay 'GET' '/api/v1/clips?limit=50')
    $f6echoes = @($f6History.clips | Where-Object { $_.device_id -eq 'windows-main' -and $_.content -eq $f1Text })
    Assert-True ($f6echoes.Count -eq 0) 'F6: loop prevention holds after shortcut sends'

    # F7: mark a "no other device content" case is a clean 404 for the requester
    $f7 = Invoke-Relay 'GET' '/api/v1/clips/latest?exclude_device_id=iphone'
    Assert-True ($f7.device_id -ne 'iphone') 'F7: simulated devices still get a non-self record'
}
finally {
    if ($clientProcess -and -not $clientProcess.HasExited) {
        try { Stop-Process -Id $clientProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    if ($relayProcess -and -not $relayProcess.HasExited) {
        try { Stop-Process -Id $relayProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    if ($null -ne $relayEnvBackup) { Write-Utf8NoBom $relayEnvPath ($relayEnvBackup -join "`r`n") }
    elseif (Test-Path $relayEnvPath) { Remove-Item $relayEnvPath -Force }
    if ($null -ne $clientConfigBackup) { Write-Utf8NoBom $clientConfigPath ($clientConfigBackup -join "`r`n") }
    elseif (Test-Path $clientConfigPath) { Remove-Item $clientConfigPath -Force }
    foreach ($f in @($script:E2eDb, "$script:E2eDb-wal", "$script:E2eDb-shm")) {
        if ($f -and (Test-Path (Join-Path $relayDir $f))) { Remove-Item (Join-Path $relayDir $f) -Force -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------- report
Write-Host ''
Write-Host '=== E2E RESULTS ===' -ForegroundColor Cyan
$failed = 0
foreach ($r in $results) {
    if ($r.Status -eq 'FAIL') { $failed++ }
    Write-Host ("{0,-4} {1}" -f $r.Status, $r.Name)
}
Write-Host ''
if ($failed -eq 0) { Write-Host 'ALL E2E CHECKS PASSED' -ForegroundColor Green; exit 0 }
Write-Host "$failed E2E CHECK(S) FAILED" -ForegroundColor Red; exit 1
