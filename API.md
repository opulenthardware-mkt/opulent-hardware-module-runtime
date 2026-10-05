# Opulent Hardware Module Runtime Local API

Protocol version: **1.0**  
Base URL: `http://127.0.0.1:17652/api/v1`

This is the stable capability boundary between the Opulent Hardware Module Runtime and visual skin packages. A skin must not depend on the executable's location, process name, UI, or internal classes. The original mixer routes remain compatible.

## Security boundary

- Loopback only: the server binds to `127.0.0.1`.
- Allowed browser origins: opaque local-file origin (`null`), `127.0.0.1`, and `localhost`.
- Other browser origins receive HTTP 403.
- Preflight replies include `Access-Control-Allow-Private-Network: true` for Chromium/iCUE local-file access.
- No third-party requests, telemetry, accounts, or credentials.

## Health

`GET /health`

```json
{
  "service": "Opulent Hardware Module Runtime",
  "runtimeId": "com.opulenthardware.module-runtime",
  "version": "1.0.0",
  "protocolVersion": "1.0",
  "platform": "windows",
  "port": 17652
}
```

## Runtime discovery

- `GET /runtime` returns the neutral runtime ID, name, version, API version, protocol, platform, and port.
- `GET /capabilities` returns the installed capability IDs.
- `GET /diagnostics` returns non-sensitive audio readiness and device/session counts.
- `POST /handshake` accepts a skin identity and its required capabilities.

Handshake request:

```json
{
  "clientId": "com.violentcrouton.biogridvolumemixer",
  "clientVersion": "1.0.0",
  "requiredCapabilities": ["audio.master.v1", "audio.sessions.v1"]
}
```

The response includes `accepted`, the Runtime descriptor, supported capabilities, and `missingCapabilities`. Skins should hide or degrade only features whose required capability is missing.

Runtime 1.0.0 exposes `runtime.health.v1`, `runtime.handshake.v1`, `runtime.diagnostics.v1`, `audio.master.v1`, `audio.sessions.v1`, `audio.output-devices.v1`, `audio.input-devices.v1`, `audio.microphone.v1`, `media.now-playing.v1`, `media.artwork.v1`, `media.transport.v1`, and `media.seek.v1`.

## Media module

- `GET /media` returns the active Windows media session: title, artist, album, source, playback state, duration/position, enabled controls, and a stable `artworkId`.
- `GET /media/artwork/{artworkId}` returns the current artwork bytes with their native image content type. Old IDs return 404 after the track changes.
- `POST /media/command` accepts `{ "command": "previous" }`, `{ "command": "playPause" }`, `{ "command": "next" }`, or `{ "command": "seekRelative", "offsetMs": 5000 }`.

`GET /media` includes `canSeek`. Seeking is available only when the current Windows media session exposes playback-position control. The Runtime clamps every relative request to ±60 seconds and to the current track bounds.

Artwork is read from the local Windows media session, capped at 5 MB, cached in memory, and never written to disk or sent off the PC.

## Mixer snapshot

`GET /mixer`

```json
{
  "protocolVersion": "1.0",
  "timestamp": "2026-09-30T20:00:00Z",
  "output": {
    "id": "master",
    "kind": "output",
    "name": "SYSTEM OUTPUT",
    "processName": null,
    "processId": null,
    "volume": 0.5,
    "muted": false,
    "peak": 0.12,
    "active": true,
    "iconDataUrl": null
  },
  "outputDeviceId": "{0.0.0.00000000}.{example}",
  "outputs": [
    {
      "id": "{0.0.0.00000000}.{example}",
      "name": "Desktop Speakers",
      "isDefault": true
    }
  ],
  "sessions": [],
  "microphone": {
    "available": true,
    "deviceId": "{0.0.1.00000000}.{example}",
    "name": "USB Microphone",
    "devices": [],
    "volume": 0.68,
    "muted": false,
    "peak": 0.12,
    "activeApplications": []
  }
}
```

`volume` and `peak` are normalized from `0.0` to `1.0`. Multiple Windows sessions owned by the same process name are grouped into one application channel. Application channel IDs remain stable while the process identity remains the same, and the array is ordered by application name rather than live activity or peak level.

## Change a channel

`POST /channels/{channelId}` with `Content-Type: application/json`

```json
{ "volume": 0.72, "muted": false }
```

Either field can be omitted. `volume` must be between `0.0` and `1.0`. Use the reserved channel ID `master` for the default Windows output. Updating an application channel applies the requested value to all Windows audio sessions in that group.

## Change the default playback device

`POST /output` with `Content-Type: application/json`

```json
{ "deviceId": "{0.0.0.00000000}.{example}" }
```

The device ID must come from the current `outputs` array. The companion applies the selected device to Windows console, multimedia, and communications roles. Refresh the mixer snapshot after switching because master volume and available sessions can change with the endpoint.

## Control the default microphone

`POST /input` accepts the same normalized `volume` and `muted` fields as a channel update. `POST /input/device` accepts a `deviceId` from `microphone.devices` and changes the Windows default capture endpoint for all three roles. `microphone.activeApplications` reports active Windows capture sessions; the companion never records or transmits audio.

## Compatibility rules for future skins

- Check `protocolVersion` before using optional future fields.
- Ignore fields a skin does not recognize.
- Keep polling at 250 ms or slower.
- Preserve the last successful snapshot during temporary outages.
- Display a clear offline state and disable controls while disconnected.
- Declare URL permission for `127.0.0.1:17652` in the widget manifest.
