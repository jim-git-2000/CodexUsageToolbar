param([Parameter(Mandatory = $true)][string]$ExecutablePath)

$ErrorActionPreference = "Stop"
$exe = (Resolve-Path $ExecutablePath).Path
$temp = Join-Path ([IO.Path]::GetTempPath()) ("toolbar-credits-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $now = [DateTimeOffset]::UtcNow
    $expired = $now.AddDays(-1).ToString("o")
    $earlier = $now.AddDays(2).ToString("o")
    $later = $now.AddDays(3).ToString("o")
    $quota = @{ available = $false; remaining_percent = $null; reset_at = $null }
    $tokens = @{}
    foreach ($window in @("today", "1d", "7d", "14d", "30d")) {
        $tokens[$window] = @{ total_tokens = 0; hit_tokens = 0; hit_rate = 0; requests = 0; total_cost_usd = 0 }
    }
    $cases = @(
        @{ name = "mixed"; count = 3; dates = @($later, $expired, $earlier); stale = $false; expected = '^Reset credits 2 \(cached\), next expires ' },
        @{ name = "expired"; count = 1; dates = @($expired); stale = $true; expected = '^Reset credits 0 \(cached\)$' },
        @{ name = "zero"; count = 0; dates = @(); stale = $false; expected = '^Reset credits 0$' }
    )
    foreach ($case in $cases) {
        $fixture = @{
            schema_version = "1.1"; app = "codex"; collected_at = $now.ToString("o")
            quota = @{ five_hour = $quota; weekly = $quota }; tokens = $tokens
            reset_credits = @{
                available = $true; available_count = $case.count; expires_at = @($case.dates)
                next_expires_at = if ($case.dates.Count) { $case.dates[0] } else { $null }
                fetched_at = $now.ToString("o"); checked_at = $now.ToString("o"); stale = $case.stale
            }
            refresh = @{ last_success_at = $now.ToString("o"); cc_switch_last_event_at = $null; stale = $false }
        }
        $path = Join-Path $temp ($case.name + ".json")
        $fixture | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding utf8NoBOM
        $info = [Diagnostics.ProcessStartInfo]::new($exe)
        $info.UseShellExecute = $false
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        foreach ($arg in @("--once", "--json-file", $path)) { $info.ArgumentList.Add($arg) }
        $process = [Diagnostics.Process]::Start($info)
        try {
            $output = $process.StandardOutput.ReadToEnd()
            $errorText = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) { throw "$($case.name): $errorText" }
            $line = ($output -split '\r?\n' | Where-Object { $_ -like 'Reset credits *' })
            if ($line -notmatch $case.expected) { throw "$($case.name): unexpected output: $output" }
            if ($case.name -eq "mixed") {
                $local = [DateTimeOffset]::Parse($earlier).ToLocalTime()
                if (!$line.EndsWith($local.ToString('MM-dd HH:mm'))) { throw "Nearest future expiry was not selected: $line" }
            }
        } finally { $process.Dispose() }
    }
    Write-Host "Reset-credit expiry, stale-cache and zero-credit CLI checks passed."
} finally {
    Remove-Item $temp -Recurse -Force
}
