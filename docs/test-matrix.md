# Coop test matrix

The host is authoritative. A client sends commands; the host assigns a
revision and broadcasts the accepted action. A client must never write the
authoritative save without a host-approved snapshot.

## State boundaries

| Area | State to compare | Current status |
|---|---|---|
| Connection | role, protocol, session, revision | Prototype |
| Career clock | date, next event, pause/speed | Partial |
| Calendar | current session, next session, event state | Partial |
| Save | selected save, backup, snapshot checksum | Partial |
| Team | drivers, staff, contracts, morale | Not tested |
| Finance | cash, income, expenses, sponsor payments | Not tested |
| HQ/research | buildings, upgrades, research queue | Not tested |
| Car | parts, reliability, design queues | Not tested |
| Race setup | tyres, fuel, setup, driver assignment | Partial |
| Race strategy | team orders, pit strategy, fuel, tyres | Partial |
| Race runtime | positions, flags, pit stops, damage, results | Not tested |
| Season boundary | standings, rewards, contracts, next season | Not tested |

## Required two-client scenarios

1. Host creates/loads `SaveJohn Sina - Scuderia Rossini 7 Coop.sav`.
2. Client joins and receives the host save.
3. Both clients confirm date, session and save checksum.
4. Run Practice, Qualifying and Race without pausing the test.
5. Change one decision at a time and verify both logs.
6. Disconnect the client during each session and reconnect.
7. Drop a revision and verify resync restores the client.
8. Save on host, reject save on client, reload from host.

## Acceptance criteria

- No duplicate or reordered command is applied.
- No client-only decision changes authoritative state.
- Host and client reach the same session after every boundary.
- Race results and post-race finance are identical.
- A resync creates a backup before replacing a client save.
- The game remains playable after client disconnect/reconnect.

## Discovered integration points

The Mono assembly exposes these useful state boundaries:

- Time: `GameTimer.PlaySkipSim`, `GameTimer.PauseOrPlaySkipSim`, `GameTimer.SetSpeedDontUnpause`.
- Sessions: `RaceEventDetails.GoToNextSession`, `Championship.GoToNextSession`, `SessionSimulation.SimulateNextSession`.
- Race strategy: `SessionStrategy.SetTeamOrders`, `SessionStrategy.SetPitStrategy`, `SessionStrategy.ActivateOrder`, `SessionStrategy.SetOrderedLapCount`.
- Save: `SaveSystem.ManualSave`, `SaveSystem.ManualSaveAs`, `SaveSystem.AutoSave`, `SaveSystem.LoadSaveWithName`.
- Car design: `CarPartDesign.StartDesigning`, `CarPartDesign.BuildTwoParts`, `NextYearCarDesign.StartDesign`.
- Pit crew: `PitCrewController.AssignRoleToPitCrewMember`, `PitCrewController.SwapActivePitCrewMembers`.

These are integration candidates, not yet proof that every UI path reaches the
same method. Each candidate must be verified by a diagnostic log before it is
made network-authoritative.
