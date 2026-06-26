param(
    [string]$SshHost = "codex-vm",
    [string]$IdentityFile = "",
    [int]$Port = 22,
    [string]$RemoteCommand = "~/.local/bin/ccswitch-export codex --windows today,1d,7d,14d,30d --json",
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"

function Fail($Message) {
    throw $Message
}

function RequireField($Object, $Name, $Path) {
    if ($null -eq $Object) {
        Fail "Missing object: $Path"
    }
    if ($null -eq $Object.PSObject.Properties[$Name]) {
        Fail "Missing required field: $Path.$Name"
    }
}

function RequireTokenWindow($Tokens, $Window) {
    RequireField $Tokens $Window "tokens"
    $item = $Tokens.$Window
    RequireField $item "total_tokens" "tokens.$Window"
    RequireField $item "hit_tokens" "tokens.$Window"
    RequireField $item "hit_rate" "tokens.$Window"
    RequireField $item "requests" "tokens.$Window"
    RequireField $item "total_cost_usd" "tokens.$Window"
}

if ($null -eq (Get-Command "ssh.exe" -ErrorAction SilentlyContinue)) {
    Fail "ssh.exe was not found. Install or enable Windows OpenSSH Client."
}

$command = $RemoteCommand
if ($CheckOnly) {
    $command = "whoami; hostname; echo HOME=`$HOME; command -v ccswitch-export || true; ls -la ~/.local/bin 2>/dev/null || true"
}

$sshArgs = @(
    "-o", "BatchMode=yes",
    "-o", "ConnectTimeout=3",
    "-p", "$Port"
)

if ($IdentityFile -ne "") {
    $sshArgs += @("-i", $IdentityFile)
}

$sshArgs += @($SshHost, $command)

$output = & ssh.exe @sshArgs 2>&1
$exitCode = $LASTEXITCODE
$text = ($output | Out-String).Trim()

if ($exitCode -ne 0) {
    if ($text -match "Could not resolve hostname") {
        Fail "ssh.exe could not resolve host. Use -SshHost jiaming@192.168.32.123 or configure codex-vm in SSH config."
    }
    if ($text -match "ccswitch-export" -and ($text -match "No such file|not found")) {
        Fail "SSH works, but ccswitch-export was not found on Ubuntu. Run bash scripts/install-ubuntu-exporter.sh on Ubuntu, then retry."
    }
    Fail "ssh.exe failed. Exit code: $exitCode. Output: $text"
}

if ($CheckOnly) {
    Write-Host $text
    exit 0
}

if ([string]::IsNullOrWhiteSpace($text)) {
    Fail "Exporter returned empty output"
}

try {
    $data = $text | ConvertFrom-Json
}
catch {
    Write-Host $text
    Fail "Exporter output is not valid JSON: $($_.Exception.Message)"
}

RequireField $data "schema_version" "<root>"
RequireField $data "app" "<root>"
RequireField $data "source" "<root>"
RequireField $data "collected_at" "<root>"
RequireField $data "quota" "<root>"
RequireField $data "tokens" "<root>"
RequireField $data "refresh" "<root>"
RequireField $data "errors" "<root>"

if ($data.app -ne "codex") {
    Fail "Expected app=codex, got app=$($data.app)"
}

if ($data.schema_version -ne "1.0") {
    Fail "Expected schema_version=1.0, got schema_version=$($data.schema_version)"
}

RequireField $data.quota "five_hour" "quota"
RequireField $data.quota "weekly" "quota"
RequireField $data.quota.five_hour "remaining_percent" "quota.five_hour"
RequireField $data.quota.five_hour "reset_at" "quota.five_hour"
RequireField $data.quota.five_hour "available" "quota.five_hour"
RequireField $data.quota.weekly "remaining_percent" "quota.weekly"
RequireField $data.quota.weekly "reset_at" "quota.weekly"
RequireField $data.quota.weekly "available" "quota.weekly"

RequireTokenWindow $data.tokens "today"
RequireTokenWindow $data.tokens "1d"
RequireTokenWindow $data.tokens "7d"
RequireTokenWindow $data.tokens "14d"
RequireTokenWindow $data.tokens "30d"

RequireField $data.refresh "last_success_at" "refresh"
RequireField $data.refresh "cc_switch_last_event_at" "refresh"
RequireField $data.refresh "stale" "refresh"

Write-Host "P1 exporter contract OK"
Write-Host "schema_version: $($data.schema_version)"
Write-Host "app: $($data.app)"
Write-Host "collected_at: $($data.collected_at)"
Write-Host "windows: today, 1d, 7d, 14d, 30d"
Write-Host ""
Write-Host "quota:"
Write-Host "  5h available: $($data.quota.five_hour.available), remaining: $($data.quota.five_hour.remaining_percent), reset: $($data.quota.five_hour.reset_at)"
Write-Host "  weekly available: $($data.quota.weekly.available), remaining: $($data.quota.weekly.remaining_percent), reset: $($data.quota.weekly.reset_at)"
Write-Host ""
Write-Host "tokens:"
foreach ($window in @("today", "1d", "7d", "14d", "30d")) {
    $item = $data.tokens.$window
    $hitPercent = [Math]::Round(([double]$item.hit_rate) * 100, 2)
    Write-Host "  $window total=$($item.total_tokens) hit=$($item.hit_tokens) hit_rate=$hitPercent% requests=$($item.requests) cost_usd=$($item.total_cost_usd)"
}

if ($data.errors.Count -gt 0) {
    Write-Host ""
    Write-Host "errors:"
    foreach ($errorItem in $data.errors) {
        Write-Host "  $($errorItem.code): $($errorItem.message)"
    }
}
