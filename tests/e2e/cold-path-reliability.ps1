# ClipBridge M4.1 — cold-path reliability stress.
#
# Targets the REAL user failure: "first Windows copy after fresh launch / idle /
# relay recovery is silently lost (iPhone Get keeps returning old content)".
#
# Scenarios (each one launches the real tray app against its own managed relay child):
#   Fresh        — no ClipBridge process -> launch -> wait for TRUE readiness
#                 (relay healthy AND status shows Relay=Running, Sync=Active)
#                 -> copy a UNIQUE payload -> verify GET /clips/latest?exclude_device_id=iphone
#                 -> graceful exit. Repeated N times.
#   Idle         — app stable -> idle IdleSeconds -> FIRST copy -> verify (repeated N times,
#                 cycling fresh launches every IdleSeconds + margin so each run starts cold).
#   RelayRestart — app running -> managed relay child killed -> RelayManager recovers
#                 (bounded restart) -> status ready again -> FIRST copy -> verify.
#
# RUN: powershell -ExecutionPolicy Bypass -File tests\e2e\cold-path-reliability.ps1
#      [-Cycles 50] [-Scenario All|Fresh|Idle|RelayRestart] [-IdleSeconds 25] [-Port 8899]
#
# The run uses an isolated instance id + an isolated relay PORT so it can coexist with a
# real user session; the user's relay (8787) and config are untouched. The client config
# and LOCALAPPDATA db are backed up and restored (the artifact's managed relay child pins
# the standard db path — see docs/LIFECYCLE.md).

param(
    [int]$Cycles = 50,
    [ValidateSet('All', 'Fresh', 'Idle', 'RelayRestart')]
    [string]$Scenario = 'All',
    [int]$IdleSeconds = 25,
    [int]$Port = 8899,
    [string]$Artifact = (Resolve-Path (Join-Path $PSScriptRoot '..\..\artifacts\ClipBridge\ClipBridge.exe')).Path
)

$ErrorActionPreference = 'Stop'
$InstanceId = 'cb-stress-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$script:Latencies = [System.Collections.Generic.List[double]]::new()
$script:CycleResults = [System.Collections.Generic.List[bool]]::new()
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Write-Step([string]$msg) { Write-Host "  $msg" -ForegroundColor DarkGray }

# Immediate, non-OLE clipboard writer: Win32 SetClipboardData with a real memory handle.
# (PowerShell's Set-Clipboard uses OLE delayed rendering — the data object lives in the
# PS process, and our app's read would wait on PS's message pump — a harness artifact
# that would turn every cycle into a false failure. A finished real-world copy has its
# data ready, which is exactly what this writer emulates.)
if (-not ('ClipBridgeStress.ClipboardNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace ClipBridgeStress {
    public static class ClipboardNative {
        [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr h);
        [DllImport("user32.dll")] public static extern bool EmptyClipboard();
        [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint format, IntPtr hMem);
        [DllImport("user32.dll")] public static extern bool CloseClipboard();
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr hMem);
        public const uint CF_UNICODETEXT = 13;
        public static void SetText(string text) {
            var chars = text.ToCharArray();
            var size = (uint)((chars.Length + 1) * 2);
            var hMem = GlobalAlloc(0x42 /*GMEM_MOVEABLE|GMEM_ZEROINIT*/, (UIntPtr)size);
            var ptr = GlobalLock(hMem);
            try {
                for (var i = 0; i < chars.Length; i++) Marshal.WriteInt16(ptr, i * 2, (short)chars[i]);
                Marshal.WriteInt16(ptr, chars.Length * 2, 0); // NUL terminator
            } finally { GlobalUnlock(hMem); }
            if (!OpenClipboard(IntPtr.Zero)) { GlobalFree(hMem); throw new InvalidOperationException("OpenClipboard failed: " + Marshal.GetLastWin32Error()); }
            try {
                EmptyClipboard();
                if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) { GlobalFree(hMem); throw new InvalidOperationException("SetClipboardData failed: " + Marshal.GetLastWin32Error()); }
            } finally { CloseClipboard(); }
        }
    }
}
'@
}
function Set-ClipboardImmediate([string]$text) {
    [ClipBridgeStress.ClipboardNative]::SetText($text)
}

function Sha256Hex([string]$text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
}

function Invoke-RelayGet($path) {
    $headers = @{ Authorization = "Bearer $script:Token" }
    try {
        return Invoke-RestMethod -Method GET -Uri "http://127.0.0.1:$Port$path" -Headers $headers -TimeoutSec 5
    } catch {
        return $null
    }
}

function Wait-Until([scriptblock]$condition, [int]$timeoutSec = 20, [int]$intervalMs = 250) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        try { if (& $condition) { return $true } } catch { }
        Start-Sleep -Milliseconds $intervalMs
    }
    return $false
}

function Get-AppStatus {
    # Snapshot via the single-instance control channel. GUI subsystem exe: `&` returns
    # immediately without waiting; use Start-Process -Wait to collect the reply.
    $guard = $env:CLIPBRIDGE_INSTANCE_ID
    $env:CLIPBRIDGE_INSTANCE_ID = $InstanceId
    try {
        $out = Join-Path $env:TEMP ('cb-status-' + [guid]::NewGuid().ToString('N') + '.txt')
        $p = Start-Process -FilePath $script:AppExe -ArgumentList '--status' -Wait -NoNewWindow `
            -RedirectStandardOutput $out -PassThru
        $text = if (Test-Path $out) { [System.IO.File]::ReadAllText($out).Trim() } else { '' }
        Remove-Item $out -Force -ErrorAction SilentlyContinue
        if ($text) { return ($text | ConvertFrom-Json) }
    } catch { }
    finally { $env:CLIPBRIDGE_INSTANCE_ID = $guard }
    return $null
}

function Get-RelayHealth {
    try {
        return (Invoke-RestMethod -Method GET -Uri "http://127.0.0.1:$Port/api/v1/health" -TimeoutSec 2 -ErrorAction SilentlyContinue).status -eq 'ok'
    } catch { return $false }
}

function Wait-Ready([int]$timeoutSec = 45) {
    # The app is "ready" when: the managed relay answers the health endpoint AND the
    # app process is alive AND the status snapshot reports Relay=Running + Sync=Active.
    # (The last check catches the first-poll window: Active requires a successful poll.)
    return (Wait-Until {
        ($script:AppProcess -and -not $script:AppProcess.HasExited) -and (Get-RelayHealth) -and
        ((Get-AppStatus).Sync -eq 'Active')
    } $timeoutSec 300)
}

function Wait-Ready([int]$timeoutSec = 40) {
    return (Wait-Until {
        $s = Get-AppStatus
        ($s -and $s.Relay -eq 'Running' -and $s.Sync -eq 'Active' -and -not $s.LastError)
    } $timeoutSec 300)
}

function Stop-App {
    # Cycle isolation: force-kill the app. The kill-on-close Job Object takes the relay
    # child with it. (The graceful path — --exit -> admin shutdown -> drain — is verified
    # by scripts\verify-daily.ps1; the stress focuses on the cold first-copy path.)
    if ($script:AppProcess -and -not $script:AppProcess.HasExited) {
        Stop-Process -Id $script:AppProcess.Id -Force -ErrorAction SilentlyContinue
        $script:AppProcess.WaitForExit(5000) | Out-Null
    }
    $script:AppProcess = $null
}

function Get-RelayListenerPid {
    try {
        $conn = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($conn) { return $conn.OwningProcess }
    } catch { }
    return $null
}

function Assert-NoRelayOrphan {
    # The parent's graceful exit drains the relay child (admin shutdown -> job close).
    # Give the child up to 10 s to release the port; anything still listening then is a
    # genuine orphan (fail the cycle — the product must never leave one behind).
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (Get-RelayListenerPid -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 300
    }
    if (Get-RelayListenerPid) {
        $script:Failures.Add("orphan relay still listening on port $Port after app exit")
        throw "orphan relay still listening on port $Port after app exit"
    }
}

function Run-Cycle([int]$index, [string]$scenario) {
    $payload = "CB-STRESS-$scenario-$([guid]::NewGuid().ToString('N'))-$index-" +
               ('x' * 1024) + "`nline2-中文😀-done"
    $payloadHash = Sha256Hex $payload
    $payloadBytes = [System.Text.Encoding]::UTF8.GetByteCount($payload)

    $env:CLIPBRIDGE_INSTANCE_ID = $InstanceId
    $env:CLIPBRIDGE_RELAY_URL = "http://127.0.0.1:$Port"
    $env:CLIPBRIDGE_RELAY_PORT = "$Port"
    try {
        # --- scenario-specific state -------------------------------------------
        if ($scenario -eq 'RelayRestart') {
            # kill the managed relay child: bounded restart (1s backoff) must recover
            $relayPid = Get-RelayListenerPid
            if (-not $relayPid) { throw "no relay listener on port $Port to kill" }
            Stop-Process -Id $relayPid -Force -ErrorAction Stop
            Write-Step "killed relay child pid=$relayPid — waiting for RelayManager recovery"
            if (-not (Wait-Ready 60)) { throw 'relay did not recover to Ready within 60 s' }
        }
        elseif ($scenario -eq 'Idle') {
            Start-Sleep -Seconds $IdleSeconds
        }

        # --- the FIRST genuine copy --------------------------------------------
        # Verify by content_hash + size (ASCII-safe): PowerShell 5.1's Invoke-RestMethod
        # decodes a charset-less application/json body as Windows-1252, so direct content
        # string comparison would mojibake the Chinese/emoji suffix of the payload. The
        # hash is charset-independent and proves the exact bytes reached the relay.
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        Set-ClipboardImmediate $payload
        $seen = Wait-Until {
            $latest = Invoke-RelayGet '/api/v1/clips/latest?exclude_device_id=iphone'
            $ok = $false
            if ($latest) {
                $ok = ($latest.content_hash -eq $payloadHash) -and ($latest.size_bytes -eq $payloadBytes) -and ($latest.device_id -eq 'windows-main')
            }
            $ok
        } 20 200
        $sw.Stop()

        if (-not $seen) {
            $latest = Invoke-RelayGet '/api/v1/clips/latest?exclude_device_id=iphone'
            $got = if ($latest) { "latest hash=$($latest.content_hash) size=$($latest.size_bytes)" } else { 'no latest' }
            throw "payload never became latest. Expected hash=$payloadHash size=$payloadBytes, $got"
        }
        $script:Latencies.Add($sw.Elapsed.TotalMilliseconds)
        $script:CycleResults.Add($true)
        Write-Step "[$scenario] cycle $index OK — commit in $([math]::Round($sw.Elapsed.TotalMilliseconds, 0)) ms"
    }
    finally {
        Remove-Item Env:CLIPBRIDGE_INSTANCE_ID -ErrorAction SilentlyContinue
        Remove-Item Env:CLIPBRIDGE_RELAY_URL -ErrorAction SilentlyContinue
        Remove-Item Env:CLIPBRIDGE_RELAY_PORT -ErrorAction SilentlyContinue
    }
}

# ------------------------------------------------------------------ preflight
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not (Test-Path $Artifact)) {
    Write-Host "Artifact not found: $Artifact" -ForegroundColor Red
    Write-Host 'Build it first: powershell -ExecutionPolicy Bypass -File scripts\build-daily.ps1'
    exit 1
}
$script:AppExe = $Artifact
$script:Token = 'cb-stress-' + [guid]::NewGuid().ToString('N')

# Isolated client configuration — through ENVIRONMENT VARIABLES ONLY.
# ClientConfig resolves env > file > defaults, so the app can be pointed at this run's
# port/token without ever writing %APPDATA%\ClipBridge\config.json. An earlier revision
# backed the file up in memory and restored it afterwards, which silently propagated a
# test config into the user's real config whenever a run aborted before its restore step
# (the restore then put back the very config the test had written one run earlier).
# The hash guard at the end fails the run if the file changes at all.
$cfgDir = Join-Path $env:APPDATA 'ClipBridge'
$cfgPath = Join-Path $cfgDir 'config.json'
if (-not (Test-Path $cfgPath)) {
    Write-Host "No %APPDATA%\ClipBridge\config.json — configure the device token first (docs\DAILY_USE.md)." -ForegroundColor Red
    exit 1
}
$cfgHashBefore = (Get-FileHash $cfgPath -Algorithm SHA256).Hash
$env:CLIPBRIDGE_INSTANCE_ID = $InstanceId
$env:CLIPBRIDGE_RELAY_PORT = "$Port"
$env:CLIPBRIDGE_RELAY_URL = "http://127.0.0.1:$Port"
$env:CLIPBRIDGE_DEVICE_TOKEN = $script:Token
$env:CLIPBRIDGE_POLL_INTERVAL_MS = '1000'

# artifact managed relay pins LOCALAPPDATA db — back it up so the user's history is untouched
$dataDir = Join-Path $env:LOCALAPPDATA 'ClipBridge'
$dbFiles = @('clipbridge.db', 'clipbridge.db-wal', 'clipbridge.db-shm')
$dbBackups = @{}
foreach ($f in $dbFiles) {
    $p = Join-Path $dataDir $f
    if (Test-Path $p) { $dbBackups[$f] = (Join-Path $env:TEMP ("cb-db-backup-$InstanceId-$f")); Copy-Item $p $dbBackups[$f] -Force }
}

# warn if the user's own instance might be running on the default port (stress uses its own)
$userRelayPid = $null
try { $userRelayPid = (Get-NetTCPConnection -State Listen -LocalPort 8787 -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess } catch { }
if ($userRelayPid) {
    Write-Host "NOTE: something is already listening on 8787 (pid $userRelayPid); the stress uses its own port $Port." -ForegroundColor Yellow
}

$scenarios = if ($Scenario -eq 'All') { @('Fresh', 'Idle', 'RelayRestart') } else { @($Scenario) }

# ------------------------------------------------------------------ main loop
foreach ($scn in $scenarios) {
    Write-Host "=== Scenario $scn — $Cycles cycles ===" -ForegroundColor Cyan
    for ($i = 1; $i -le $Cycles; $i++) {
        try {
            # fresh app per cycle: no ClipBridge process may exist for this instance
            Stop-App
            Start-Sleep -Milliseconds 400
            Assert-NoRelayOrphan

            $env:CLIPBRIDGE_INSTANCE_ID = $InstanceId
            $env:CLIPBRIDGE_RELAY_PORT = "$Port"
            $env:CLIPBRIDGE_RELAY_URL = "http://127.0.0.1:$Port"
            $script:AppProcess = Start-Process -FilePath $Artifact -PassThru -WindowStyle Hidden
            if (-not (Wait-Ready 45)) {
                throw 'app did not reach READY (Relay=Running + Sync=Active, no error) within 45 s'
            }
            Run-Cycle $i $scn
        }
        catch {
            $msg = $_.Exception.Message
            $script:Failures.Add("[$scn] cycle ${i}: $msg")
            $script:CycleResults.Add($false)
            Write-Host "[$scn] cycle ${i} FAILED: $msg" -ForegroundColor Red
        }
        finally {
            Stop-App
            Start-Sleep -Milliseconds 300
            Assert-NoRelayOrphan
            Remove-Item Env:CLIPBRIDGE_INSTANCE_ID -ErrorAction SilentlyContinue
            Remove-Item Env:CLIPBRIDGE_RELAY_PORT -ErrorAction SilentlyContinue
            Remove-Item Env:CLIPBRIDGE_RELAY_URL -ErrorAction SilentlyContinue
        }
    }
}

# ------------------------------------------------------------------ restore
Stop-App
$cfgHashAfter = (Get-FileHash $cfgPath -Algorithm SHA256).Hash
if ($cfgHashAfter -ne $cfgHashBefore) {
    Write-Host 'SAFETY: the user config.json changed during this run — investigate before trusting the result.' -ForegroundColor Red
    $script:Failures.Add('SAFETY: user config.json was modified')
}
foreach ($f in $dbFiles) {
    $p = Join-Path $dataDir $f
    if ($dbBackups.ContainsKey($f)) {
        Copy-Item $dbBackups[$f] $p -Force
    } elseif (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
}
Remove-Item Env:CLIPBRIDGE_INSTANCE_ID, Env:CLIPBRIDGE_RELAY_PORT, Env:CLIPBRIDGE_RELAY_URL, Env:CLIPBRIDGE_DEVICE_TOKEN, Env:CLIPBRIDGE_POLL_INTERVAL_MS -ErrorAction SilentlyContinue

# ------------------------------------------------------------------ report
$ok = @($script:CycleResults | Where-Object { $_ }).Count
$total = $script:CycleResults.Count
$lats = $script:Latencies | Sort-Object
$median = if ($lats.Count -gt 0) { $lats[[math]::Floor($lats.Count / 2)] } else { 0 }
$p95 = 0; $max = 0; $min = 0
if ($lats.Count -gt 0) {
    $max = $lats[-1]; $min = $lats[0]
    $idx95 = [math]::Ceiling(0.95 * $lats.Count) - 1
    $p95 = $lats[[math]::Max(0, $idx95)]
}

Write-Host ''
Write-Host '=== COLD-PATH RELIABILITY RESULTS ===' -ForegroundColor Cyan
Write-Host ("Cycles:    {0}" -f $total)
Write-Host ("Success:   {0}" -f $ok)
Write-Host ("Failure:   {0}" -f ($total - $ok))
if ($lats.Count -gt 0) {
    Write-Host ("Median commit latency:  {0} ms" -f [math]::Round($median, 1))
    Write-Host ("P95 commit latency:     {0} ms" -f [math]::Round($p95, 1))
    Write-Host ("Max commit latency:     {0} ms" -f [math]::Round($max, 1))
    Write-Host ("Min commit latency:     {0} ms" -f [math]::Round($min, 1))
}
if ($script:Failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'FAILURES:' -ForegroundColor Red
    $script:Failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
}
Write-Host ''
if ($total -gt 0 -and $ok -eq $total) {
    Write-Host 'COLD-PATH STRESS: ALL CYCLES PASSED (100%)' -ForegroundColor Green
    exit 0
}
Write-Host 'COLD-PATH STRESS: FAILURES PRESENT' -ForegroundColor Red
exit 1
