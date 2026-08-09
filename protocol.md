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
