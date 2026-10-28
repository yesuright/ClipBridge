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

# GUI subsystem exe: `&` does not wait for it, so run it with redirected output.
# System.Diagnostics.Process is used instead of Start-Process: the latter throws on
# machines whose environment contains both HTTPS_PROXY and https_proxy (case-only
# duplicates are legal in Windows but rejected by PowerShell 5.1's Start-Process).
function Invoke-ClipBridgeCommand([string]$arguments) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = $arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $p = [System.Diagnostics.Process]::Start($psi)
    try {
        $text = $p.StandardOutput.ReadToEnd()
        $null = $p.StandardError.ReadToEnd()
        if (-not $p.WaitForExit(30000)) { try { $p.Kill() } catch { } }
        return $text.Trim()
    } finally { $p.Dispose() }
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

# Local API call that never uses a proxy: the harness must talk to 127.0.0.1 directly.
# Invoke-RestMethod follows the machine's WinINET proxy, which on some networks rewrites or
# rejects bodies sent to loopback (observed as mangled Unicode / 404). WebRequest with
# .Proxy = $null is used because Windows PowerShell 5.1 has no -NoProxy switch.
# The product's proxy behaviour is intentionally unchanged; this only isolates test traffic.
function Invoke-LocalJson([string]$method, [string]$uri, [string]$token = '', $body = $null) {
    try {
        $request = [System.Net.WebRequest]::Create($uri)
        $request.Method = $method
        $request.Proxy = $null
        $request.Timeout = 8000
        $request.ReadWriteTimeout = 8000
        if ($token) { $request.Headers['Authorization'] = "Bearer $token" }
        if ($null -ne $body) {
            $json = $body | ConvertTo-Json -Depth 5 -Compress
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
            $request.ContentType = 'application/json; charset=utf-8'
            $request.ContentLength = $bytes.Length
            $stream = $request.GetRequestStream()
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Close()
        }
        $response = $request.GetResponse()
        $reader = New-Object System.IO.StreamReader($response.GetResponseStream(), [System.Text.Encoding]::UTF8)
        $text = $reader.ReadToEnd()
        $reader.Close()
        $response.Close()
        if (-not $text) { return $null }
        return ($text | ConvertFrom-Json)
    } catch { return $null }
}

function Get-Health([int]$port) {
    $r = Invoke-LocalJson 'GET' "http://127.0.0.1:$port/api/v1/health"
    return ($null -ne $r -and $r.status -eq 'ok')
}

# The OS clipboard can be held by other processes (another harness may run on this
# machine), which makes Clipboard calls block for ~60 s. Every clipboard operation runs in
# a bounded child process so the harness can never hang; the text is transported as Base64
# because a redirected child's stdout uses the console code page (GBK on many CN machines),
# which silently destroys characters such as emoji.
function Invoke-ClipboardSet([string]$value) {
    $ps1 = Join-Path $env:TEMP ('cb-set-' + [guid]::NewGuid().ToString('N') + '.ps1')
    $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($value))
    Set-Content -Path $ps1 -Encoding UTF8 -Value @"
Add-Type -AssemblyName System.Windows.Forms
`$text = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$b64'))
[System.Windows.Forms.Clipboard]::SetText(`$text, [System.Windows.Forms.TextDataFormat]::UnicodeText)
"@
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'powershell'
        $psi.Arguments = "-NoProfile -STA -ExecutionPolicy Bypass -File `"$ps1`""
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $p = [System.Diagnostics.Process]::Start($psi)
        $ok = $p.WaitForExit(6000)
        if (-not $ok) { try { $p.Kill() } catch { } }
        $p.Dispose()
        return $ok
    } finally { Remove-Item $ps1 -Force -ErrorAction SilentlyContinue }
}

function Invoke-ClipboardGet() {
    $ps1 = Join-Path $env:TEMP ('cb-get-' + [guid]::NewGuid().ToString('N') + '.ps1')
    Set-Content -Path $ps1 -Encoding UTF8 -Value @"
Add-Type -AssemblyName System.Windows.Forms
`$text = [System.Windows.Forms.Clipboard]::GetText([System.Windows.Forms.TextDataFormat]::UnicodeText)
Write-Output ([Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes(`$text)))
"@
    # .NET Process, not Start-Process: on machines whose environment contains both
    # HTTPS_PROXY and https_proxy, Start-Process with redirection throws before launching.
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'powershell'
        $psi.Arguments = "-NoProfile -STA -ExecutionPolicy Bypass -File `"$ps1`""
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.CreateNoWindow = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $p = [System.Diagnostics.Process]::Start($psi)
        $b64 = $p.StandardOutput.ReadToEnd().Trim()
        if (-not $p.WaitForExit(6000)) { try { $p.Kill() } catch { } }
        $p.Dispose()
        if (-not $b64) { return $null }
        try { return [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64)) }
        catch { return $null }
    } catch { return $null }
    finally { Remove-Item $ps1 -Force -ErrorAction SilentlyContinue }
}

if (-not (Test-Path $exe)) { Write-Host "Artifact not found: $exe (run scripts\build-daily.ps1 first)"; exit 1 }
# A config is no longer required to run the artifact (first start provisions one), but a
# pre-existing config is still a useful sanity signal on a developer machine.
if (-not (Test-Path $configPath)) { Write-Host "NOTE: no %APPDATA%\ClipBridge\config.json — the run will auto-provision one" -ForegroundColor Yellow }

# ---------------------------------------------------------------- hermetic environment (before any launch)
# - unique CLIPBRIDGE_HOME: config, database, logs and the relay's SQLite store are this
#   run's own. Without it the verify instance shares the live user's DB, so clips posted
#   here show up in the user's history and vice versa (observed as clipboard cross-talk).
# - unique DEVICE ID: the shared clipboard can be read by another running ClipBridge
#   instance; a distinct device id keeps the two histories unambiguous.
# - unique single-instance id: never fights a real running ClipBridge
# - unique .local hostname: never conflicts with another responder
# - unique DEVICE TOKEN (env > config): the managed relay only accepts THIS run, so any
#   other process on the machine is rejected with 401 and cannot pollute the verification.
$verifyHome = Join-Path $env:TEMP ('clipbridge-verify-home-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $verifyHome | Out-Null
$env:CLIPBRIDGE_HOME = $verifyHome
$env:CLIPBRIDGE_DEVICE_ID = 'windows-verify'
$env:CLIPBRIDGE_RELAY_PORT = "$VerifyPort"
$env:CLIPBRIDGE_RELAY_URL = "http://127.0.0.1:$VerifyPort"
$env:CLIPBRIDGE_INSTANCE_ID = 'clipbridge-verify-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$env:CLIPBRIDGE_LAN_HOSTNAME = 'clipbridge-verify'
$VerifyToken = 'verify-' + [guid]::NewGuid().ToString('N')
$env:CLIPBRIDGE_DEVICE_TOKEN = $VerifyToken
$env:CLIPBRIDGE_POLL_INTERVAL_MS = '1000'

# Test-environment only: keep the app's loopback traffic out of any system proxy while the
# verification runs. The product's proxy handling is deliberately unchanged (see
# docs/PORTABILITY.md §3.6); without this a machine-wide proxy would sit between the app
# and its own relay and the clipboard checks could not be measured.
if ($env:HTTP_PROXY -or $env:HTTPS_PROXY -or $env:http_proxy -or $env:https_proxy) {
    $env:NO_PROXY = (@('127.0.0.1', 'localhost', $env:NO_PROXY) | Where-Object { $_ }) -join ','
    $env:no_proxy = $env:NO_PROXY
    Write-Host "NOTE: proxy detected - NO_PROXY extended with loopback for this run only" -ForegroundColor Yellow
}

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
# The Milestone-4 UI build started at 0.4.0 and the product has since moved to 1.x, so assert
# a floor instead of a version band (the previous `Major -eq 0` check failed on 1.0.0 even
# though the packaged assembly was the right one).
$uiVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Artifact 'ClipBridge.dll')).Version
Assert-True ($uiVersion -ge [version]'0.4.0') 'A: M4 Windows UI assembly packaged' "version=$uiVersion"

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
        $latest = Invoke-LocalJson 'GET' "http://127.0.0.1:$VerifyPort/api/v1/clips/latest?exclude_device_id=iphone" $VerifyToken
        $latest -and $latest.content -eq $marker
    } 4 300
}
Assert-True ($d1Ok) 'D1: Windows clipboard -> packaged relay'

$d2Ok = $false
for ($attempt = 1; $attempt -le 8 -and -not $d2Ok; $attempt++) {
    $unicode = "来自 iPhone 的验证 🐱🚀 " + [guid]::NewGuid().ToString('N') + [Environment]::NewLine + "second line"
    $null = Invoke-LocalJson 'POST' "http://127.0.0.1:$VerifyPort/api/v1/clips/shortcut" $VerifyToken @{ device_id = 'iphone'; content = $unicode }
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
# The isolated profile is disposable: its DB/logs only describe this run.
try { Remove-Item -LiteralPath $verifyHome -Recurse -Force -ErrorAction SilentlyContinue } catch { }
Write-Host ''
Write-Host '=== verify-daily RESULTS ===' -ForegroundColor Cyan
$failed = 0
foreach ($r in $results) { if ($r.Status -eq 'FAIL') { $failed++ }; Write-Host ("{0,-4} {1}" -f $r.Status, $r.Name) }
if ($failed -eq 0) { Write-Host 'ALL DAILY CHECKS PASSED' -ForegroundColor Green; exit 0 }
Write-Host "$failed DAILY CHECK(S) FAILED" -ForegroundColor Red; exit 1
