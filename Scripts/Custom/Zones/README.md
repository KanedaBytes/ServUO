# Custom/Zones

Restricted zones: a non-staff living player entering a marked rectangle gets a warning and an
undismissable 30-second countdown gump. Leave in time and it cancels; stay and they are handed
to the jail service.

Ported from ModernUO `Custom/Zones/`. Config lives in `Data/Custom/restricted-zones.json`, in a
schema **identical** to the ModernUO shard's so the map editor works against either.

## Commands

All `AccessLevel.GameMaster`. Every mutator writes the JSON and rebuilds the regions.

| Command | Effect |
| --- | --- |
| `[RestrictZone <name>` | Target two corners to create a zone (inclusive of both) |
| `[UnrestrictZone <name>` | Remove a zone by name |
| `[ListRestrictedZones` | List every zone |
| `[RestrictedZonesReload` | Re-read the JSON and rebuild (alias: `[ReloadRestrictedZones`) |

## Config

```json
{ "zones": [ {"name":"...","map":"Trammel","x":0,"y":0,"width":0,"height":0} ] }
```

`x`/`y`/`width`/`height` are **origin plus size**, not a corner pair. `map` defaults to
`"Trammel"` if absent. Validation collects every problem at once — blank name, duplicate name
(case-insensitively), unknown facet, non-positive size.

**A failed load keeps the live zones.** Every error path returns before the existing set is torn
down, so a bad edit can never leave the shard with no zones. `[RestrictedZonesReload` says so
explicitly when it fails.

## Behaviour contract

- Warning on entry: hue `0x22`, sound `0x1F3`, then the gump at 30 counting down at 1 Hz.
- Leaving in time: `"You have left <zone>."` at hue `0x40`. Only sent if a countdown was actually
  running — not on disconnect, re-entry, or after a jail.
- Re-entry **restarts** at 30, never resumes.
- **Resurrecting inside counts as a fresh entry.** Death fires no region change, so without this
  dying at 29 seconds and resurrecting at a shrine inside the zone would let you loiter forever.
- Staff (`AccessLevel > Player`) and ghosts are never warned.
- Countdown state is **in memory only, by design** — a restart clears every pending countdown so
  nobody is jailed by a timer they could not see.
- Creating or resizing a zone catches players already standing in it: `Region.Register()` sweeps
  the affected sectors and re-resolves every mobile, firing `OnEnter`.

### Known gap: disconnect resets the countdown

Cancelling on disconnect (ModernUO parity) means a player can drop their link at 29 seconds,
reconnect, and get a fresh 30 — indefinitely. Accepted deliberately: the zone still works as a
deterrent, since someone who has to keep dropping their connection is effectively locked out of
the area anyway. The alternative — jailing on disconnect — punishes genuine connection drops.

### Noise on reload

Unregistering a zone fires `OnExit` for everyone inside, so a reload sends `"You have left ..."`
to every occupant and then immediately re-warns them. Cosmetic, and matches ModernUO.

## ServUO-specific notes

- **`YoungProtected => true` is not optional.** ServUO's `BaseRegion.OnEnter` (unlike ModernUO's
  base) pops a `YoungDungeonWarning` gump at Young players, which would fight the countdown gump.
- **`OnResurrect` must call `base`** — ServUO's chains to the parent region, unlike `OnEnter` /
  `OnExit` which are dispatched per-region up the chain and must not.
- **`CloseGump` before every `SendGump` is mandatory.** `NetState.AddGump` *disconnects* the
  client at 512 gumps; at 1 Hz that is under nine minutes.
- The region name is deliberately `null` so it is not added to `Map.Regions` — a duplicate name
  there warns, and unregistering the second evicts the first's entry.

## Jail

`IJailService` / `JailService.Provider` is the seam. **The current provider is
`StubJailService` — a placeholder, not a jail.** It teleports the player into ServUO's existing
Jail region (on their own facet, clamped to Felucca/Trammel) and remembers them in memory only.
No sentence, no release, no persistence. `[CoreSmoke` reports it as `Warn` while it is active.

Port step 2 replaces it by assigning `JailService.Provider` from its own `Configure()`, which
runs before `JailService.Initialize()` so no stub is ever constructed.

Callers must **guard with `IsPlayerJailed` before jailing and assert it afterwards**. That shape
is carried over from the ModernUO shard, where jailing an already-jailed player overwrote the
release timer without stopping it, and `JailPlayer` could silently no-op.
