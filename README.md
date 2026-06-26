# Codex Usage Toolbar

A lightweight Windows floating toolbar for Codex usage, backed by a small Ubuntu exporter over SSH.

## Quick Start

1. On Ubuntu, install the exporter:

   ```bash
   bash scripts/install-ubuntu-exporter.sh
   ```

2. On Windows, edit `settings.json` next to `CodexUsageToolbar.exe`:

   ```json
   {
     "sshHost": "user@192.168.x.x"
   }
   ```

3. Run:

   ```powershell
   .\CodexUsageToolbar.exe
   ```

## Validate Data

Run once from PowerShell:

```powershell
.\CodexUsageToolbar.exe --once --ssh-host user@192.168.x.x
```

The toolbar reads quota from Codex session `rate_limits` events and token/cost data from the cc-switch database.

## Release Build

Windows binaries are built by GitHub Actions. Push tag `v1.1.0` to create the GitHub Release asset:

```bash
git tag v1.1.0
git push origin v1.1.0
```
