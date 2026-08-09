# Two-client test procedure

## Safe test available now

Use one real game instance as host and one protocol client:

```powershell
node .\lan_client.js 127.0.0.1 27153 "Virtual Client"
```

This validates connection, role negotiation, commands, revisions, save
permissions and resync without opening a second writer against the same save
directory.

## Full race test

Two full game processes must not share `Cloud\Saves`. The current machine has
about 12.49 GB free while the game installation is about 14.75 GB, so a full
second copy is intentionally not created.

Run the full test after either freeing enough disk space or using a second PC:

1. Install the exact same game build and mod commit on both machines.
2. Prepare the same `* Coop.sav` from the host launcher.
3. Start Host in the first game.
4. Start Client in the second game and request the host snapshot.
5. Verify Practice, Qualifying and Race session boundaries.
6. Compare both logs, game date, session, strategy and final results.
7. Test disconnect/reconnect during each session.

Never run two instances against the same save directory.
