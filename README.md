# Opulent Hardware Module Runtime

Opulent Hardware Module Runtime is a Windows tray application that supplies local audio and media capabilities to compatible Opulent Hardware widgets. Version 1.0.1 powers the BIO-GRID Audio Module while keeping the capability layer independent from any particular visual skin.

## Features

- Master and per-application Windows volume control
- Playback-device and default-microphone switching
- Microphone gain, mute, input level, and capture-activity reporting
- Now-playing metadata, artwork, timeline progress, transport controls, and supported seeking
- Capability-based handshake for future compatible widgets and skins
- Single-instance tray application with **Start with Windows** enabled on first launch
- Loopback-only HTTP service; no account or cloud connection required

## Privacy and security

The Runtime binds only to `127.0.0.1:17652`. It does not listen on the LAN, record audio, upload media, require an account, or transmit personal data off the PC. Browser-origin checks restrict API access to local widget and development contexts.

## Requirements

- Windows 10 version 2004 or newer
- x64 processor
- .NET 8 SDK only when building from source; release builds are self-contained

## Installation

1. Download `Opulent-Hardware-Module-Runtime-1.0.1.exe` from the release.
2. Run it once. The app starts in the Windows notification area.
3. Startup with Windows is enabled automatically on first launch.
4. Right-click the tray icon to open status, disable or re-enable startup, or exit.

The startup preference is opt-out: after a user disables it, later launches preserve that choice.

## Local API

- Base URL: `http://127.0.0.1:17652/api/v1`
- Health: `GET /health`
- Runtime descriptor: `GET /runtime`
- Capabilities: `GET /capabilities`
- Diagnostics: `GET /diagnostics`

The protocol version remains `1.0` and is intentionally separate from the application release version.

## Release policy

Application releases use semantic versioning. The stable widget-facing contract is capability-based so newer widgets can detect missing features and degrade gracefully.

## License

Copyright © 2026 Opulent Hardware. All rights reserved. See [LICENSE](LICENSE).

