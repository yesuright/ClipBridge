# Daily-use artifact integration smoke test (Milestone 4).
# Launches artifacts\ClipBridge\ClipBridge.exe and verifies the whole product lifecycle:
#   - no-console build (PE subsystem = GUI)
#   - relay auto-starts as a child and listens on 0.0.0.0:VERIFY_PORT
#   - single instance (second launch exits without a second process/relay)
#   - clipboard upload round-trip through the packaged relay
#   - relay crash recovery (child killed -> restarted with backoff)
#   - clean exit: tray process gone, relay child gone, port free, no orphans
#
# The run is hermetic: a dedicated port, a dedicated single-instance id and a dedicated
# .local hostname, so a real running ClipBridge (or any parallel test run on this machine)
# cannot cross-talk into this verification.
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\verify-daily.ps1
# Prereq: scripts\build-daily.ps1 has been run.

param(
    [string]$Artifact = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'artifacts\ClipBridge'),
    # The managed relay always binds the configured port. Keep the product default (8787):
    # the Shortcut pins it, and this machine's network protection is port-specific.
    [int]$VerifyPort = 8787
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $Artifact 'ClipBridge.exe'
$relayExePath = Join-Path $Artifact 'relay\ClipBridgeRelay.exe'
$configPath = Join-Path $env:APPDATA 'ClipBridge\config.json'

$results = [System.Collections.Generic.List[object]]::new()
function Pass($name, $detail = '') { $results.Add([pscustomobject]@{Name=$name;Status='PASS';Detail=$detail}); Write-Host "[PASS] $name $detail" -ForegroundColor Green }
function Fail($name, $detail = '') { $results.Add([pscustomobject]@{Name=$name;Status='FAIL';Detail=$detail}); Write-Host "[FAIL] $name $detail" -ForegroundColor Red }
function Assert-True([bool]$c, $name, $detail = '') { if ($c) { Pass $name $detail } else { Fail $name $detail } }

function Wait-Until([scriptblock]$cond, [int]$timeoutSec = 30, [int]$intervalMs = 500) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        try { if (& $cond) { return $true } } catch { }
        Start-Sleep -Milliseconds $intervalMs
    }
    return $false
}

# GUI subsystem exe: `&` does not wait for it; use Start-Process -Wait with redirected output.
function Invoke-ClipBridgeCommand([string]$arguments) {
    $out = Join-Path $env:TEMP ('cb-cmd-' + [guid]::NewGuid().ToString('N') + '.txt')
    $p = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -NoNewWindow `
        -RedirectStandardOutput $out -PassThru
    $text = if (Test-Path $out) { [System.IO.File]::ReadAllText($out).Trim() } else { '' }
    Remove-Item $out -Force -ErrorAction SilentlyContinue
    return $text
}

function Get-OwnClientProcesses {
    # CIM ExecutablePath is unreliable for these binaries; use Get-Process .Path instead.
    $own = @()
    foreach ($p in (Get-Process -Name 'ClipBridge' -ErrorAction SilentlyContinue)) {
        try { if ($p.Path -eq $exe) { $own += $p } } catch { }
    }
    return $own
}

function Get-OwnRelayProcesses([int]$parentPid = 0) {
    $own = @()
    foreach ($p in (Get-Process -Name 'ClipBridgeRelay' -ErrorAction SilentlyContinue)) {
        try {
            if ($p.Path -eq $relayExePath -and ($parentPid -eq 0 -or (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)" -ErrorAction SilentlyContinue).ParentProcessId -eq $parentPid)) {
                $own += $p
            }
        } catch { }
    }
    return $own
}

function Get-Health([int]$port) {
    try { return (Invoke-RestMethod "http://127.0.0.1:$port/api/v1/health" -TimeoutSec 2).status -eq 'ok' } catch { return $false }
}

# The OS clipboard can be held by other processes (another harness runs on this machine),
# which makes [System.Windows.Forms.Clipboard]-style calls block for ~60 s. Run every
# clipboard operation in a bounded child process so the harness can never hang.
# The OS clipboard can be held by other processes (another harness runs on this machine),
# which makes [System.Windows.Forms.Clipboard]-style calls block for ~60 s. Run every
# clipboard operation in a bounded child process (temp script file → no quoting issues)
# so the harness can never hang.
function Invoke-ClipboardSet([string]$value) {
    $ps1 = Join-Path $env:TEMP ('cb-set-' + [guid]::NewGuid().ToString('N') + '.ps1')
    Set-Content -Path $ps1 -Encoding UTF8 -Value @"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(`$false)
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Clipboard]::SetText('$value', [System.Windows.Forms.TextDataFormat]::UnicodeText)
"@
    $p = Start-Process powershell -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',$ps1 -PassThru -WindowStyle Hidden
    $ok = $p.WaitForExit(6000)
    if (-not $ok) { $p.Kill() }
    Remove-Item $ps1 -Force -ErrorAction SilentlyContinue
    return $ok
}

function Invoke-ClipboardGet() {
    $out = Join-Path $env:TEMP ('cb-get-' + [guid]::NewGuid().ToString('N') + '.txt')
    $ps1 = Join-Path $env:TEMP ('cb-get-' + [guid]::NewGuid().ToString('N') + '.ps1')
    Set-Content -Path $ps1 -Encoding UTF8 -Value @"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(`$false)
Add-Type -AssemblyName System.Windows.Forms
Write-Output ([System.Windows.Forms.Clipboard]::GetText([System.Windows.Forms.TextDataFormat]::UnicodeText))
"@
    $p = Start-Process powershell -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',$ps1 `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $out
    if (-not $p.WaitForExit(6000)) {
        $p.Kill()
        Remove-Item $out,$ps1 -Force -ErrorAction SilentlyContinue
        return $null
    }
    # Write-Output appends a line terminator; trim it away (markers have no trailing spaces).
    $text = if (Test-Path $out) { [System.IO.File]::ReadAllText($out).Trim() } else { '' }
    Remove-Item $out,$ps1 -Force -ErrorAction SilentlyContinue
    return $text
}

if (-not (Test-Path $exe)) { Write-Host "Artifact not found: $exe (run scripts\build-daily.ps1 first)"; exit 1 }
if (-not (Test-Path $configPath)) { Write-Host "No %APPDATA%\ClipBridge\config.json — configure DeviceToken first"; exit 1 }
$cfg = Get-Content $configPath -Raw | ConvertFrom-Json
if (-not $cfg.DeviceToken) { Write-Host 'config.json has no DeviceToken'; exit 1 }

# ---------------------------------------------------------------- hermetic environment (before any launch)
# - unique single-instance id: never fights a real running ClipBridge
# - unique .local hostname: never conflicts with another responder
# - unique DEVICE TOKEN (env > config): the managed relay only accepts THIS run, so any
#   other process on the machine (another agent's test harness) is rejected with 401
#   and cannot pollute the verification.
$env:CLIPBRIDGE_RELAY_PORT = "$VerifyPort"
$env:CLIPBRIDGE_RELAY_URL = "http://127.0.0.1:$VerifyPort"
$env:CLIPBRIDGE_INSTANCE_ID = 'clipbridge-verify-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$env:CLIPBRIDGE_LAN_HOSTNAME = 'clipbridge-verify'
$VerifyToken = 'verify-' + [guid]::NewGuid().ToString('N')
$env:CLIPBRIDGE_DEVICE_TOKEN = $VerifyToken
$env:CLIPBRIDGE_POLL_INTERVAL_MS = '1000'

# ---------------------------------------------------------------- preflight: nothing else on our id/port
$existing = Invoke-ClipBridgeCommand '--status'
if ($existing -and $existing -notmatch 'not_running') {
    Write-Host "A ClipBridge verify instance is already running (id $env:CLIPBRIDGE_INSTANCE_ID) — aborting." -ForegroundColor Red
    exit 1
}
if (Get-Health $VerifyPort) {
    Write-Host "Port $VerifyPort is already serving — aborting." -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- A. no console (PE subsystem = GUI)
$bytes = [System.IO.File]::ReadAllBytes($exe)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
$subsystem = [BitConverter]::ToUInt16($bytes, $peOffset + 24 + 68)
Assert-True ($subsystem -eq 2) 'A: exe is a GUI subsystem binary (no console window)' "subsystem=$subsystem"
Assert-True ($subsystem -ne 3) 'A: not a console binary' "subsystem=$subsystem"
Assert-True ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $Artifact 'ClipBridge.dll')).Version.Major -eq 0 -and
    [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Artifact 'ClipBridge.dll')).Version.Minor -ge 4) 'A: M4 Windows UI assembly packaged'

# Sign-in uses a launch argument, not a standalone registry command. Exercise it with
# this harness's existing isolated instance id; do not modify the user's Run entry.
$signIn = Start-Process -FilePath $exe -ArgumentList '--tray-autostart' -PassThru -WindowStyle Hidden
$signInReady = Wait-Until { Get-Health $VerifyPort } 40 500
$signIn.Refresh()
Assert-True ($signInReady -and -not $signIn.HasExited) 'A: sign-in launch starts the daily app'
Assert-True ($signIn.MainWindowHandle -eq 0) 'A: sign-in launch stays in the tray'
Invoke-ClipBridgeCommand '--exit' | Out-Null
$signInGone = Wait-Until { -not (Get-Process -Id $signIn.Id -ErrorAction SilentlyContinue) } 20 500
Assert-True ($signInGone) 'A: sign-in instance exits cleanly'

# ---------------------------------------------------------------- B. launch artifact
Write-Host ''
Write-Host '=== launching artifact ===' -ForegroundColor Cyan
$client = Start-Process -FilePath $exe -PassThru

$healthOk = Wait-Until { Get-Health $VerifyPort } 40 500
Assert-True ($healthOk) 'B: relay health after launch'
Assert-True (-not $client.HasExited) 'B: client process alive after launch'
$windowShown = Wait-Until { $client.Refresh(); $client.MainWindowHandle -ne 0 } 10 100
Assert-True ($windowShown) 'B: normal launch opens the native main window'

$listener = Get-NetTCPConnection -LocalPort $VerifyPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
Assert-True ($null -ne $listener) 'B: relay listening on the verify port'
Assert-True ($listener.LocalAddress -eq '0.0.0.0') 'B: relay bound to 0.0.0.0 (LAN mode for iPhone)' "bound=$($listener.LocalAddress)"
# The relay child is hard to enumerate by name in its first seconds (path/parent lookup
# races), so use the listener's owning process — which is the relay by construction.
$relayPid = if ($listener) { [int]$listener.OwningProcess } else { 0 }
Assert-True ($relayPid -gt 0) 'B: packaged relay child process running' "pid=$relayPid"

$statusText = Invoke-ClipBridgeCommand '--status'
$statusJson = $statusText | ConvertFrom-Json
Assert-True ($statusJson.Relay -eq 'Running') 'B: status relay=Running'
# Readiness must be published even when the relay came up before the first poll failed:
# the engine's health event only fires on a transition, so a naive implementation left the
# UI on "Offline" while sync actually worked (found by tests\e2e\relay-lifecycle-stress.ps1).
$syncActive = Wait-Until {
    $s = Invoke-ClipBridgeCommand '--status'
    $s -and (($s | ConvertFrom-Json).Sync -eq 'Active')
} 20 500
Assert-True ($syncActive) 'B: status sync=Active (relay up AND answering)'
Assert-True ($statusJson.App -eq 'Running') 'B: status app=Running'
Assert-True ($statusJson.LanUrl -match '^http://') "B: status has a LAN URL ($($statusJson.LanUrl))"
Assert-True ($statusJson.Mdns -in @('Advertising','Off','Conflict','Error')) 'B: mDNS state is tracked'

# ---------------------------------------------------------------- C. single instance
Write-Host ''
Write-Host '=== single instance ===' -ForegroundColor Cyan
$second = Start-Process -FilePath $exe -PassThru
$secondExited = Wait-Until { $second.HasExited } 15 300
Assert-True ($secondExited) 'C: second instance exits by itself'
Start-Sleep -Seconds 1
$clients = Get-OwnClientProcesses
Assert-True ($clients.Count -eq 1) "C: exactly one client process (got $($clients.Count))"
$relays = Get-OwnRelayProcesses -parentPid $client.Id
Assert-True ($relays.Count -eq 1) "C: exactly one relay child of the client (got $($relays.Count))"

# ---------------------------------------------------------------- D. clipboard round-trip via packaged relay
Write-Host ''
Write-Host '=== clipboard round-trip ===' -ForegroundColor Cyan
# NOTE: this machine can run ANOTHER ClipBridge test harness concurrently on the shared
# OS clipboard (seen in practice). To stay deterministic the harness uses:
#  - D1 (Windows -> relay): ASCII marker (PS Set-Clipboard is exact + fast) with retries;
#  - D2 (relay -> Windows, Unicode): POST a unicode marker as "iphone" (the exact iPhone
#    Shortcut path) and wait until the client applies it to the clipboard.
$d1Ok = $false
for ($attempt = 1; $attempt -le 8 -and -not $d1Ok; $attempt++) {
    $marker = "verify-daily-" + [guid]::NewGuid().ToString('N')
    if (-not (Invoke-ClipboardSet $marker)) { continue }
    $d1Ok = Wait-Until {
        $latest = Invoke-RestMethod -Uri "http://127.0.0.1:$VerifyPort/api/v1/clips/latest?exclude_device_id=iphone" -Headers @{Authorization="Bearer $VerifyToken"} -TimeoutSec 2
        $latest.content -eq $marker
    } 4 300
}
Assert-True ($d1Ok) 'D1: Windows clipboard -> packaged relay'

$d2Ok = $false
for ($attempt = 1; $attempt -le 8 -and -not $d2Ok; $attempt++) {
    $unicode = "来自 iPhone 的验证 🐱🚀 " + [guid]::NewGuid().ToString('N') + [Environment]::NewLine + "second line"
    $null = Invoke-RestMethod -Method POST -Uri "http://127.0.0.1:$VerifyPort/api/v1/clips/shortcut" `
        -Headers @{Authorization="Bearer $VerifyToken"} -ContentType 'application/json; charset=utf-8' `
        -Body (@{ device_id = 'iphone'; content = $unicode } | ConvertTo-Json -Depth 3) -TimeoutSec 5
    $d2Ok = Wait-Until { (Invoke-ClipboardGet) -eq $unicode } 4 300
}
Assert-True ($d2Ok) 'D2: packaged relay -> Windows clipboard (exact Unicode)'

# ---------------------------------------------------------------- E. relay crash recovery
Write-Host ''
Write-Host '=== relay crash recovery ===' -ForegroundColor Cyan
if ($relayPid -gt 0) { Stop-Process -Id $relayPid -Force -ErrorAction SilentlyContinue }
$relayBackUp = Wait-Until {
    $p = Get-OwnRelayProcesses -parentPid $client.Id | Select-Object -First 1
    $p -and [int]$p.Id -ne $relayPid
} 40 700
Assert-True ($relayBackUp) 'E: relay restarted after crash (bounded restart policy)'
$healthAgain = Wait-Until { Get-Health $VerifyPort } 30 500
Assert-True ($healthAgain) 'E: relay healthy again after crash recovery'
Assert-True (-not (Get-Process -Id $client.Id -ErrorAction SilentlyContinue).HasExited) 'E: client survived relay crash'

# ---------------------------------------------------------------- F. clean exit, no orphan
Write-Host ''
Write-Host '=== clean exit ===' -ForegroundColor Cyan
Invoke-ClipBridgeCommand '--exit' | Out-Null
$gone = Wait-Until { -not (Get-Process -Id $client.Id -ErrorAction SilentlyContinue) } 20 500
Assert-True ($gone) 'F: client process exited'
$orphans = Get-OwnRelayProcesses -parentPid $client.Id
Assert-True ($orphans.Count -eq 0) "F: no relay orphan (found $($orphans.Count))"
$portFree = -not (Get-NetTCPConnection -LocalPort $VerifyPort -State Listen -ErrorAction SilentlyContinue)
Assert-True ($portFree) 'F: verify port released after exit'

# ---------------------------------------------------------------- report
Write-Host ''
Write-Host '=== verify-daily RESULTS ===' -ForegroundColor Cyan
$failed = 0
foreach ($r in $results) { if ($r.Status -eq 'FAIL') { $failed++ }; Write-Host ("{0,-4} {1}" -f $r.Status, $r.Name) }
if ($failed -eq 0) { Write-Host 'ALL DAILY CHECKS PASSED' -ForegroundColor Green; exit 0 }
Write-Host "$failed DAILY CHECK(S) FAILED" -ForegroundColor Red; exit 1
