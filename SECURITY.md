# Security Policy

## Supported version

Security fixes are provided for the latest published release.

## Reporting a vulnerability

Please report suspected vulnerabilities privately to the repository owner rather than opening a public issue. Include reproduction steps, the affected Runtime version, and any relevant logs with personal information removed.

Do not include audio-session names, device identifiers, usernames, or local filesystem paths unless they are necessary to reproduce the issue.

## Security model

The Runtime listens only on IPv4 loopback at `127.0.0.1:17652`, validates browser origins, and exposes no LAN or cloud endpoint. It does not record or transmit audio.

