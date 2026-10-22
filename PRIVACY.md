# Privacy

Opulent Hardware Module Runtime operates locally on the user's Windows PC.

## Data handling

- No account is required.
- No cloud service is used by the Runtime.
- Audio is not recorded, stored, uploaded, or transmitted.
- Media artwork and playback information are read from Windows media-session APIs and remain on the PC.
- Application names, audio-session information, device names, and microphone activity remain on the PC.
- No analytics, advertising identifiers, or usage telemetry are collected.

## Local connection

Compatible widgets communicate with the Runtime through `127.0.0.1:17652`. This is the computer's loopback address and is not exposed to the local network or internet.

## Microphone information

The Runtime can report the selected microphone, gain, mute state, live input level, and applications Windows identifies as using the microphone. It does not capture or retain microphone audio.

