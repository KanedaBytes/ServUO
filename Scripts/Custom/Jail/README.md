# Custom/Jail

The jail administration layer, built on top of ServUO's **existing** Jail region
(`Scripts/Regions/Jail.cs`, `Data/Regions.xml:699` Felucca and `:1770` Trammel).

## What ServUO already enforces — not reimplemented here

The region and thirteen call sites across the tree already prevent, for non-staff inside jail:
spellcasting (`502629`), all skill use (`1116000`) **and skill gain** (`SkillCheck.cs:366`),
harmful and beneficial acts (`1115999`), combatant changes, housing, recall/gate/mark/teleport out
(`SpellHelper.cs:875`, cliloc `1114345`), every escape item (Bag of Sending, Bracelet of Binding,
Horn of Retreat, Ball of Summoning), the `[help` stuck option and Young transport, pet renaming,
Young death teleport, character deletion while parked in jail, and ambient light level 9.

**`Jail.AllowAutoClaim` returns false**, so `PlayerMobile.ClaimAutoStabledPets()` short-circuits —
ServUO already withholds stabled pets for the whole sentence and returns them on the first login
after release.

Not enforced by anything, and out of scope: walking out (containment is pure geometry — one 41×33
rect, no cells), speech, trade, item use, and being gated in by a third party.

## What this adds

Sentence records, escalation, persistence, release timers, the status gump, and the commands.

| Command | Access | Notes |
| --- | --- | --- |
| `[Jail <player> [reason]` | GameMaster | Online only. Reason is the whole rest of the line |
| `[Unjail <player>` | GameMaster | Works on offline players and on expired-but-unreleased ones |
| `[JailInfo <player>` | Counselor | Full record, plus what the next sentence would be |
| `[JailRecord` | Player | Own record, 30-second cooldown |

## Sentences

Linear escalation, clamped: **5 minutes** at the first offence rising to **12 hours** by the
tenth, and 12 hours for every offence after.

```
sentence = clamp(5min + (offence - 1) * (12h - 5min) / 9, 5min, 12h)
```

Offence 1 → 5m, 2 → 1h 24m, 10 → 12h, 11+ → 12h. `JailCount` is never reset — the escalation is
meant to be permanent.

**`JailEndTimeUtc` is absolute UTC**, so real time passes while the server is down and a sentence
can expire during an outage. That is intentional, and it is why there is a login safety net.

## Sequences

Jail (10s, frozen throughout): freeze + message + sound `0x204` + staff broadcast → **+2s**
dismount, dismiss summons, force-stable the rest → **+5s** teleport to the Jail region's
`GoLocation` on the player's own facet → **+10s** unfreeze, arm the release timer, show the gump.

Release (10s): freeze + message + sound `0x1FF` + staff broadcast → **+5s** teleport to
**Britain Bank (1444, 1697, 10)** on the origin facet → **+10s** unfreeze, welcome messages, and
a line about stabled pets if there are any.

Facets are clamped to Felucca/Trammel (the only two with a jail, and the only two with a Britain);
anything else falls back to Trammel.

### Pets — order matters

`BaseCreature.CanAutoStable` returns **false** for anything `Summoned` and for a mount that still
has a rider. So the order is **dismount → dismiss summons → `AutoStablePets()`**; get it wrong and
the mount and every summon are silently left standing in the world.

`AutoStablePets()` is ServUO's own logout path, so it bypasses the stable fee and the slot limit.

## Three fixes over the ModernUO original

1. **The release timer is always stopped before being replaced.** The original overwrote the
   dictionary entry and left the old timer live, releasing the player early and then firing again.
2. **No persistent "being jailed" latch.** The original re-added prisoners to it on load and never
   cleared it on release, leaving a restored prisoner permanently unjailable. Ours is transient
   and never populated on load.
3. **The timer is armed from the absolute end time, not the sentence length.** The original
   stamped the end time at t=0 but armed the timer at t=+10s for the full duration, so release ran
   ten seconds late while `IsPlayerJailed` went false ten seconds early.

Also: sentence lengths are formatted as whole units. The original interpolated raw `TotalMinutes`
and printed *"jailed for 84.444444445 minutes"*.

## Failure mode — the jail fails open

`JailSystem` is a `CustomPersistence` store. If it goes **degraded** (unreadable save file) it
loads empty, so **every prisoner is free** rather than anyone being falsely imprisoned. The
degraded store refuses to save and quarantines the file to `Backups/Degraded/`, so nothing is
lost, and `[CoreSmoke` reports it as `Fail`.

## Notes

- `Mobile.Frozen` blocks **movement only** — not speech, casting, item use or gumps. It is not
  persisted and `Kill()` clears it, which is why it is used only during the two 10-second
  sequences and never for the sentence itself.
- `EventSink.Login` is the correct hook: `Map`, `Location`, `Region` and `NetState` are all
  restored and final before it fires. `EventSink.Connected` fires *before* the map restore.
- Character names are **not unique** across accounts. `TryFindPlayer` refuses on multiple matches
  rather than picking one.
- Offline jailing is feasible (offline characters keep settable `LogoutLocation`/`LogoutMap`) but
  is deliberately not built — a sentence should start when it is actually served.
