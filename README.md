# Motorsport Manager Coop (LAN prototype)

This directory contains the first LAN prototype for the local copy of
Motorsport Manager.

## Current status

- The game is a Unity Mono build and exposes `Assembly-CSharp.dll`.
- No mod loader is installed in the game directory, so the prototype is kept
  separate from the original installation.
- The first milestone is a host/client handshake and state-message transport.

## Planned integration

1. Add a Mono-compatible loader entry point.
2. Add an in-game LAN menu (host/join).
3. Synchronize one deterministic game action at a time.
4. Extend synchronization to career, race and save boundaries.

Do not copy files into the game directory until the loader stage is ready.
