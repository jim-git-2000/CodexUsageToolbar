# P1 Status - JSON Contract Verification

Date: 2026-06-26

## Goal

Verify that the Ubuntu exporter can return the stable Windows JSON contract:

```powershell
ssh codex-vm "~/.local/bin/ccswitch-export codex --windows today,1d,7d,14d,30d --json"
```

## Current Result

P1 is complete.

Current status:

- SSH to the VM works with `jiaming@jiaming-VM-Ubuntu` and `jiaming@192.168.32.123`.
- Ubuntu has `/usr/bin/cc-switch`.
- Ubuntu exporter adapter has been implemented at `scripts/ccswitch-export`.
- The exporter has been installed to `/home/jiaming/.local/bin/ccswitch-export`.
- Local Ubuntu validation passes with `bash scripts/check-p1-exporter.sh`.
- Windows SSH validation passes with `.\scripts\Validate-P1Exporter.ps1 -SshHost jiaming@192.168.32.123`.
- `today` window support has been added after the first P1 pass; rerun `bash scripts/install-ubuntu-exporter.sh` and validate again to update the installed exporter.

Previously observed command before installing the exporter:

```powershell
.\scripts\Validate-P1Exporter.ps1 -SshHost jiaming@192.168.32.123
```

Observed result:

```text
/home/jiaming/.local/bin/ccswitch-export: No such file or directory
```

Passing Windows result:

```text
P1 exporter contract OK
schema_version: 1.0
app: codex
collected_at: 2026-06-26T14:25:16+08:00
windows: today, 1d, 7d, 14d, 30d
```

Quota status: exporter now reads non-secret `rate_limits` fields from Codex session JSONL files under `~/.codex/sessions/**/*.jsonl`.

- 5h quota: `window_minutes == 300`
- weekly quota: `window_minutes == 10080`
- exported fields: `used_percent`, `remaining_percent`, `reset_at`, `available`

Token, cache hit, request count, and cost fields are still aggregated from cc-switch DB.

## Added Validation Script

Ubuntu-side validation:

```bash
bash scripts/check-p1-exporter.sh
```

If the exporter is not at `~/.local/bin/ccswitch-export`, pass the actual path:

```bash
bash scripts/check-p1-exporter.sh /actual/path/to/ccswitch-export
```

Install the repo exporter to the default path expected by Windows:

```bash
bash scripts/install-ubuntu-exporter.sh
```

Use this script on the Windows machine that has access to the Hyper-V Ubuntu VM:

```powershell
.\scripts\Validate-P1Exporter.ps1
```

Optional parameters:

```powershell
.\scripts\Validate-P1Exporter.ps1 `
  -SshHost codex-vm `
  -IdentityFile "$HOME\.ssh\codex_usage_vm_ed25519" `
  -Port 22 `
  -RemoteCommand "~/.local/bin/ccswitch-export codex --windows today,1d,7d,14d,30d --json" `
  -ConnectTimeoutSeconds 3 `
  -CommandTimeoutSeconds 5
```

The script checks:

- `schema_version == "1.0"`
- `app == "codex"`
- root fields: `source`, `collected_at`, `quota`, `tokens`, `refresh`, `errors`
- quota fields for `five_hour` and `weekly`
- token windows: `today`, `1d`, `7d`, `14d`, `30d`
- token fields: `total_tokens`, `hit_tokens`, `hit_rate`, `requests`, `total_cost_usd`
- refresh fields: `last_success_at`, `cc_switch_last_event_at`, `stale`

Diagnostic mode:

```powershell
.\scripts\Validate-P1Exporter.ps1 -SshHost jiaming@192.168.32.123 -CheckOnly
```

This only checks SSH, user/home, and possible cc-switch/exporter paths. It does not validate the JSON contract.

## To Complete P1

Detected Ubuntu prompt:

```text
jiaming@jiaming-VM-Ubuntu:~$
```

This means:

- SSH user: `jiaming`
- VM host name: `jiaming-VM-Ubuntu`

Either run directly with the VM address:

```powershell
.\scripts\Validate-P1Exporter.ps1 `
  -SshHost ubuntu@192.168.x.x `
  -IdentityFile "$HOME\.ssh\codex_usage_vm_ed25519"
```

Or add a Windows SSH config entry:

```sshconfig
Host codex-vm
  HostName jiaming-VM-Ubuntu
  User jiaming
  IdentityFile ~/.ssh/codex_usage_vm_ed25519
  IdentitiesOnly yes
```

Then run:

```powershell
.\scripts\Validate-P1Exporter.ps1
```

P1 is complete when the script prints:

```text
P1 exporter contract OK
```
