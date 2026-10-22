# Opulent Hardware Module Runtime

Opulent Hardware Module Runtime is the local Windows companion for compatible Opulent Hardware widgets, including BIO-GRID Audio Module. It provides Windows audio mixing, microphone control, playback-device switching, media artwork, timeline progress, transport controls, and supported seeking.

This is the official download and documentation repository. The Runtime is distributed as a compiled Windows application; its source code is not published here.

## Current release

**Runtime 1.0.2** improves album and browser-video artwork reliability during natural track changes, playlist autoplay, and delayed Windows media-session thumbnail updates.

BIO-GRID Audio Module 1.1.1 and Runtime 1.0.2 are the recommended pairing.

## Requirements

- Windows 10 version 2004 or newer
- Windows 11
- x64 processor
- A compatible Opulent Hardware widget

## Installation

1. Download `Opulent-Hardware-Module-Runtime-1.0.2.exe` from the latest GitHub Release.
2. Create a permanent folder such as `%LOCALAPPDATA%\Opulent Hardware\Module Runtime`.
3. Place the downloaded executable in that folder before running it.
4. Run the executable. The Runtime appears in the Windows notification area.
5. Right-click its notification-area icon to open status, control **Start with Windows**, or exit.
6. In iCUE, ensure **Settings → Widget Access → BIO-GRID Audio Module → localhost:17652** is allowed.

Do not leave the Runtime in Downloads or a temporary folder. Windows startup points to the exact executable location from which the Runtime was launched.

## Updating

1. Exit the currently running Runtime from its notification-area icon.
2. Download the new executable from GitHub Releases.
3. Replace the previous executable in the same permanent folder.
4. Run the new version once.
5. If Windows startup does not work after an update, uncheck and recheck **Start with Windows** from the Runtime tray menu.

## Removing the Runtime

1. Right-click the Runtime's notification-area icon.
2. Uncheck **Start with Windows**.
3. Select **Exit**.
4. Delete the executable and its containing folder.

## Local service and privacy

The Runtime uses Windows audio and media APIs and exposes a local-only service at `127.0.0.1:17652`. It does not listen on the local network, require an account, connect to a cloud service, record audio, or transmit audio or personal data off the PC.

See [PRIVACY.md](PRIVACY.md), [SECURITY.md](SECURITY.md), and [SUPPORT.md](SUPPORT.md) for additional information.

## License

Copyright © 2026 Opulent Hardware. All rights reserved. See [LICENSE](LICENSE).

