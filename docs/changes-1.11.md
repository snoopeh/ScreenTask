# ScreenTask 1.11

- Apply capture/audio/privacy settings live; reconnect video/audio decoders automatically and rebind network prefixes without recreating the listener.
- Detect active Windows HDR mode and display luminance metadata automatically; keep manual overrides under Advanced.
- Save preferences atomically under LocalAppData/ScreenTask, migrate older settings, and recover damaged XML from backup.
- Replace desktop Files with browser selection, share links, progress, throughput, ETA, cancellation and revocation.
- Stream requested file ranges through ScreenTask using bounded browser WebSocket windows and the native browser download manager. No file staging or whole-file buffering; sender page stays open.

Tests cover Chrome/Edge downloads, 64-bit ranges in a sparse 6 GiB browser-selected file, a hashed 128 MiB transfer, cancellation, concurrency, authentication, cross-origin rejection, live native HDR peer recovery, audio reconnection, file-only and port changes, settings migration/recovery, updater rollback, and existing connection/HDR/layout/native-file regressions.
