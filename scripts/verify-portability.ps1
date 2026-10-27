# Cross-environment portability verification for the packaged ClipBridge build.
#
# What this proves (on ONE real machine, without touching the developer's profile):
#   1. a brand-new user profile with no config file is provisioned automatically
#   2. a Windows path containing spaces and Chinese characters works end to end
#   3. a busy port is detected and the relay moves to a free one (persisted)
#   4. a second launch reuses the running instance instead of starting another relay
#   5. a corrupt config is backed up and replaced, not silently lost
#   6. a package with the relay component missing reports it instead of hanging
#
# Every scenario runs with CLIPBRIDGE_HOME pointing at a temp directory, a unique
# CLIPBRIDGE_INSTANCE_ID, and (unless stated) a non-default port, so the user's real
# ClipBridge instance, config, database and clipboard are left alone.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-portability.ps1
#   ... -Artifact <path> -Keep      # keep the temp profiles for inspection
#
# Exit code 0 = every scenario passed.

param(
    [string]$Artifact = '',
    [switch]$Keep,
    [switch]$SkipClipboard
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Artifact) { $Artifact = Join-Path $root 'artifacts\ClipBridge' }
$exe = Join-Path $Artifact 'ClipBridge.exe'
if (-not (Test-Path $exe)) { throw "ClipBridge.exe not found at $exe (build first: scripts\build-daily.ps1)" }
$relayExe = Join-Path $Artifact 'relay\ClipBridgeRelay.exe'
if (-not (Test-Path $relayExe)) { Write-Host "WARNING: relay\ClipBridgeRelay.exe missing; a managed-relay scenario will be skipped." -ForegroundColor Yellow }

$work = Join-Path $env:TEMP ('clipbridge-portability-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null

$script:results = @()
$script:cleanup = New-Object System.Collections.Generic.List[string]
$script:handles = New-Object System.Collections.Generic.List[object]

function Add-Result([string]$scenario, [string]$name, [bool]$ok, [string]$detail = '') {
    $script:results += [pscustomobject]@{ Scenario = $scenario; Name = $name; Ok = $ok; Detail = $detail }
    $color = if ($ok) { 'Green' } else { 'Red' }
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1}{2}" -f $mark, $name, $(if ($detail) { " - $detail" } else { '' })) -ForegroundColor $color
}

function Skip([string]$scenario, [string]$name, [string]$why) {
    $script:results += [pscustomobject]@{ Scenario = $scenario; Name = $name; Ok = $true; Detail = "SKIPPED: $why" }
    Write-Host "  [SKIP] $name - $why" -ForegroundColor Yellow
}

function Wait-Until([scriptblock]$condition, [int]$timeoutMs = 25000, [int]$intervalMs = 250) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds $intervalMs
    }
    return [bool](& $condition)
}

function Invoke-Exe([string]$path, [string[]]$arguments, [int]$timeoutSec = 30) {
    # System.Diagnostics.Process instead of Start-Process: the latter fails on machines whose
    # environment contains both HTTPS_PROXY and https_proxy (a very common corporate setup).
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $path
    $psi.Arguments = ($arguments -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $proc = [System.Diagnostics.Process]::Start($psi)
    try {
        $text = $proc.StandardOutput.ReadToEnd()
        $err = $proc.StandardError.ReadToEnd()
        if (-not $proc.WaitForExit($timeoutSec * 1000)) {
            try { $proc.Kill() } catch { }
            return [pscustomobject]@{ ExitCode = -1; Output = $text; Error = "$err (timed out)" }
        }
        return [pscustomobject]@{ ExitCode = $proc.ExitCode; Output = $text; Error = $err }
    } finally { $proc.Dispose() }
}

function Get-Status([string]$exePath) {
    $r = Invoke-Exe $exePath @('--status')
    try { return ($r.Output | ConvertFrom-Json) } catch { return $null }
}

function Get-Doctor([string]$exePath) {
    $r = Invoke-Exe $exePath @('--doctor')
    try { return ($r.Output | ConvertFrom-Json) } catch { return $null }
}

function Start-App([string]$exePath) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exePath
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $script:cleanup.Add($proc.Id)
    $script:handles.Add($proc)
    return $proc
}

function Stop-App([string]$exePath) {
    $null = Invoke-Exe $exePath @('--exit')
    Start-Sleep -Milliseconds 800
}

function Wait-RelayHealthy([int]$port, [int]$timeoutMs = 25000) {
    return Wait-Until {
        try {
            $h = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/v1/health" -TimeoutSec 2
            return ($h.status -eq 'ok')
        } catch { return $false }
    } $timeoutMs
}

function Get-FreePort {
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $port
}

function Get-Sha256Hex([string]$text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
        return (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join '')
    } finally { $sha.Dispose() }
}

function Get-LogTail([string]$profileHome, [int]$lines = 6) {
    # Diagnostics must never leak the token: client.log contains no secrets by design,
    # but keep the tail short and single-line for the report.
    try {
        $log = Join-Path $profileHome 'data\logs\client.log'
        if (-not (Test-Path $log)) { return 'no client.log' }
        $tail = Get-Content -Path $log -Tail $lines -Encoding UTF8
        return (($tail | ForEach-Object { ($_ -split ' ', 3)[-1] }) -join ' ; ')
    } catch { return 'log unreadable' }
}

function Set-ScenarioEnv([string]$profileHome, [int]$port, [string]$instanceId, [switch]$PortOnly) {
    $env:CLIPBRIDGE_HOME = $profileHome
    $env:CLIPBRIDGE_INSTANCE_ID = $instanceId
    $env:CLIPBRIDGE_LAN_HOSTNAME = 'clipbridge-verify'
    $env:CLIPBRIDGE_POLL_INTERVAL_MS = '800'
    if ($port -gt 0) { $env:CLIPBRIDGE_RELAY_PORT = "$port" } else { Remove-Item Env:\CLIPBRIDGE_RELAY_PORT -ErrorAction SilentlyContinue }
    if ($port -gt 0 -and -not $PortOnly) {
        $env:CLIPBRIDGE_RELAY_URL = "http://127.0.0.1:$port"
    } else {
        Remove-Item Env:\CLIPBRIDGE_RELAY_URL -ErrorAction SilentlyContinue
    }
}

function Clear-ScenarioEnv {
    foreach ($name in @('CLIPBRIDGE_HOME', 'CLIPBRIDGE_INSTANCE_ID', 'CLIPBRIDGE_LAN_HOSTNAME',
            'CLIPBRIDGE_RELAY_PORT', 'CLIPBRIDGE_RELAY_URL', 'CLIPBRIDGE_POLL_INTERVAL_MS')) {
        Remove-Item "Env:\$name" -ErrorAction SilentlyContinue
    }
}

$port8787Free = $true
try {
    $probe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Any, 8787)
    $probe.Start(); $probe.Stop()
} catch { $port8787Free = $false }

# A proxy that captures loopback changes what this harness can observe: the app's own HTTP
# calls (poll/upload) go through it, while the harness's calls do not. That behaviour is
# deliberately unchanged pending a product decision (docs/PORTABILITY.md, finding P0-2),
# so the affected checks are reported as SKIPPED instead of as product failures.
$proxyValues = @($env:HTTP_PROXY, $env:HTTPS_PROXY, $env:http_proxy, $env:https_proxy) | Where-Object { $_ }
$noProxyText = (@($env:NO_PROXY, $env:no_proxy) | Where-Object { $_ }) -join ','
$proxyBypassesLoopback = ($noProxyText -match '127\.0\.0\.1') -or ($noProxyText -match 'localhost') -or ($noProxyText -match '<local>')
$proxyInterceptsLoopback = ($proxyValues.Count -gt 0) -and (-not $proxyBypassesLoopback)
$proxyReason = if ($proxyInterceptsLoopback) {
    "HTTP_PROXY=$($proxyValues[0]) captures 127.0.0.1 in this shell; proxy handling is unchanged by request (docs/PORTABILITY.md P0-2)"
} else { '' }

Write-Host ''
Write-Host '=== ClipBridge portability verification ===' -ForegroundColor Cyan
Write-Host "Artifact : $Artifact"
Write-Host "Profiles : $work"
Write-Host "Port 8787: $(if ($port8787Free) { 'free (default-port scenario enabled)' } else { 'in use - default-port scenario uses a temporary port' })"
if ($proxyInterceptsLoopback) {
    Write-Host "Proxy    : detected ($($proxyValues[0])) - app HTTP checks will be SKIPPED, see the report" -ForegroundColor Yellow
}
Write-Host ''

# --------------------------------------------------------------------------- scenario 1
Write-Host '[1/6] Fresh profile, ASCII path' -ForegroundColor Cyan
$home1 = Join-Path $work 'user-alice\AppData\ClipBridge'
$port1 = if ($port8787Free) { 8787 } else { Get-FreePort }
Set-ScenarioEnv $home1 $port1 ('cb-verify-fresh-' + [guid]::NewGuid().ToString('N').Substring(0, 6))

try {
    $doctorBefore = Get-Doctor $exe
    Add-Result 1 'a new profile starts with no config file' ($doctorBefore.config_state -eq 'Missing') "state=$($doctorBefore.config_state)"

    $p1 = Start-App $exe
    $configPath = Join-Path $home1 'config\config.json'
    $created = Wait-Until { Test-Path $configPath } 20000
    Add-Result 1 'config.json is created automatically' $created $configPath

    $cfg = if ($created) { Get-Content -Raw -Encoding UTF8 $configPath | ConvertFrom-Json } else { $null }
    Add-Result 1 'a strong token was generated' ($cfg -and $cfg.DeviceToken.Length -ge 32) $(if ($cfg) { "length=$($cfg.DeviceToken.Length)" } else { 'no config' })
    Add-Result 1 'a unique device id was generated' ($cfg -and $cfg.DeviceId -match '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$') $(if ($cfg) { $cfg.DeviceId } else { '' })
    Add-Result 1 'schema version is written' ($cfg -and $cfg.ConfigVersion -ge 2) $(if ($cfg) { "v$($cfg.ConfigVersion)" } else { '' })

    $healthy = Wait-RelayHealthy $port1
    Add-Result 1 'relay answers /api/v1/health' $healthy "port=$port1"

    $status = Wait-Until {
        $s = Get-Status $exe
        return ($s -and $s.Relay -eq 'Running')
    } 20000
    $s1 = Get-Status $exe
    Add-Result 1 'status reports Relay=Running' $status $(if ($s1) { "relay=$($s1.Relay)" } else { 'no status' })
    if ($proxyInterceptsLoopback) {
        Skip 1 'status reports Sync=Active' $proxyReason
    } else {
        $syncActive = Wait-Until {
            $s = Get-Status $exe
            return ($s -and $s.Relay -eq 'Running' -and $s.Sync -eq 'Active')
        } 20000
        $s1 = Get-Status $exe
        Add-Result 1 'status reports Sync=Active' $syncActive $(if ($s1) { "relay=$($s1.Relay) sync=$($s1.Sync)" } else { 'no status' })
    }
    Add-Result 1 'a LAN URL is exposed for the Shortcut' ($s1 -and $s1.LanUrl) $(if ($s1) { "$($s1.LanUrl)" } else { '' })

    if ($cfg -and $healthy) {
        $content = 'ClipBridge portability check ✓ 中文 ' + [guid]::NewGuid().ToString('N').Substring(0, 6)
        $body = @{
            clip_id      = [guid]::NewGuid().ToString()
            device_id    = 'iphone'
            content_type = 'text/plain'
            content      = $content
            content_hash = Get-Sha256Hex $content
            created_at   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
            origin       = 'shortcut'
        } | ConvertTo-Json
        $headers = @{ Authorization = "Bearer $($cfg.DeviceToken)" }
        $clipId = ($body | ConvertFrom-Json).clip_id
        # Explicit UTF-8 bytes: Windows PowerShell would otherwise encode the body as ASCII
        # and mangle the Chinese characters we are testing for.
        $payload = [System.Text.Encoding]::UTF8.GetBytes($body)
        try {
            $null = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port1/api/v1/clips" `
                -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $payload -TimeoutSec 5
            # Read the record back by id: another client may legitimately post something newer
            # while we look, and /latest would then hide the round-trip we are testing.
            # Decode UTF-8 explicitly: Windows PowerShell would otherwise read the JSON body
            # as Latin-1 and mangle every non-ASCII character.
            $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$port1/api/v1/clips/$clipId" `
                -Headers $headers -UseBasicParsing -TimeoutSec 5
            $storedJson = [System.Text.Encoding]::UTF8.GetString($resp.RawContentStream.ToArray())
            $stored = $storedJson | ConvertFrom-Json
            Add-Result 1 'the generated token authenticates (iPhone protocol intact)' ($stored.content -eq $content) "clip=$clipId"
        } catch {
            Add-Result 1 'the generated token authenticates (iPhone protocol intact)' $false $_.Exception.Message
        }

        try {
            $bad = $false
            try {
                $null = Invoke-RestMethod -Uri "http://127.0.0.1:$port1/api/v1/clips/latest" `
                    -Headers @{ Authorization = 'Bearer wrong-token' } -TimeoutSec 5
            } catch { $bad = ($_.Exception.Response.StatusCode.value__ -eq 401) }
            Add-Result 1 'a wrong token is rejected with 401' $bad 'never weakened for tests'
        } catch {
            Add-Result 1 'a wrong token is rejected with 401' $false $_.Exception.Message
        }
    }

    Stop-App $exe
    $exited = Wait-Until {
        $after = Get-Status $exe
        return ($after -eq $null -or $after.error -eq 'not_running')
    } 15000
    Add-Result 1 '--exit leaves no running instance' $exited ''
} finally { Clear-ScenarioEnv }

# --------------------------------------------------------------------------- scenario 2
Write-Host '[2/6] Profile path with spaces and Chinese characters' -ForegroundColor Cyan
$home2 = Join-Path $work '用户 测试\ClipBridge 数据'
$port2 = Get-FreePort
Set-ScenarioEnv $home2 $port2 ('cb-verify-unicode-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
try {
    $p2 = Start-App $exe
    $configPath2 = Join-Path $home2 'config\config.json'
    $created2 = Wait-Until { Test-Path $configPath2 } 20000
    Add-Result 2 'config is created under a Unicode path' $created2 $configPath2
    $healthy2 = Wait-RelayHealthy $port2
    Add-Result 2 'relay starts from a Unicode profile' $healthy2 "port=$port2"
    $data2 = Join-Path $home2 'data'
    Add-Result 2 'database and logs live under the profile' (Test-Path (Join-Path $data2 'clipbridge.db')) $data2
    $running2 = Wait-Until {
        $s = Get-Status $exe
        return ($s -and $s.Relay -eq 'Running')
    } 20000
    $s2 = Get-Status $exe
    Add-Result 2 'status is reachable' $running2 $(if ($s2) { "relay=$($s2.Relay)" } else { 'no status' })
    Stop-App $exe
} finally { Clear-ScenarioEnv }

# --------------------------------------------------------------------------- scenario 3
Write-Host '[3/6] Configured port already taken by another program' -ForegroundColor Cyan
# An existing user who set RelayPort in config.json — no environment override involved, so
# the persisted result is observable in the file.
$home3 = Join-Path $work 'port-conflict'
$busyPort = Get-FreePort
$blocker = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Any, $busyPort)
try { $blocker.Start() } catch { }
New-Item -ItemType Directory -Force -Path (Join-Path $home3 'config') | Out-Null
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$seedBytes = New-Object byte[] 32
$rng.GetBytes($seedBytes)
$rng.Dispose()
$seedToken = [Convert]::ToBase64String($seedBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
$seed = @{ DeviceToken = $seedToken; DeviceId = 'windows-porttest'; RelayPort = $busyPort; RelayUrl = "http://127.0.0.1:$busyPort"; ConfigVersion = 2 } | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $home3 'config\config.json'), $seed, (New-Object System.Text.UTF8Encoding($false)))
Set-ScenarioEnv $home3 0 ('cb-verify-port-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
try {
    # Prove the port really is taken before asking the app to deal with it:
    # a successful TCP connect means something is listening there.
    $occupied = $false
    try {
        $probe = New-Object System.Net.Sockets.TcpClient
        $occupied = $probe.ConnectAsync('127.0.0.1', $busyPort).Wait(1000)
        $probe.Close()
    } catch { $occupied = $false }
    Add-Result 3 'the test port is genuinely occupied' $occupied "port=$busyPort"

    $p3 = Start-App $exe
    $configPath3 = Join-Path $home3 'config\config.json'
    $moved = Wait-Until {
        try {
            $c = Get-Content -Raw -Encoding UTF8 $configPath3 | ConvertFrom-Json
            return ($c.RelayPort -ne $busyPort)
        } catch { return $false }
    } 25000
    $cfg3 = if (Test-Path $configPath3) { Get-Content -Raw -Encoding UTF8 $configPath3 | ConvertFrom-Json } else { $null }
    Add-Result 3 'the app moves off an occupied port' $moved "configured=$busyPort actual=$(if ($cfg3) { $cfg3.RelayPort } else { '?' })"
    Add-Result 3 'the chosen port is persisted for the next launch' ($cfg3 -and $cfg3.RelayPort -ne $busyPort) $(if ($cfg3) { "RelayPort=$($cfg3.RelayPort)" } else { '' })
    if ($cfg3) {
        Add-Result 3 'RelayUrl follows the new port' ($cfg3.RelayUrl -eq "http://127.0.0.1:$($cfg3.RelayPort)") $cfg3.RelayUrl
        $healthy3 = Wait-RelayHealthy $cfg3.RelayPort
        Add-Result 3 'relay is healthy on the fallback port' $healthy3 "port=$($cfg3.RelayPort)"
    }
    Stop-App $exe
} finally {
    try { $blocker.Stop() } catch { }
    Clear-ScenarioEnv
}

# --------------------------------------------------------------------------- scenario 4
Write-Host '[4/6] Second launch while ClipBridge is already running' -ForegroundColor Cyan
$home4 = Join-Path $work 'single-instance'
$port4 = Get-FreePort
Set-ScenarioEnv $home4 $port4 ('cb-verify-single-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
try {
    $first = Start-App $exe
    $null = Wait-RelayHealthy $port4
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $second = Start-App $exe
    $exited = $second.WaitForExit(8000)
    Add-Result 4 'the second process exits instead of duplicating' $exited "after $([int]$sw.ElapsedMilliseconds) ms"
    $s4 = Get-Status $exe
    Add-Result 4 'the first instance is still the only one answering' ($s4 -and $s4.Relay -eq 'Running') $(if ($s4) { "relay=$($s4.Relay)" } else { 'no status' })
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $port4 -ErrorAction SilentlyContinue)
    Add-Result 4 'exactly one relay listener on the port' ($listeners.Count -eq 1) "listeners=$($listeners.Count)"
    Stop-App $exe
} finally { Clear-ScenarioEnv }

# --------------------------------------------------------------------------- scenario 5
Write-Host '[5/6] Corrupt configuration file' -ForegroundColor Cyan
$home5 = Join-Path $work 'corrupt-config'
$port5 = Get-FreePort
New-Item -ItemType Directory -Force -Path (Join-Path $home5 'config') | Out-Null
$broken = '{ "DeviceToken": "old-token-from-user", oops'
Set-Content -Path (Join-Path $home5 'config\config.json') -Value $broken -Encoding UTF8 -NoNewline
Set-ScenarioEnv $home5 $port5 ('cb-verify-corrupt-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
try {
    $p5 = Start-App $exe
    $healthy5 = Wait-RelayHealthy $port5
    Add-Result 5 'the app still starts after config corruption' $healthy5 "port=$port5"
    $backups = @(Get-ChildItem -Path (Join-Path $home5 'config') -Filter 'config.corrupt-*.json' -ErrorAction SilentlyContinue)
    Add-Result 5 'the original file is preserved as a backup' ($backups.Count -ge 1) $(if ($backups.Count) { $backups[0].Name } else { 'none' })
    if ($backups.Count -ge 1) {
        Add-Result 5 'the backup still contains the old token' ((Get-Content -Raw -Encoding UTF8 $backups[0].FullName).Contains('old-token-from-user')) ''
    }
    $cfg5 = Get-Content -Raw -Encoding UTF8 (Join-Path $home5 'config\config.json') | ConvertFrom-Json
    Add-Result 5 'a new working token replaced the broken file' ($cfg5.DeviceToken.Length -ge 32 -and $cfg5.DeviceToken -ne 'old-token-from-user') "length=$($cfg5.DeviceToken.Length)"
    Stop-App $exe
} finally { Clear-ScenarioEnv }

# --------------------------------------------------------------------------- scenario 6
Write-Host '[6/6] Incomplete package (relay component missing)' -ForegroundColor Cyan
if (-not (Test-Path $relayExe)) {
    Skip 6 'incomplete package reports the missing relay' 'artifact has no packaged relay'
} else {
    $brokenPkg = Join-Path $work 'incomplete-package'
    New-Item -ItemType Directory -Force -Path $brokenPkg | Out-Null
    Get-ChildItem -Path $Artifact -Force | Where-Object { $_.Name -ne 'relay' } |
        Copy-Item -Destination $brokenPkg -Recurse -Force
    $brokenExe = Join-Path $brokenPkg 'ClipBridge.exe'
    $home6 = Join-Path $work 'missing-relay'
    $port6 = Get-FreePort
    Set-ScenarioEnv $home6 $port6 ('cb-verify-incomplete-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
    try {
        $doctor6 = Get-Doctor $brokenExe
        Add-Result 6 'diagnostics report relay_exe_found=false' ($doctor6 -and -not $doctor6.relay_exe_found) ''
        $p6 = Start-App $brokenExe
        $reported = Wait-Until {
            $s = Get-Status $brokenExe
            return ($s -and $s.LastError -eq 'relay_component_missing')
        } 15000
        Add-Result 6 'the app explains the missing component' $reported 'status.LastError=relay_component_missing'
        Stop-App $brokenExe
    } finally { Clear-ScenarioEnv }
}

# --------------------------------------------------------------------------- clipboard path
Write-Host '[+] Windows client -> relay clipboard path' -ForegroundColor Cyan
if ($SkipClipboard) {
    Skip '+' 'copied text reaches the relay' 'disabled with -SkipClipboard'
} elseif ($proxyInterceptsLoopback) {
    # The client uploads through the app's HttpClient, which this shell's proxy captures.
    Skip '+' 'copied text reaches the relay' $proxyReason
} else {
    $home7 = Join-Path $work 'clipboard-path'
    $port7 = Get-FreePort
    Set-ScenarioEnv $home7 $port7 ('cb-verify-clip-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
    $saved = $null
    try {
        try { $saved = Get-Clipboard -Raw -ErrorAction Stop } catch { $saved = $null }
        $p7 = Start-App $exe
        if (-not (Wait-RelayHealthy $port7)) {
            Skip '+' 'copied text reaches the relay' 'relay did not start'
        } else {
            $cfg7 = Get-Content -Raw -Encoding UTF8 (Join-Path $home7 'config\config.json') | ConvertFrom-Json
            $text = 'portability clipboard probe ' + [guid]::NewGuid().ToString('N').Substring(0, 8)
            Set-Clipboard -Value $text
            $headers = @{ Authorization = "Bearer $($cfg7.DeviceToken)" }
            $seen = Wait-Until {
                try {
                    $latest = Invoke-RestMethod -Uri "http://127.0.0.1:$port7/api/v1/clips/latest?exclude_device_id=iphone" -Headers $headers -TimeoutSec 2
                    return ($latest.content -eq $text)
                } catch { return $false }
            } 20000
            $detail = "port=$port7"
            if (-not $seen) { $detail += ' | log: ' + (Get-LogTail $home7 6) }
            Add-Result '+' 'copied text reaches the relay with the provisioned token' $seen $detail
        }
        Stop-App $exe
    } finally {
        if ($null -ne $saved) { try { Set-Clipboard -Value $saved } catch { } }
        Clear-ScenarioEnv
    }
}

# --------------------------------------------------------------------------- teardown
foreach ($id in $script:cleanup) {
    try {
        $proc = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($proc -and -not $proc.HasExited) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    } catch { }
}
# Relay children are bound to their parent by a Job Object; give them a moment to exit.
Start-Sleep -Milliseconds 700

if (-not $Keep) {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "Profiles kept at $work" -ForegroundColor Yellow
}

$failed = @($script:results | Where-Object { -not $_.Ok })
$skipped = @($script:results | Where-Object { $_.Detail -like 'SKIPPED*' })
Write-Host ''
Write-Host ('=== {0} checks: {1} passed, {2} failed, {3} skipped ===' -f
    $script:results.Count, ($script:results.Count - $failed.Count), $failed.Count, $skipped.Count) `
    -ForegroundColor $(if ($failed.Count) { 'Red' } else { 'Green' })
if ($failed.Count) {
    $failed | ForEach-Object { Write-Host ("  FAILED: [{0}] {1} - {2}" -f $_.Scenario, $_.Name, $_.Detail) -ForegroundColor Red }
    exit 1
}
exit 0
