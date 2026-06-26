#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/scripts/ccswitch-export"
target_dir="$HOME/.local/bin"
target_file="$target_dir/ccswitch-export"

if [[ ! -f "$source_file" ]]; then
  echo "ERROR: source exporter not found: $source_file" >&2
  exit 1
fi

mkdir -p "$target_dir"
install -m 0755 "$source_file" "$target_file"

echo "Installed: $target_file"
echo "Validate with:"
echo "  $target_file codex --windows today,1d,7d,14d,30d --json"
