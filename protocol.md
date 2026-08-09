# LAN protocol v0

Transport: TCP over the local network. The host owns the authoritative game
state and the save file.

Each packet is one UTF-8 JSON object followed by `\n`:

```json
{"type":"hello","protocol":0,"name":"Player 2"}
{"type":"welcome","protocol":0,"session":"...","host":"Player 1"}
{"type":"action","id":"...","kind":"advance_day","payload":{}}
{"type":"state","revision":1,"kind":"career_snapshot","payload":{}}
{"type":"error","message":"..."}
```

The host rejects unsupported protocol versions and clients never write the
authoritative save directly.

## Race controller

The supported coop topology is one authoritative game process plus a lightweight
race controller. The controller does not load Unity or a career save.

Host telemetry (sent twice per second while a session is running):

```json
{"type":"telemetry","session":"Practice","speed":1,"paused":false,"vehicles":[{"id":10,"driver":"Driver","lap":3,"position":5,"fuel":8.4,"tyreWear":72.0,"status":"Racing"}]}
```

Controller command:

```json
{"type":"action","kind":"engine_mode","target":10,"value":2,"aux":0,"flag":0}
```

Supported controller commands are `driving_style`, `engine_mode`, `ers_mode`,
`send_out_on_track`, `return_to_garage`, `pit_command`, `cancel_pit`, `pit_fuel`,
`pit_repair`, `pause_or_play`, and `simulation_speed`.
