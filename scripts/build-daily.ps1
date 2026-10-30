# Build the daily-use ClipBridge artifact (Milestone 4).
#
# Produces:  artifacts\ClipBridge\
#   ClipBridge.exe            <- Windows tray client (self-contained .NET, no runtime needed)
#   ClipBridge.exe.config / dlls...
#   relay\ClipBridgeRelay.exe <- packaged relay (PyInstaller onedir, no Python needed)
#   relay\_internal\...
#   README.txt                <- quick start for a non-developer
#   DAILY_USE.md              <- full plain-language guide
#
# No installer, no admin, no firewall/router changes. Double-click ClipBridge.exe and it
# runs the whole system (relay + tray client) from one process tree.
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\build-daily.ps1
# Requires: .NET 10 SDK 10.0.402 or newer (global.json-pinned; candidate SDKs are probed
# for it, see the locate-tools step), relay venv with pyinstaller installed.

param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    # Default keeps the documented artifacts\ClipBridge location. Pass another folder to
    # build and test a second copy while the installed one is running (the running app
    # holds file locks inside artifacts\ClipBridge\relay and cannot be replaced in place).
    [string]$OutputDir = '',
    # Explicit SDK to publish with. Resolution order: this parameter, CLIPBRIDGE_DOTNET,
    # %USERPROFILE%\.dotnet\dotnet.exe, then dotnet on PATH.
    [string]$DotnetExe = ''
)

# self-contained publishing copies the runtime out of the SDK's runtime packs, so the
# SDK (not the machine's installed runtime) decides which .NET patch ships. An older
# SDK therefore produces an unpatched package without any warning. 10.0.13 is the
# .NET 10 servicing release bundled with the SDK pinned in global.json (10.0.402);
# keep this floor in sync with global.json.
$requiredRuntime = [version]'10.0.13'

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactDisk = Join-Path $root 'build\dist-daily'
$artifact = if ($OutputDir) { [IO.Path]::GetFullPath($OutputDir) } else { Join-Path $root 'artifacts\ClipBridge' }
# Never clean a computed path unless it resolves inside the canonical workspace.
if (-not [IO.Path]::GetFullPath($artifact).StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Artifact path is outside the ClipBridge workspace.'
}

Write-Host '=== build-daily: ClipBridge daily-use artifact ===' -ForegroundColor Cyan

# ------------------------------------------------------------------ locate tools
# Candidate dotnets are probed from the repo root, where the SDK resolver honours
# global.json: the first one that can actually supply the pinned .NET 10 SDK wins.
# A machine may carry several SDKs (e.g. a user-profile .NET 8 and a machine-wide
# .NET 10); probing — instead of trusting folder or PATH order — is what keeps the
# build reproducible, and the published runtime is read back from disk further below.
$dotnet = $null
$sdkVersion = $null
foreach ($candidate in @($DotnetExe, $env:CLIPBRIDGE_DOTNET, "$env:USERPROFILE\.dotnet\dotnet.exe", (Get-Command dotnet -ErrorAction SilentlyContinue).Source)) {
    if (-not $candidate -or -not (Test-Path $candidate)) { continue }
    if ($sdkVersion) { continue }
    # global.json is only honoured for commands run from inside the repository.
    Push-Location $root
    $version = try { (& $candidate --version 2>$null | Select-Object -First 1) } catch { $null }
    Pop-Location
    if ($version) { $dotnet = $candidate; $sdkVersion = $version }
}
if (-not $dotnet) {
    Write-Host 'No .NET SDK matching global.json (10.0.402 or any later 10.0.xxx) was found.' -ForegroundColor Red
    Write-Host 'Install SDK 10.0.402 or newer: dotnet-install.ps1 -Version 10.0.402 -InstallDir $env:USERPROFILE\.dotnet' -ForegroundColor Yellow
    Write-Host 'Or pass an explicit SDK: scripts\build-daily.ps1 -DotnetExe "C:\Program Files\dotnet\dotnet.exe"' -ForegroundColor Yellow
    exit 1
}
Write-Host "Toolchain: $dotnet (SDK $sdkVersion)" -ForegroundColor DarkGray
Write-Host "Runtime floor for this build: .NET $requiredRuntime" -ForegroundColor DarkGray

$relayDir = Join-Path $root 'services\relay'
$pyInstaller = Join-Path $relayDir '.venv\Scripts\pyinstaller.exe'
if (-not (Test-Path $pyInstaller)) {
    Write-Host "PyInstaller missing: $pyInstaller"
    Write-Host 'Install it into the relay venv:  '
    Write-Host "  cd $relayDir"
    Write-Host '  .venv\Scripts\pip install pyinstaller'
    exit 1
}

# ------------------------------------------------------------------ 1) Windows client (self-contained)
Write-Host ''
Write-Host '[1/3] Publishing Windows client (self-contained, no .NET runtime required)...' -ForegroundColor Cyan
# PathMap rewrites build-machine paths (PDB source paths + the RSDS PDB reference inside
# each PE) to a neutral /clipbridge prefix, so a distributed artifact never embeds the
# maintainer's Windows account name or personal folder layout.
& $dotnet publish (Join-Path $root 'apps\windows\src\ClipBridge.Win\ClipBridge.Win.csproj') `
    -c $Configuration -r $Runtime --self-contained true -o $artifactDisk --nologo -v q `
    -p:PathMap="$root=/clipbridge"
if ($LASTEXITCODE -ne 0) { Write-Host 'client publish failed'; exit $LASTEXITCODE }

# ------------------------------------------- 1b) refuse to ship a stale .NET runtime
# The published package must be self-contained AND carry a fully patched runtime. Both
# facts are read back from what was actually written to disk (runtimeconfig.json is
# generated by the SDK from the runtime pack it used, and hostpolicy.dll ships in the
# package), so a wrong or outdated toolchain cannot slip through unnoticed.
$runtimeConfig = Join-Path $artifactDisk 'ClipBridge.runtimeconfig.json'
if (-not (Test-Path $runtimeConfig)) { Write-Host "publish output has no runtimeconfig.json: $runtimeConfig"; exit 1 }
$runtimeOptions = (Get-Content $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions
$frameworks = @($runtimeOptions.includedFrameworks)
if ($frameworks.Count -eq 0) { Write-Host 'publish output is not self-contained (no includedFrameworks)'; exit 1 }
$runtimeOk = $true
foreach ($fw in $frameworks) {
    $version = $null
    [void][version]::TryParse([string]$fw.version, [ref]$version)
    $label = '{0} {1}' -f $fw.name, $fw.version
    if (-not $version -or $version -lt $requiredRuntime) {
        Write-Host "  FAIL  $label  (needs >= $requiredRuntime)" -ForegroundColor Red
        $runtimeOk = $false
    } else {
        Write-Host "  ok    $label" -ForegroundColor DarkGray
    }
}
$hostpolicyVersion = (Get-Item (Join-Path $artifactDisk 'hostpolicy.dll')).VersionInfo.ProductVersion
$hostpolicyToken = (($hostpolicyVersion -split ' ')[0] -replace ',', '.')
$hostpolicyParsed = $null
if (-not [version]::TryParse($hostpolicyToken, [ref]$hostpolicyParsed) -or $hostpolicyParsed -lt $requiredRuntime) {
    Write-Host "  FAIL  hostpolicy.dll $hostpolicyVersion (needs >= $requiredRuntime)" -ForegroundColor Red
    $runtimeOk = $false
} else {
    Write-Host "  ok    hostpolicy.dll $hostpolicyVersion" -ForegroundColor DarkGray
}
if (-not $runtimeOk) {
    Write-Host ''
    Write-Host "This build would ship a .NET runtime older than $requiredRuntime." -ForegroundColor Red
    Write-Host "Publish with .NET SDK 10.0.402 or newer (see global.json), e.g.:" -ForegroundColor Yellow
    Write-Host '  dotnet-install.ps1 -Version 10.0.402 -InstallDir $env:USERPROFILE\.dotnet' -ForegroundColor Yellow
    Write-Host '  scripts\build-daily.ps1 -DotnetExe "C:\Program Files\dotnet\dotnet.exe"' -ForegroundColor Yellow
    exit 1
}

# ------------------------------------------------------------------ 2) packaged relay (PyInstaller onedir)
Write-Host ''
Write-Host '[2/3] Building packaged relay (PyInstaller onedir, no Python required)...' -ForegroundColor Cyan
$buildDir = Join-Path $root 'build'
& $pyInstaller --noconfirm --onedir --name ClipBridgeRelay `
    (Join-Path $relayDir 'run.py') `
    --distpath (Join-Path $buildDir 'dist-pyinstaller') `
    --workpath (Join-Path $buildDir 'work-pyinstaller') `
    --specpath (Join-Path $buildDir 'spec-pyinstaller') `
    --paths $relayDir `
    --hidden-import uvicorn.loops.auto `
    --hidden-import uvicorn.loops.asyncio `
    --hidden-import uvicorn.protocols.http.auto `
    --hidden-import uvicorn.protocols.http.h11_impl `
    --hidden-import uvicorn.protocols.websockets.auto `
    --hidden-import uvicorn.lifespan.on `
    --hidden-import uvicorn.logging `
    --collect-submodules uvicorn `
    --exclude-module pytest `
    --exclude-module _pytest
if ($LASTEXITCODE -ne 0) { Write-Host 'relay packaging failed'; exit $LASTEXITCODE }

# the relay ships third-party code: the notices file and the packaged exe must agree
# (every shipped component declared, every declared pin matching the build venv).
$venvPython = Join-Path $relayDir '.venv\Scripts\python.exe'
& $venvPython (Join-Path $PSScriptRoot 'check-relay-notices.py') `
    --exe (Join-Path $buildDir 'dist-pyinstaller\ClipBridgeRelay\ClipBridgeRelay.exe') `
    --notices (Join-Path $root 'THIRD-PARTY-NOTICES.txt')
if ($LASTEXITCODE -ne 0) { Write-Host 'third-party notices cross-check failed'; exit $LASTEXITCODE }

# ------------------------------------------------------------------ 3) assemble artifact
Write-Host ''
Write-Host '[3/3] Assembling artifacts\ClipBridge\...' -ForegroundColor Cyan
if (Test-Path $artifact) { Remove-Item -LiteralPath $artifact -Recurse -Force }
New-Item -ItemType Directory -Force -Path $artifact | Out-Null

# client (published)
Get-ChildItem -Path $artifactDisk -Force | Copy-Item -Destination $artifact -Recurse -Force

# end users get no debug symbols: PDBs are build artifacts, they are not needed to run,
# and removing them keeps the privacy guarantee even if PathMap is ever bypassed
Get-ChildItem -Path $artifact -Recurse -Filter '*.pdb' -File | Remove-Item -Force

# license files: the repo LICENSE, the project's third-party notices (tracked at the
# repo root and cross-checked against the packaged relay above), and the third-party
# notices of the EXACT embedded .NET runtime, read from the runtime pack the SDK
# restored (version taken from the published runtimeconfig.json) — not from whatever
# runtime happens to be installed on the machine. License completeness is a release
# requirement: a missing file fails the build instead of shipping silently.
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $artifact 'LICENSE') -Force
$projectNotices = Join-Path $root 'THIRD-PARTY-NOTICES.txt'
if (-not (Test-Path $projectNotices)) { Write-Host 'THIRD-PARTY-NOTICES.txt is missing at the repo root'; exit 1 }
Copy-Item $projectNotices (Join-Path $artifact 'THIRD-PARTY-NOTICES.txt') -Force

$netcoreVersion = ($frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' } | Select-Object -First 1).version
if (-not $netcoreVersion) { Write-Host 'could not read the embedded Microsoft.NETCore.App version from runtimeconfig.json'; exit 1 }
$packageRoots = @($env:NUGET_PACKAGES, "$env:USERPROFILE\.nuget\packages") | Where-Object { $_ } | Select-Object -Unique
$packNotices = $null
foreach ($packageRoot in $packageRoots) {
    $candidatePath = Join-Path $packageRoot "microsoft.netcore.app.runtime.win-x64\$netcoreVersion\THIRD-PARTY-NOTICES.TXT"
    if (Test-Path $candidatePath) { $packNotices = $candidatePath; break }
}
if (-not $packNotices) {
    Write-Host "FAIL: third-party notices for the embedded .NET runtime $netcoreVersion were not found." -ForegroundColor Red
    Write-Host 'Searched the runtime pack in these package roots:' -ForegroundColor Red
    foreach ($packageRoot in $packageRoots) { Write-Host "  $packageRoot" -ForegroundColor Red }
    Write-Host 'Restore the matching runtime pack once (e.g. dotnet restore on this solution) or set NUGET_PACKAGES to the cache that has it.' -ForegroundColor Yellow
    exit 1
}
Copy-Item $packNotices (Join-Path $artifact 'THIRD-PARTY-NOTICES-dotnet-runtime.txt') -Force

# build manifest: generated from the live environment so the shipped file always
# describes THIS build (v1.0.0 shipped the same file inside its ZIP).
$buildCommit = try { (git -C $root rev-parse HEAD) } catch { 'unknown' }
$buildDateUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm')
$pythonVersion = (& $venvPython --version).TrimStart('Python ')
$pyInstallerVersion = (& $venvPython -m PyInstaller --version | Select-Object -Last 1).Trim()
$versionsList = ($frameworks | ForEach-Object { "  $($_.name) $($_.version)" }) -join "`r`n"
$freeze = (& $venvPython -m pip freeze --disable-pip-version-check) -join "`r`n"
@"
ClipBridge NET10-EVAL build manifest (candidate, NOT a release)
build commit: $buildCommit (branch feat/dotnet10-migration, local-only)
build date (UTC): $buildDateUtc

toolchain:
  .NET SDK $sdkVersion (win-x64)
  Python $pythonVersion
  PyInstaller $pyInstallerVersion

components:
  client: 1.0.0 (assembly 1.0.0.0), self-contained, net10.0-windows
  relay: 1.0.0 (PyInstaller onedir, no Python required; sources unchanged from v1.0.0)

embedded runtime:
$versionsList

runtime components (build venv dist-info, the environment that produced this build):
$freeze
"@ | Set-Content -Path (Join-Path $artifact 'BUILD-MANIFEST.txt') -Encoding UTF8

# relay (packaged) → artifact\relay\
$relayDist = Join-Path $buildDir 'dist-pyinstaller\ClipBridgeRelay'
New-Item -ItemType Directory -Force -Path (Join-Path $artifact 'relay') | Out-Null
Get-ChildItem -Path $relayDist -Force | Copy-Item -Destination (Join-Path $artifact 'relay') -Recurse -Force

# docs inside the artifact (best effort — the artifact works without them)
if (Test-Path (Join-Path $root 'docs\DAILY_USE.md')) {
    Copy-Item (Join-Path $root 'docs\DAILY_USE.md') (Join-Path $artifact 'DAILY_USE.md') -Force
}
@"
ClipBridge - daily use
======================

Start:   double-click ClipBridge.exe (nothing else to run or install)
Setup:   on first start ClipBridge creates its own access token and shows a
         "Connect your iPhone" window with the address + token for both Shortcuts.
         Open it again any time: tray icon -> Connection setup...
Open:    click the tray icon (Home / History / Settings)
Hide:    close the window; sync keeps running in the tray
Stop:    right-click the tray icon -> Exit ClipBridge
Logs:    %LOCALAPPDATA%\ClipBridge\logs\
Data:    %LOCALAPPDATA%\ClipBridge\clipbridge.db
Config:  %APPDATA%\ClipBridge\config.json
Doctor:  ClipBridge.exe --doctor      (paths, config state, relay, LAN URL, autostart)
LAN URL: Home -> iPhone connection -> Copy address
         Use the current PC LAN IP, e.g. http://192.168.1.14:8787
Theme:   Settings -> Appearance -> System / Light / Dark

Portable/test: set CLIPBRIDGE_HOME to a folder to keep config + data there.

iPhone: see DAILY_USE.md next to this file. Never forward port 8787 on your router.
If Windows Firewall asks about ClipBridgeRelay.exe, allow it on private networks only.
"@ | Set-Content -Path (Join-Path $artifact 'README.txt') -Encoding UTF8

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "Artifact: $artifact" -ForegroundColor Green
Write-Host "  client:  $artifact\ClipBridge.exe"
Write-Host "  relay:   $artifact\relay\ClipBridgeRelay.exe"
Write-Host 'Runtime dependencies: NONE (self-contained). No Python, no .NET install, no admin.'
Write-Host ("Embedded .NET runtime: {0} (SDK {1})" -f (($frameworks | ForEach-Object { "$($_.name) $($_.version)" }) -join ', '), $sdkVersion)
