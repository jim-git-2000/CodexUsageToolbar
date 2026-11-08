# Changelog

## v1.4.0 - 2026-11-08

- Added a clickable reset-credit summary that opens all reported expiry times in chronological order, while keeping the nearest expiry visible by default.
- Kept the floating toolbar expanded while the reset-credit list is open.
- Adapted usage aggregation to CC Switch v4.0.26 input-token semantics, daily rollups, and proxy/session deduplication.
- Queried live quota and reset credits together through the Codex app-server, with a one-minute cache and session-quota fallback.
- Clarified that the Ubuntu exporter runs on demand over SSH and needs no startup service.

## v1.3.0 - 2026-07-22

- Added a persistent tray switch for showing or hiding the 5-hour quota panel.
- Added earned Codex rate-limit reset count and nearest-expiry display through the local Codex app-server, with hourly refresh and stale-cache fallback.
- Updated the exporter contract to schema version 1.1 while retaining client compatibility with 1.0 caches.

## v1.2.0 - 2026-07-01

- Added edge auto-hide for all screen edges.
- Added accent-colored edge strips with hover-to-expand behavior.
- Improved edge docking when the toolbar is dragged partially off-screen.
- Fixed edge-hover flicker and prevented button clicks from immediately hiding the expanded toolbar.

## v1.1.0 - 2026-06-26

- Added custom application/tray icon support.
- Added font size controls in the tray menu.
- Improved floating window behavior for task switching and virtual desktop changes.
- Improved release packaging with versioned zip names.

## v1.0.0 - 2026-06-26

- Added Windows floating toolbar with tray controls.
- Added SSH-based Ubuntu exporter contract for Codex usage.
- Added 5h and weekly quota display from Codex session rate limit events.
- Added token/cost windows for Today, 1d, 7d, 14d, and 30d.
- Added dark/light background mode, ring/bar quota layout, accent colors, click-through, startup toggle, resize support, and last-good cache.
- Added application and tray icon.
