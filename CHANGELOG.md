# Changelog

## 1.0.2 — 2026-10-15

- Hardened album and video artwork across natural media transitions and autoplay.
- Preserved the previous cover briefly while Windows, Spotify, Apple Music, YouTube, or a browser publishes the next thumbnail.
- Added a bounded recent-artwork cache so delayed widget image requests remain available when the active track changes.
- Added rechecks for browsers that temporarily publish the previous thumbnail under a new video title.
- Prevented temporary artwork failures from being cached by the widget webview.

## 1.0.1 — 2026-10-12

- Fixed playback and microphone endpoint ordering so changing the Windows default device no longer reshuffles device cycling.
- Ensured clients can traverse every active endpoint, including built-in speakers, display audio, USB outputs, and headset endpoints.

## 1.0.0 — 2026-10-04

- Initial public release of Opulent Hardware Module Runtime.
- Added local Windows master and per-application audio controls.
- Added playback-device and microphone management.
- Added now-playing metadata, artwork, progress, transport, and supported seeking.
- Added first-run Windows startup registration.
- Restricted the service to local loopback with no account or cloud dependency.

