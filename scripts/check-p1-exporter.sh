#!/usr/bin/env bash
set -euo pipefail

EXPORTER="${1:-$HOME/.local/bin/ccswitch-export}"
COMMAND=("$EXPORTER" codex --windows today,1d,7d,14d,30d --json)

echo "user=$(whoami)"
echo "host=$(hostname)"
echo "home=$HOME"
echo

echo "== cc-switch / exporter candidates =="
command -v cc-switch || true
command -v ccswitch || true
command -v ccswitch-export || true
find "$HOME/.local/bin" "$HOME/bin" /usr/local/bin /usr/bin \
  -maxdepth 2 \( -type f -o -type l \) \
  \( -name '*ccswitch*' -o -name '*cc-switch*' \) \
  2>/dev/null | sort || true
echo

if [[ ! -x "$EXPORTER" ]]; then
  echo "ERROR: exporter is not executable: $EXPORTER" >&2
  echo "Pass the actual exporter path as the first argument if it exists elsewhere." >&2
  echo "If only /usr/bin/cc-switch exists, inspect the probe output above and create ~/.local/bin/ccswitch-export as the stable JSON adapter." >&2
  exit 2
fi

echo "== exporter version / help probe =="
"$EXPORTER" --version 2>/dev/null || true
"$EXPORTER" --help 2>/dev/null | sed -n '1,40p' || true
echo

tmp_json="$(mktemp)"
trap 'rm -f "$tmp_json"' EXIT

echo "== running JSON contract command =="
printf '%q ' "${COMMAND[@]}"
echo
"${COMMAND[@]}" >"$tmp_json"

if [[ ! -s "$tmp_json" ]]; then
  echo "ERROR: exporter returned empty output" >&2
  exit 3
fi

python3 - "$tmp_json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
raw = path.read_text(encoding="utf-8")

try:
    data = json.loads(raw)
except Exception as exc:
    print(raw)
    raise SystemExit(f"ERROR: output is not valid JSON: {exc}")

def require(obj, key, label):
    if key not in obj:
        raise SystemExit(f"ERROR: missing required field: {label}.{key}")
    return obj[key]

require(data, "schema_version", "<root>")
require(data, "app", "<root>")
require(data, "source", "<root>")
require(data, "collected_at", "<root>")
quota = require(data, "quota", "<root>")
tokens = require(data, "tokens", "<root>")
refresh = require(data, "refresh", "<root>")
require(data, "errors", "<root>")

if data["schema_version"] != "1.0":
    raise SystemExit(f"ERROR: expected schema_version=1.0, got {data['schema_version']!r}")
if data["app"] != "codex":
    raise SystemExit(f"ERROR: expected app=codex, got {data['app']!r}")

for quota_name in ("five_hour", "weekly"):
    item = require(quota, quota_name, "quota")
    for key in ("remaining_percent", "reset_at", "available"):
        require(item, key, f"quota.{quota_name}")

for window in ("today", "1d", "7d", "14d", "30d"):
    item = require(tokens, window, "tokens")
    for key in ("total_tokens", "hit_tokens", "hit_rate", "requests", "total_cost_usd"):
        require(item, key, f"tokens.{window}")

for key in ("last_success_at", "cc_switch_last_event_at", "stale"):
    require(refresh, key, "refresh")

print("P1 exporter contract OK")
print(f"schema_version: {data['schema_version']}")
print(f"app: {data['app']}")
print(f"collected_at: {data['collected_at']}")
print("windows: today, 1d, 7d, 14d, 30d")
PY
