# Diagnostics

Every network event should be logged with:

```text
[COOP] role=host direction=send revision=12 kind=team_orders
[COOP] role=client direction=receive revision=12 kind=team_orders
[COOP] role=client apply revision=12 session=Race
```

For each test capture:

- host `MM_Data/output_log.txt`;
- client `MM_Data/output_log.txt`;
- selected save SHA-256;
- current revision;
- current session and game date;
- disconnect/reconnect timestamps.
