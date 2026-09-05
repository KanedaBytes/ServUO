# Goblin Gang — Shard Guide

Custom Ultima Online shard built on **ServUO `pub57`** (assembly 57.4).

Previously built on ModernUO at `E:\dev\UO\ModernUO`; those systems are being ported here.
See `Scripts/Custom/MODIFICATIONS.md` for the upstream-edit log and `CLAUDE.md` for the
engine-level conventions.

## Shard facts

| | |
| --- | --- |
| **Port** | `2594` (`Config/Server.cfg`) |
| **Name** | Goblin Gang! |
| **Era / expansion** | **EJ** — Endless Journey (`Config/Expansion.cfg`) |
| **Primary facet** | **Trammel** — all custom content, zones and spawners target Trammel unless stated otherwise |
| **Client data** | `C:\Games\Electronic Arts\Ultima Online Classic` (`Config/DataPath.cfg`) |
| **Target framework** | `net48` (.NET Framework 4.8), x64 |
| **OS** | **Windows now.** Linux later via Mono, or a .NET retarget |

## Conventions

- **All custom content lives in `Scripts/Custom/`**, namespace root `Server.Custom`.
- **Never edit upstream files.** Prefer subclassing, `EventSink`, or a `Configure()` hook.
  If an upstream edit is genuinely unavoidable, it **must** be logged in
  `Scripts/Custom/MODIFICATIONS.md` with the diff and the reason no `Custom/`-side approach worked.
- Custom JSON config lives in `Data/Custom/`; custom spawn XML in `Spawns/Custom/`.
- Custom `.cfg` files go in `Config/` like any other — they are auto-discovered at boot.
- Keep everything **Mono-safe**: no Windows-only APIs (registry, WinForms, Windows-only path
  assumptions), so the Linux path keeps working.

## Build and run

**Use the shard's build scripts.** They run the build, **abort if it fails**, and only then
launch the server.

```
# Windows
.\build.ps1              # build + run
.\build.ps1 -NoRun       # build only
.\build.ps1 -Debug       # Debug config, launches with -debug

# Linux / Mono
./build.sh               # build + run
./build.sh --no-run
./build.sh --debug
```

**The server must be stopped before rebuilding** — `Scripts.dll` is locked while loaded and
there is no hot-reload.

### Why not `_winrelease.bat` / `make`?

Because a failed script build is **silent** on ServUO. `ScriptCompiler.Compile()` never checks
the `dotnet build` exit code: it returns success regardless and loads whatever `Scripts.dll`
already exists, so a compile error boots the **previous, stale DLL** and your change simply
isn't there. The only evidence is MSBuild output scrolling past in the console.

This shard therefore sets `Config/Compiler.cfg` to **`Dynamic=False`** (the server never builds
at boot) and builds only through `build.ps1` / `build.sh`, which check the exit code. The stock
`_winrelease.bat`, `_windebug.bat` and `makefile` are left in place as upstream files but are
not the supported run path.

## Custom systems

Ported from the ModernUO shard, in this order:

| # | System | Status |
| --- | --- | --- |
| 0 | `Scripts/Custom/Core/` — loop queue, JSON config, logger, persistence base, health checks | **done** |
| 1 | Restricted zones + countdown gump → auto-jail | **done** |
| 2 | Jail administration (on ServUO's existing jail region) | **done** |
| 3 | Old Marta + auto-collect + `[ResetQuest` | pending |
| 4 | Britain daily life (day cycle, tavern, watch, townsfolk, shops) | pending |
| 5 | Admin API + MapExport + shard editor | pending |

Roadmap beyond the port: a test project, a possible .NET retarget, and a `TimedSpawner`.

## Staff commands

| Command | Access | Effect |
| --- | --- | --- |
| `[CoreSmoke` | Administrator | Exercises the `Custom/Core` foundations and reports every health check |
| `[RestrictZone <name>` | GameMaster | Target two corners to create a restricted zone |
| `[UnrestrictZone <name>` | GameMaster | Remove a restricted zone |
| `[ListRestrictedZones` | GameMaster | List every restricted zone |
| `[RestrictedZonesReload` | GameMaster | Re-read `restricted-zones.json` and rebuild the regions |
| `[Jail <player> [reason]` | GameMaster | Jail an online player; sentence escalates per offence |
| `[Unjail <player>` | GameMaster | Release immediately |
| `[JailInfo <player>` | Counselor | Show a player's jail record |
| `[JailRecord` | Player | Show your own jail record |

## Health checks

`[CoreSmoke` (Administrator) exercises the `Custom/Core` foundations and reports every
registered health check — including any persistence store that has gone **degraded** and is
refusing to save. Run it after every upstream merge.

Systems register their own checks with `HealthCheck.Register(...)` rather than growing the
command, so `[CoreSmoke` stays a one-command health check for the whole shard.

ServUO's console cannot invoke staff commands, so to run it headlessly set
`CoreSmokeOnStart=True` in `Config/Custom.cfg` and read the console. Leave it `False` on a
live shard.

## Shard editor (once ported)

Enable in `Config/AdminApi.cfg`, then browse `http://127.0.0.1:8081`.
The API binds **loopback only**; remote access is via SSH tunnel
(`ssh -L 8081:127.0.0.1:8081 user@host`). The bearer token is generated on first run and kept
in `Config/AdminApi.cfg`, which is gitignored.

Map tiles are rendered separately and are safe to build while the shard is up:

```
dotnet run --project MapExport -c Release -- --out web/tiles --client "C:\Games\Electronic Arts\Ultima Online Classic"
```
