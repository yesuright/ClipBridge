# Pre-commit secret scan: fails if any likely secret is found in the working tree.
# Checks:
#   1. PEM private key blocks
#   2. inline `Authorization: Bearer <long-token>` literals
#   3. secret-like assignments (token/password/apiKey) whose value is a long
#      hex or base64 blob AND is not obviously code/a placeholder
# Usage: powershell -ExecutionPolicy Bypass -File scripts/secret-scan.ps1
param([string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path)

$ErrorActionPreference = 'Stop'
$hits = [System.Collections.Generic.List[string]]::new()

function Scan-File($path) {
    $text = Get-Content $path -Raw -ErrorAction SilentlyContinue
    if (-not $text) { return }

    $lines = $text -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $trimmed = $line.Trim()
        if ($trimmed -eq '') { continue }

        # 1. PEM private keys
        if ($line -match 'BEGIN (RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY') {
            $hits.Add("$path`:$($i+1): PEM private key block")
        }

        # 2. inline long bearer token (never in code)
        if ($trimmed -match 'Bearer\s+[A-Za-z0-9+/=_-]{40,}') {
            $hits.Add("$path`:$($i+1): inline Bearer token")
        }

        # 3. secret-like assignments
        $keyMatch = [regex]::Match(
            $trimmed,
            '^(CLIPBRIDGE_TOKEN|CLIPBRIDGE_DEVICE_TOKEN|DEVICE_TOKEN|deviceToken|DeviceToken|token|password|passwd|secret|api[_-]?key|apikey)\s*[=:]\s*(.+)$')
        if ($keyMatch.Success) {
            $value = $keyMatch.Groups[2].Value.Trim(' ', '"', "'", ';', ',', '`')
            # skip anything that looks like code, config access, or a placeholder
            if ($value -match 'env\(|config\.|\?\?|\$env|Get-Content|\[|\{|\(|=>|""|''|\.env') { continue }
            if ($value -match '^(change-me|your-|test-|<|\.\.\.|empty|none|null|default|"")' -or $value -eq '') { continue }
            # flag only real secret material: long hex / long base64-ish blobs
            if ($value -match '^[0-9a-fA-F]{32,}$' -or $value -match '^[A-Za-z0-9+/=_-]{40,}$') {
                $hits.Add("$path`:$($i+1): secret-like value for $($keyMatch.Groups[1].Value)")
            }
        }
    }
}

Get-ChildItem -Path $RepoRoot -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '\\\.git\\|\\\.venv\\|\\bin\\|\\obj\\|\\node_modules\\|\bTestResults\b' -and
        $_.Extension -in '.ps1', '.py', '.cs', '.md', '.json', '.txt', '.yaml', '.yml', '.example' -or
        $_.Name -eq '.env'
    } |
    ForEach-Object { Scan-File $_.FullName }

if ($hits.Count -eq 0) {
    Write-Host 'Secret scan: CLEAN — no obvious credentials found.' -ForegroundColor Green
    exit 0
}
Write-Host 'Secret scan: potential secrets found:' -ForegroundColor Red
$hits | ForEach-Object { Write-Host "  $_" }
exit 1
