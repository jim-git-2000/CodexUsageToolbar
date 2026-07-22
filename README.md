# Codex Usage Toolbar

A lightweight Windows floating toolbar that displays Codex quota, token usage, cache hits, request counts, and estimated cost. A small Ubuntu exporter reads cc-switch data and Codex session rate-limit events; the Windows client fetches its sanitized JSON over SSH.

Current release: `v1.2.0`.

## Features

- Optional 5-hour quota and always-visible weekly quota, including reset times.
- Earned Codex rate-limit reset count and the nearest available-credit expiry time when reported by Codex.
- Today, 1-day, 7-day, 14-day, and 30-day usage windows.
- Compact and expanded views, ring or bar quota layouts, dark or light backgrounds, accent colors, and adjustable font size.
- Tray controls, click-through, always-on-top, start-with-Windows, resizing, and edge auto-hide.
- Last-known-good cache: an SSH or exporter failure marks the display stale instead of replacing usage with zero.

## Requirements

- Windows 10 or 11 with Windows OpenSSH Client.
- Ubuntu with Python 3, cc-switch data at `~/.cc-switch/cc-switch.db`, and readable Codex sessions under `~/.codex/sessions/`.
- SSH key or agent authentication that works in batch mode. The application does not store passwords or API credentials.

## Setup

On Ubuntu, clone or copy this repository and install the exporter:

```bash
bash scripts/install-ubuntu-exporter.sh
bash scripts/check-p1-exporter.sh
```

The installer writes `~/.local/bin/ccswitch-export`. Re-run it after changing `scripts/ccswitch-export`.

On Windows, copy `settings.example.json` to `settings.json` next to `CodexUsageToolbar.exe`, then set `sshHost`:

```json
{
  "sshHost": "user@192.168.x.x"
}
```

You can use an SSH alias such as `codex-vm` instead. Keep private host names, key paths, and other machine-specific values out of commits.

Run the toolbar:

```powershell
.\CodexUsageToolbar.exe
```

The application reads `settings.json` from the current directory first, then from the executable directory. Tray and window changes are saved back to the selected file. Use the tray menu's `Show 5h quota` checkbox to show or hide the 5-hour panel; the choice persists in `showFiveHourQuota`. See `settings.example.json` for every supported setting.

## Validate the Data Path

Validate the exporter locally on Ubuntu:

```bash
bash scripts/check-p1-exporter.sh
```

Validate the same JSON contract from Windows:

```powershell
.\scripts\Validate-P1Exporter.ps1 -SshHost user@192.168.x.x
```

Validate the complete client path once without opening the toolbar:

```powershell
.\CodexUsageToolbar.exe --once --ssh-host user@192.168.x.x
```

For parser-only diagnostics, pass a saved exporter response:

```powershell
.\CodexUsageToolbar.exe --once --json-file .\sample.json
```

Run `.\CodexUsageToolbar.exe --help` for all CLI options. `CODEX_USAGE_SSH_HOST`, `CODEX_USAGE_REMOTE_COMMAND`, and `CODEX_USAGE_IDENTITY_FILE` provide environment-variable overrides.

## Data and Security Boundary

The Windows application only invokes `ssh.exe`, parses exporter JSON, and renders it. It does not read Codex credentials, parse raw Codex session logs, copy the cc-switch database to Windows, or recalculate token and cost semantics.

The Ubuntu exporter:

- aggregates Codex-only token and estimated-cost fields from cc-switch's `proxy_request_logs`;
- reads only non-secret `rate_limits` fields from Codex session JSONL files;
- asks the local Codex app-server for earned reset-credit count and expiry metadata without reading or exporting `auth.json`;
- returns schema version `1.1` for `today`, `1d`, `7d`, `14d`, and `30d`;
- opens the cc-switch database read-only.

Quota or reset-credit data can be unavailable while token data remains valid. The exporter reports those states explicitly instead of inventing values. Reset-credit lookup requires a signed-in Codex CLI version that supports `account/rateLimits/read`; only the count, expiry, and refresh timestamps cross SSH.

Reset-credit metadata is queried immediately when no cached content exists, then at most once per hour while cached content is available. The exporter keeps the sanitized runtime cache at `~/.cache/codex-usage-toolbar/reset-credits.json`. If an hourly refresh fails, the previous content remains available and is marked stale; the next backend attempt occurs one hour later. This single cache file can be deleted safely to force an immediate query.

## Development

The application targets `.NET 8` WinForms. The repository intentionally has no third-party runtime package dependencies and no test project.

Restore and build locally:

```bash
dotnet restore src/CodexUsageToolbar/CodexUsageToolbar.csproj --configfile NuGet.config
dotnet build src/CodexUsageToolbar/CodexUsageToolbar.csproj --configuration Release --no-restore
```

The local `NuGet.config` disables external package sources. GitHub Actions uses `NuGet.CI.config` to obtain the Windows runtime packs required for the self-contained `win-x64` release.

Also validate scripts when they change:

```bash
bash -n scripts/check-p1-exporter.sh scripts/install-ubuntu-exporter.sh
python3 -m py_compile scripts/ccswitch-export
```

## Release

`.github/workflows/build.yml` builds and smoke-tests a self-contained single-file `win-x64` zip. Pushes and pull requests create workflow artifacts; a new `v*` tag also creates a GitHub Release asset.

Before creating a new tag:

1. Update the version in `src/CodexUsageToolbar/CodexUsageToolbar.csproj`.
2. Add the same version and an absolute release date to `CHANGELOG.md`.
3. Build locally, then create and push a new tag such as `v1.3.0`.

Do not recreate or move the existing `v1.2.0` tag. See [CHANGELOG.md](CHANGELOG.md) for release history.
