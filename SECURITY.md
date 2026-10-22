# Security policy

## Supported version

Security fixes are provided for the latest published Runtime release.

## Reporting a vulnerability

Report suspected vulnerabilities privately to the repository owner rather than opening a public issue. Include reproduction steps and the affected Runtime version. Remove personal information from logs before sharing them.

Do not include audio-session names, device identifiers, usernames, or local filesystem paths unless they are necessary to reproduce the issue.

## Security model

The Runtime binds only to IPv4 loopback at `127.0.0.1:17652`, validates browser origins, and exposes no LAN or cloud endpoint. It does not record or transmit audio.

