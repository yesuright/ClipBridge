# ClipBridge relay lifecycle stress (regression for the 2026-09-10 "permanently Offline" incident).
#
# The real failure: the relay child was force-killed by a reboot, every post-reboot start
# failed the startup health check, the bounded restart budget was consumed in ~90 s, and the
# app then stayed Relay=Failed / Sync=Offline FOREVER — the restart budget could only be
# reset inside the child's Exited handler, and a terminal Failed state has no child.
#
# This harness verifies, against the REAL daily artifact:
#   Phase A  long-running app survives N unexpected relay exits (100% recovery, new PID,
#            8787 listening, health OK, status back to Relay=Running + Sync=Active)
#   Phase B  full relaunch cycles (launch -> kill relay -> recover -> clean exit)
#   Phase C  budget exhaustion -> relay_restart_exhausted logged -> SELF-HEAL with no user
#            action, twice (proves a stable run resets the budget and a later crash still
#            recovers, and that exhaustion is never a dead end)
#
# Hermetic: unique single-instance id, unique relay port, unique token, unique .local name.
# The client config and the SQLite history DB are backed up and restored (the managed relay
# child pins the standard LOCALAPPDATA db path — see docs/LIFECYCLE.md).
#
# RUN: powershell -ExecutionPolicy Bypass -File tests\e2e\relay-lifecycle-stress.ps1
#      [-Cycles 20] [-RelaunchCycles 4] [-ExhaustionRounds 2] [-Port 8897]

param(
    [int]$Cycles = 20,
    [int]$RelaunchCycles = 4,
    [int]$ExhaustionRounds = 2,
    [int]$Port = 8897,
    [string]$Artifact = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'artifacts\ClipBridge')
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $Artifact 'ClipBridge.exe'
$InstanceId = 'cb-lifecycle-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$script:App = $null
$script:Results = [System.Collections.Generic.List[object]]::new()
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Step([string]$m) { Write-Host "  $m" -ForegroundColor DarkGray }
function Pass([string]$name, [string]$detail = '') {
    $script:Results.Add([pscustomobject]@{ Name = $name; Ok = $true })
    Write-Host "[PASS] $name $detail" -ForegroundColor Green
}
function Fail([string]$name, [string]$detail = '') {
    $script:Results.Add([pscustomobject]@{ Name = $name; Ok = $false })
    $script:Failures.Add("$name $detail")
    Write-Host "[FAIL] $name $detail" -ForegroundColor Red
}

function Get-Status {
    $guard = $env:CLIPBRIDGE_INSTANCE_ID
    $env:CLIPBRIDGE_INSTANCE_ID = $InstanceId
    try {
        $out = Join-Path $env:TEMP ('cb-lc-' + [guid]::NewGuid().ToString('N') + '.txt')
        Start-Process -FilePath $exe -ArgumentList '--status' -Wait -NoNewWindow -RedirectStandardOutput $out | Out-Null
        $text = if (Test-Path $out) { [System.IO.File]::ReadAllText($out).Trim() } else { '' }
        Remove-Item $out -Force -ErrorAction SilentlyContinue
        if ($text) { return ($text | ConvertFrom-Json) }
    } catch { }
    finally { $env:CLIPBRIDGE_INSTANCE_ID = $guard }
    return $null
}

function Get-ListenerPid {
    try {
        $c = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($c) { return [int]$c.OwningProcess }
    } catch { }
    return 0
}

function Test-Health {
    try { return (Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/v1/health" -TimeoutSec 2).status -eq 'ok' }
    catch { return $false }
}

function Wait-Until([scriptblock]$cond, [int]$timeoutSec, [int]$intervalMs = 250) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        try { if (& $cond) { return $true } } catch { }
        Start-Sleep -Milliseconds $intervalMs
    }
    return $false
}

function Wait-Ready([int]$timeoutSec = 45) {
    return (Wait-Until {
        $s = Get-Status
        ($s -and $s.Relay -eq 'Running' -and $s.Sync -eq 'Active' -and -not $s.LastError) -and (Test-Health)
    } $timeoutSec 300)
}

function Start-App {
    $env:CLIPBRIDGE_INSTANCE_ID = $InstanceId
    $env:CLIPBRIDGE_RELAY_PORT = "$Port"
    $env:CLIPBRIDGE_RELAY_URL = "http://127.0.0.1:$Port"
    $script:App = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
    return (Wait-Ready 45)
}

function Stop-App {
    if ($script:App -and -not $script:App.HasExited) {
        $env:CLIPBRIDGE_INSTANCE_ID = $InstanceId
        try { & $exe --exit | Out-Null } catch { }
        if (-not $script:App.WaitForExit(15000)) { Stop-Process -Id $script:App.Id -Force -ErrorAction SilentlyContinue }
    }
    $script:App = $null
    # wait for the port to be released (no orphan relay)
    Wait-Until { (Get-ListenerPid) -eq 0 } 15 250 | Out-Null
}

function Assert-NoOrphan([string]$label) {
    if ((Get-ListenerPid) -ne 0) { Fail "$label orphan" "relay still listening on $Port after app exit" }
}

# ------------------------------------------------------------------ preflight
if (-not (Test-Path $exe)) { Write-Host "Artifact not found: $exe (run scripts\build-daily.ps1)"; exit 1 }

$others = @(Get-Process -Name ClipBridge -ErrorAction SilentlyContinue)
if ($others.Count -gt 0) {
    Write-Host "Another ClipBridge instance is running (pid $($others.Id -join ', '))." -ForegroundColor Red
    Write-Host 'The managed relay pins the shared LOCALAPPDATA db; stop it first (tray -> Exit ClipBridge) and re-run.'
    exit 2
}

$cfgDir = Join-Path $env:APPDATA 'ClipBridge'
$cfgPath = Join-Path $cfgDir 'config.json'
# SAFETY: this harness configures the app through ENVIRONMENT VARIABLES ONLY
# (ClientConfig resolves env > file > defaults). It must never write %APPDATA%\ClipBridge\
# config.json: an earlier revision backed the file up in memory and restored it, which
# propagated a test config into the user's real config when a run aborted before the
# restore step. The hash guard below fails the run if the file changes at all.
if (-not (Test-Path $cfgPath)) {
    Write-Host "No %APPDATA%\ClipBridge\config.json — configure the device token first (see docs/DAILY_USE.md)."
    exit 1
}
$cfgHashBefore = (Get-FileHash $cfgPath -Algorithm SHA256).Hash
$script:Token = 'lifecycle-' + [guid]::NewGuid().ToString('N')
$env:CLIPBRIDGE_INSTANCE_ID = $InstanceId
$env:CLIPBRIDGE_RELAY_PORT = "$Port"
$env:CLIPBRIDGE_RELAY_URL = "http://127.0.0.1:$Port"
$env:CLIPBRIDGE_LAN_HOSTNAME = 'clipbridge-lifecycle'
$env:CLIPBRIDGE_DEVICE_TOKEN = $script:Token
$env:CLIPBRIDGE_POLL_INTERVAL_MS = '1000'

$dataDir = Join-Path $env:LOCALAPPDATA 'ClipBridge'
$dbFiles = @('clipbridge.db', 'clipbridge.db-wal', 'clipbridge.db-shm')
$dbBackups = @{}
foreach ($f in $dbFiles) {
    $p = Join-Path $dataDir $f
    if (Test-Path $p) { $dbBackups[$f] = Join-Path $env:TEMP ("cb-lc-db-$InstanceId-$f"); Copy-Item $p $dbBackups[$f] -Force }
}
$log = Join-Path $dataDir 'logs\client.log'
function Log-Len { if (Test-Path $log) { (Get-Item $log).Length } else { 0 } }
function Log-Since([long]$from) {
    if (-not (Test-Path $log)) { return @() }
    $fs = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite'); $fs.Seek($from, 'Begin') | Out-Null
    $sr = New-Object System.IO.StreamReader($fs); $t = $sr.ReadToEnd(); $sr.Close(); $fs.Close()
    return ($t -split "`r?`n" | Where-Object { $_ -ne '' })
}

Write-Host "=== ClipBridge relay lifecycle stress ===" -ForegroundColor Cyan
Write-Host "artifact=$Artifact  port=$Port  instance=$InstanceId"
Write-Host "Phase A=$Cycles kill/recover cycles, Phase B=$RelaunchCycles relaunches, Phase C=$ExhaustionRounds exhaustion+self-heal rounds"
Write-Host ''

# ------------------------------------------------------------------ Phase A
Write-Host "=== Phase A: $Cycles unexpected relay exits against ONE long-running app ===" -ForegroundColor Cyan
if (-not (Start-App)) { Fail 'A: launch+ready' 'app did not become Ready'; Stop-App; exit 1 }
Pass 'A: launch+ready' "relay pid=$(Get-ListenerPid) port=$Port"
$firstPid = Get-ListenerPid

for ($i = 1; $i -le $Cycles; $i++) {
    $old = Get-ListenerPid
    if ($old -eq 0) { Fail "A[$i]" 'no relay listener before kill'; continue }
    Stop-Process -Id $old -Force -ErrorAction SilentlyContinue
    $recovered = Wait-Until { $p = Get-ListenerPid; $p -ne 0 -and $p -ne $old } 40 200
    $healthy = $recovered -and (Test-Health)
    $active = $healthy -and (Wait-Until { $s = Get-Status; $s -and $s.Relay -eq 'Running' -and $s.Sync -eq 'Active' } 20 300)
    $new = Get-ListenerPid
    if ($recovered -and $healthy -and $active -and $new -ne $old) {
        Pass "A[$i]: kill $old -> recover $new"
    }
    else {
        Fail "A[$i]" "old=$old new=$new recovered=$recovered health=$healthy active=$active"
    }
}
$lastAPid = Get-ListenerPid
Stop-App
Assert-NoOrphan 'A:'
Pass 'A: clean exit (no orphan)' "port $Port released (last relay pid $lastAPid)"

# ------------------------------------------------------------------ Phase B
Write-Host ''
Write-Host "=== Phase B: $RelaunchCycles full relaunch cycles ===" -ForegroundColor Cyan
for ($i = 1; $i -le $RelaunchCycles; $i++) {
    if (-not (Start-App)) { Fail "B[$i]: launch" 'not Ready'; Stop-App; continue }
    $pidAtLaunch = Get-ListenerPid
    Stop-Process -Id $pidAtLaunch -Force -ErrorAction SilentlyContinue
    $recovered = Wait-Until { $p = Get-ListenerPid; $p -ne 0 -and $p -ne $pidAtLaunch } 40 200
    $active = $recovered -and (Test-Health) -and (Wait-Until { $s = Get-Status; $s -and $s.Relay -eq 'Running' -and $s.Sync -eq 'Active' } 20 300)
    if ($active) { Pass "B[$i]: relaunch + kill + recover" "$pidAtLaunch -> $(Get-ListenerPid)" }
    else { Fail "B[$i]" "recovered=$recovered active=$active" }
    Stop-App
}

# ------------------------------------------------------------------ Phase C
Write-Host ''
Write-Host "=== Phase C: exhaust the restart budget, then heal with NO user action ===" -ForegroundColor Cyan
if (-not (Start-App)) { Fail 'C: launch' 'app did not become Ready'; Stop-App; exit 1 }
for ($r = 1; $r -le $ExhaustionRounds; $r++) {
    $before = Log-Len
    # Kill repeatedly until the budget is exhausted. Stop as soon as the manager reports
    # exhaustion: waiting for a listener that will not come (the cool-down is running)
    # would consume the cool-down and make the Failed state unobservable.
    $logExhausted = $false
    for ($k = 1; $k -le 4 -and -not $logExhausted; $k++) {
        Wait-Until { (Get-ListenerPid) -ne 0 } 20 200 | Out-Null
        $p = Get-ListenerPid
        if ($p -ne 0) { Stop-Process -Id $p -Force -ErrorAction SilentlyContinue }
        $logExhausted = Wait-Until {
            @(Log-Since $before | Where-Object { $_ -match 'relay_restart_exhausted' }).Count -ge 1
        } 8 300
        if (-not $logExhausted) { Start-Sleep -Milliseconds 300 }
    }

    $statusFailed = Wait-Until { $s = Get-Status; $s -and $s.Relay -eq 'Failed' } 10 300
    if ($logExhausted -and $statusFailed) {
        Pass "C[$r]: budget exhausted (relay_restart_exhausted logged, status Relay=Failed)"
    }
    elseif ($logExhausted) {
        # The cool-down can elapse before a status round-trip lands; the logged exhaustion
        # is the authoritative signal and the self-heal check below still applies.
        Pass "C[$r]: budget exhausted (relay_restart_exhausted logged; recovery already under way)"
    }
    else {
        Fail "C[$r]: exhaustion" "logExhausted=$logExhausted statusFailed=$statusFailed"
    }

    # No action from here on: the manager must heal itself.
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $healed = Wait-Until {
        $s = Get-Status
        ($s -and $s.Relay -eq 'Running' -and $s.Sync -eq 'Active' -and -not $s.LastError) -and (Test-Health) -and (Get-ListenerPid) -ne 0
    } 120 500
    $sw.Stop()
    $lines = Log-Since $before
    if ($healed -and ($lines | Where-Object { $_ -match 'relay_recovery_attempt' }).Count -ge 1) {
        Pass "C[$r]: SELF-HEALED after $([math]::Round($sw.Elapsed.TotalSeconds,1))s (no user action)" "relay pid=$(Get-ListenerPid)"
    }
    else { Fail "C[$r]: self-heal" "healed=$healed after $([math]::Round($sw.Elapsed.TotalSeconds,1))s" }

    # stability reset: let it run past the 60 s threshold, then crash again -> must recover
    Step "C[$r]: running stably for 65s to exercise the budget reset..."
    Start-Sleep -Seconds 65
    if (-not (Test-Health)) { Fail "C[$r]: still healthy after stable window" '' }
    $stablePid = Get-ListenerPid
    Stop-Process -Id $stablePid -Force -ErrorAction SilentlyContinue
    $afterStable = Wait-Until {
        $p = Get-ListenerPid
        $p -ne 0 -and $p -ne $stablePid -and (Test-Health)
    } 40 250
    if ($afterStable) { Pass "C[$r]: crash AFTER a stable run still recovers" "$stablePid -> $(Get-ListenerPid)" }
    else { Fail "C[$r]: post-stability crash" 'did not recover' }
}
Stop-App
Assert-NoOrphan 'C:'
Pass 'C: clean exit (no orphan)' "port $Port released"

# ------------------------------------------------------------------ restore + report
$cfgHashAfter = (Get-FileHash $cfgPath -Algorithm SHA256).Hash
if ($cfgHashAfter -ne $cfgHashBefore) {
    Fail 'SAFETY: user config.json was modified' "before=$($cfgHashBefore.Substring(0,12)) after=$($cfgHashAfter.Substring(0,12))"
}
foreach ($f in $dbFiles) {
    $p = Join-Path $dataDir $f
    if ($dbBackups.ContainsKey($f)) { Copy-Item $dbBackups[$f] $p -Force }
    elseif (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
}
Remove-Item Env:CLIPBRIDGE_INSTANCE_ID, Env:CLIPBRIDGE_RELAY_PORT, Env:CLIPBRIDGE_RELAY_URL, Env:CLIPBRIDGE_DEVICE_TOKEN, Env:CLIPBRIDGE_POLL_INTERVAL_MS, Env:CLIPBRIDGE_LAN_HOSTNAME -ErrorAction SilentlyContinue

$ok = @($script:Results | Where-Object { $_.Ok }).Count
$total = $script:Results.Count
$cycleTotal = $Cycles + $RelaunchCycles + (2 * $ExhaustionRounds)
Write-Host ''
Write-Host '=== LIFECYCLE STRESS RESULTS ===' -ForegroundColor Cyan
Write-Host ("Lifecycle cycles:      {0}" -f $cycleTotal)
Write-Host ("Checks passed:         {0}/{1}" -f $ok, $total)
Write-Host ("Checks failed:         {0}" -f ($total - $ok))
if ($script:Failures.Count -gt 0) {
    Write-Host 'FAILURES:' -ForegroundColor Red
    $script:Failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
}
Write-Host ''
if ($total -gt 0 -and $ok -eq $total) {
    Write-Host 'LIFECYCLE STRESS: ALL CHECKS PASSED (100% recovery)' -ForegroundColor Green
    exit 0
}
Write-Host 'LIFECYCLE STRESS: FAILURES PRESENT' -ForegroundColor Red
exit 1
