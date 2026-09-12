# Should `PlayerBot` stay a `BaseCreature`?

**Decision spike, 11 September 2026. Read-only.** No code was changed, nothing was built, the shard
was not run, no probe was executed, `bots.json` was not touched. Every claim below is a static
reading of source, cited to `file:line`, verified against this tree (assembly 57.4) and against the
pinned reference `E:\dev\UO\uo-offline` @ `7f38c7c`.

---

## Sean's decision

**(d) confirmed — `PlayerMobile`, accountless now; an `Account` per persistent economic bot later.**

- **PK means Felucca.** Trammel stays as it is.
- **The migration starts next session, with the locomotion adapter** — the `INavActor` step in
  [§5](#5-what-exists-only-because-bots-are-basecreatures) below.

The rest of this file is the evidence that was put in front of that decision, kept because the
reasons matter more than the verdict: the next person to ask "why is `BotAI` gone?" or "why did we
edit `FastAStarAlgorithm.cs`?" should find the answer here rather than re-derive it.

One consequence of the Felucca answer is recorded up front, because it moves a link in the argument
below. On Trammel, a guild war was the *only* legal route to bot-on-bot harm
(`Notoriety.cs:154,171`), which chained "PK" onto "guilds". On Felucca `Mobile_AllowHarmful` returns
at `Notoriety.cs:128` — *"In felucca, anything goes"* — before any of that. **So PK no longer
depends on guilds.** Guilds remain a `PlayerMobile` gate on their own account
(`GuildRosterGump.cs:206`), and the other reasons for (d) are untouched; the chain is simply shorter
than it was when the memo was drafted.

---

## Contents

1. [Four lifetimes, kept apart](#1-four-lifetimes-kept-apart)
2. [The seven wants, and what the engine actually gates on](#2-the-seven-wants-and-what-the-engine-actually-gates-on)
3. [Banking, which underpins all of 7f](#3-banking-which-underpins-all-of-7f)
4. [How uo-offline survives with no client](#4-how-uo-offline-survives-with-no-client)
5. [What exists only because bots are `BaseCreature`s](#5-what-exists-only-because-bots-are-basecreatures)
6. [Cost per bot on the game thread](#6-cost-per-bot-on-the-game-thread)
7. [The four options](#7-the-four-options)
8. [What could not be answered from the code](#8-what-could-not-be-answered-from-the-code)

Method: `REVIEW.md` §3 in full plus F2 and §11, then `CLAUDE.md`, then `SHARD.md`'s **Picking the
bot layer up cold** (`SHARD.md:145`), then this folder's README — **Current contract**, **The
constraint everything else follows from**, **Deviations from uo-offline**, **Severed seams** — then
targeted engine source, then the reference. Where `REVIEW.md` §3 already cites a gate, this file
says *confirms* or *refutes* rather than re-deriving it.

---

## 1. Four lifetimes, kept apart

`REVIEW.md` §3 closes on this and it is the spine of everything below: a bot's **durable identity**,
its **`Mobile` object**, its **`Account`** and its **`NetState`** are four different things with
four different lifetimes. Almost every wrong answer in this area comes from collapsing two of them.

- The **`Mobile`** is what stands in Britain. Today it dies at every restart by policy
  (`PlayerBot.cs:1086`), not by inheritance.
- The **`NetState`** is a socket. Nothing — no class, no account — conjures one, and this shard
  should not fake one (`REVIEW.md` §3).
- The **`Account`** is where the engine keeps house limits, house decay eligibility and, on this
  shard, gold (`CurrentExpansion.cs:20`).
- The **durable identity** is the thing that owns property across all three. It does not exist yet.

Read that way, the seven wants sort cleanly, and only one of them is really about the class.

---

## 2. The seven wants, and what the engine actually gates on

| Want | Gated on | Works today on `BaseCreature` + `Player=true`? | Custom code needed either way? | Impossible without an `Account`? |
| --- | --- | --- | --- | --- |
| Gain skills | `Mobile` + the Player flag; *quality* on `PlayerMobile` | Yes — without anti-macro, GGS or cap redistribution, **and with a stat-total-cap breach** | No | No |
| PK / murder | Player flag (events), `PlayerMobile` (handlers), **facet** (whether it is legal at all) | Death events yes; **the murder report throws** (F2); Trammel forbids it outright | Yes — reporting and decay | No |
| Join guilds | **`PlayerMobile`** | **No — a bot cannot even be invited** | Yes — clientless acceptance | No |
| Be traded with | **`NetState`**, on a `virtual` method | The stock gate refuses; an override is legal today | Yes, in both classes | No |
| Party with players | Player flag | **Yes** | Yes — `BotParty`, already built | No |
| Buy and own houses | `Mobile` owner; **`Account`** for the limit and for decay | Owner yes; **Condemned**, and no limit applies | Yes | **Yes** |
| Run their own vendors | `Mobile` owner **+ a house** | Owner yes | Yes | Through the house |

### 2.1 Gain skills — a partial `PlayerMobile` gate, and one live defect

`SkillCheck.Gain` takes a `Mobile`. The **total skill cap is enforced by the Player flag, not the
type**: `SkillCheck.cs:444` reads `if (!from.Player || (skills.Total + toGain <= skills.Cap))`, and
our bots are flagged, so the 700 holds.

What a `BaseCreature` never reaches is the *quality* of a player's progression — all
`PlayerMobile`-typed branches: anti-macro (`SkillCheck.cs:336,347`), down-lock redistribution at
total cap (`:439-441` → `CheckReduceSkill`, `:484`), GGS (`:450`), Siege (`:380`), ML enhanced skill
(`:404`), accelerated skill (`:411`). And `Focus` is refused outright for an uncontrolled
`BaseCreature` (`SkillCheck.cs:372-373`) — which is the same engine behaviour this folder already
works around from the other side, where `ChangeAIType` *stamps* `Focus` and `DetectHidden` onto
every non-vendor creature (README, **The engine writes skills behind you**).

**A defect, not merely a difference.** `SkillCheck.CanRaise` guards its at-total-cap branch with
`atTotalCap && from is PlayerMobile` (`SkillCheck.cs:588,601,614`). Anything else falls straight
through to `return true`. `IncreaseStat` then only lowers a sibling stat when `CanLower` allows
(`:629,637-647`), and `CanLower` requires `StatLockType.Down` (`:566-577`), which nothing sets on a
bot. So **a `BaseCreature` bot standing at the 225 stat total can raise a stat past it**, where a
`PlayerMobile` simply stops gaining. This is not in `REVIEW.md`. It matters here because our bots
*do* gain: session 5 drives ServUO's real harvest and craft, and that is the one place this port
deliberately does more than upstream (README, **The yield is real, and upstream's was not**).

There is a second, sharper skills consequence that lands directly on 7f. `Mining.GetResourceType`
resolves the ore a swing produces through `PlayerMobile pm = from as PlayerMobile`
(`Mining.cs:206`): gem mining at `:216`, stone at `:221`, and everything else falls to
`resource.Types[0]` at `:229` — plain iron, forever. Sand is `PlayerMobile`-gated outright
(`:286`). **A `BaseCreature` Miner can never bring home coloured ore, granite or gems**, and that is
the engine's decision, not a config one.

*Confirms `REVIEW.md` §3's skill row* ("BaseCreature can gain some skills, so 'it cannot gain skills'
is false; player-equivalent progression is also false"), and adds the stat-cap breach and the ore
ceiling.

### 2.2 PK and murder — the Player flag raises it, `PlayerMobile` handles it, the facet permits it

`Mobile.OnDeath` takes the player branch on `!m_Player` (`Mobile.cs:4229-4247`) and raises
`PlayerDeath` at `:4259`. That is the **flag**, so a bot already produces player death events.

The handler is where it breaks. `ReportMurdererGump.EventSink_PlayerDeath` casts the victim at
`ReportMurderer.cs:44` — `((PlayerMobile)m).RecentlyReported` — reached under `Core.SE` with a
reportable player aggressor, and a bot killing a bot meets that condition because both are flagged.
**This is F2**, reproduced on every `[BotSmoke` by `Bots.Death`, and `PlayerMobile` removes it.

**`PlayerMobile` removes the throw and does not award the count.** The gump is delivered to a client
(`new GumpTimer(m, killers)`, `ReportMurderer.cs:96`) and the count is awarded inside
`OnResponse(NetState state, RelayInfo info)` (`:112`, `killer.Kills++` at `:122`). A clientless
victim answers nothing, so a player could still cut down the whole of Britain and stay blue. The
repair is an adapter, and upstream has written it — see [§4](#4-how-uo-offline-survives-with-no-client).

Two further asymmetries survive the class change, both worth writing down now that PK means Felucca:

- **A bot murderer never cools off.** `Kills` and `ShortTermMurders` live on `Mobile`
  (`Mobile.cs:11766,11797`), but the decay is `PlayerMobile.CheckKillDecay` (`PlayerMobile.cs:5151`)
  driven by `GameTime`, which only advances while `NetState != null`
  (`PlayerMobile.cs:5182-5194`, accumulated on logout at `:1702`). `GameTime` for a clientless bot
  stays at zero, so neither the 8-hour nor the 40-hour elapse is ever passed. `ResetKillTime`
  (`ReportMurderer.cs:132`) is `PlayerMobile`-only as well.
- **A bot red is never banished.** `ReportMurderer.CheckMurderer` teleports a new murderer to
  Felucca, but only `if (m.NetState != null)` (`ReportMurderer.cs:169`). A bot that turns red on
  Felucca will happily walk back to Trammel unless something of ours stops it.

On the facet itself: `MapRules.TrammelRules` includes `HarmfulRestrictions` (`Map.cs:128`), so
`Mobile.CanBeHarmful` (`Mobile.cs:7983`) defers to `Region.AllowHarmful` (`Region.cs:888`) →
`Notoriety.Mobile_AllowHarmful` (`Notoriety.cs:119`), which at `:171` says
`if (target.Player) return false;` — **on Trammel, no Player-flagged mobile may harm another, in
either class.** `FeluccaRules = None` (`Map.cs:129`), so the same method returns at `:128` before
any of it. Sean's answer takes the Felucca door, which is the one that does not require a guild war
(`Notoriety.cs:154`).

*Confirms `REVIEW.md` §3's PK row*, including "changing the class does not enable PvP in Trammel",
and supplies the mechanism behind the `Notoriety.cs:140` citation it gives without one.

### 2.3 Join guilds — the hardest gate, and the only one that is purely about the class

`Guild.AddMember(Mobile)` accepts any `Mobile` (`Guild.cs:1585`), and only sets a rank for a
`PlayerMobile` (`:1608`). That much `REVIEW.md` says.

**What it does not say is that nothing can put a bot in the list in the first place.** This is an EJ
shard, so `Core.SE` is true, so `Guild.NewGuildSystem` is true (`Guild.cs:779`), and the recruit
path opens with `PlayerMobile targ = targeted as PlayerMobile;` (`GuildRosterGump.cs:206`) and
answers a null at `:222` with *"That isn't a valid player."* **A player standing at a guildstone
cannot invite a bot at all.** There is no `Custom/`-side override for that; it is a local variable in
a stock targeting callback.

Membership would also be cosmetic if it were achieved by other means: the roster gump builds itself
from `Utility.SafeConvertList<Mobile, PlayerMobile>(g.Members)` (`GuildRosterGump.cs:123`), and
`SafeConvertList` silently drops anything that fails the cast (`Utility.cs:1486-1498`), so a
`BaseCreature` member would be invisible to every player who opened the roster. Voting needs a rank
(`Guild.cs:1820`).

`PlayerMobile` is **necessary but not sufficient**: acceptance is a gump
(`GuildInvitationRequest.cs:11,33`), so a clientless bot needs a `BotParty`-shaped adapter, and
online counting is `NetState.Running` (`Guild.cs:2009,2011`) and will never be true for a bot in
either class.

*Confirms and sharpens `REVIEW.md` §3's guild row.* Its "a BaseCreature in a member collection is not
full guild support" is right; the stronger fact is that on *this* shard a `BaseCreature` cannot reach
the collection through any stock path.

### 2.4 Be traded with — a `NetState` gate on a `virtual` method, and ServUO needs no patch

`Mobile.OpenTrade` (`Mobile.cs:10840`) checks `!from.Player || !Player` first — the **flag**, which
passes today — and then both `NetState`s (`:10846-10850`). It is `virtual`, so a bot of **either**
class may override it and build the trade itself.

**And ServUO's `SecureTrade` already tolerates a clientless participant.** Every `NetState` read in
it is guarded (`SecureTrade.cs:25-29,166,173,196-197`) and `Mobile.Send` is null-safe
(`Mobile.cs:7403-7418`). This is a real divergence from ModernUO, where the constructor dereferenced
`NetState` unconditionally and upstream had to patch `Projects/Server/SecureTrade.cs`
(`uo-offline/INTEGRATION-NOTES.txt`, *"let a bot be one side of a trade window"*).

So a bot-side trade window is buildable **today, on a `BaseCreature`, with no upstream edit**. This
is the one want on which the class decision is genuinely irrelevant.

*Confirms `REVIEW.md` §3's trade row* — "neither `PlayerMobile` nor `Account` supplies a network
session" — and adds the useful half: the engine will not fight us here.

### 2.5 Party with players — works today; keep the adapter, move its clock

`AddPartyTarget.cs:30-32` refuses on `!m.Player`, which the flag already satisfies, and
`Party.Invite` sends through `target.Send(...)` (`Party.cs:167-195`), which is null-safe.
`BotParty.CheckInvite` then walks the same path `/accept` walks (README, **Party invitations**).

One migration item, which `REVIEW.md` already asks for: the poll lives in `PlayerBot.OnThink`
(`PlayerBot.cs:976`), and `OnThink` has exactly two callers, both inside the `BaseAI` timer
(`BaseAI.cs:2204,3082`). Under `PlayerMobile` it has no driver at all and must move to
`BotTickManager`.

*Confirms `REVIEW.md` §3's party row verbatim*, including "this is already an appropriate adapter,
not a reason to keep `BaseCreature`".

### 2.6 Buy and own houses — the only want that genuinely needs an `Account`

Ownership itself is a `Mobile`, so "assign an owner" works in either class. Everything around it does
not.

- **The limit does not apply.** `BaseHouse.GetAccountHouseCount` returns **0** with no `Account`
  (`BaseHouse.cs:4033-4037`), so `AtAccountHouseLimit` (`:4082`) is always false and an accountless
  bot silently evades `Housing.AccountHouseLimit` (`BaseHouse.cs:23`).
- **The house is condemned.** `BaseHouse.DecayType` (`:75-104`): with no `Account` it returns
  `Core.AOS ? DecayType.Condemned : DecayType.ManualRefresh`, and EJ is AOS. The only escape without
  an account is `RestrictDecay = true` (`:77`) — which is exactly what upstream does, and it is not
  ownership, it is scenery ([§4](#4-how-uo-offline-survives-with-no-client)).
- **And it crashes.** `BaseHouse.HandleDeletion` reads `acct.Length` with no null check
  (`BaseHouse.cs:3529-3536`) and is called from `PlayerMobile.OnAfterDelete`
  (`PlayerMobile.cs:5250`). **An accountless `PlayerMobile` bot that owns a house and is deleted
  throws a `NullReferenceException` on the game thread.** Today's `BaseCreature` bot never reaches
  that call, so its house is merely orphaned. This is not in `REVIEW.md`, and it is the reason for
  the written rule attached to option (d).

Placement withdraws real money (`HousePlacementTool.cs:788`) and one UI path casts
`(PlayerMobile)from` (`HousePlacementTool.cs:1017`).

*Confirms `REVIEW.md` §3's house row* — "'assign an owner' is insufficient" — and names the two
mechanisms it leaves implicit: `Condemned`, and the deletion NRE.

### 2.7 Run their own vendors — class-neutral; the prerequisite is a house

`PlayerVendor.Owner` is a `Mobile`, serialized as one (`PlayerVendor.cs:290,492`), and neither
ownership nor the wage timer tests `PlayerMobile` or `Account` (`:647`, `:1516`).

But `BaseHouse.NewVendorSystem` is `Core.AOS` (`BaseHouse.cs:25`), so `IsOwner` defers to
`House.IsOwner(m)` (`PlayerVendor.cs:652`). **A player vendor is a house feature**, so this want
inherits §2.6 whole.

*Confirms `REVIEW.md` §3's vendor row*: "neither `Account` nor `PlayerMobile` alone is a universal
vendor prerequisite. Durable owner identity, wage funding, stock management, and clientless
management are the real missing contracts."

---

## 3. Banking, which underpins all of 7f

`Banker.GetBalance`, `Withdraw` and `Deposit` (`Banker.cs:33,96,151`) prefer **account gold** when
`AccountGold.Enabled && from.Account != null`, and fall back to the physical bank box otherwise.
`AccountGold.Enabled = Core.TOL` (`CurrentExpansion.cs:20`), which is **true here**.

Today's bots already have a real bank box: `Mobile.BankBox` creates on demand for any mobile
(`Mobile.cs:10498-10515`), and `GetBalance` picks `m.Player ? m.BankBox : m.FindBankNoCreate()`
(`Banker.cs:48`) — the Player flag again.

The consequence to hold on to for 7f is that **giving a bot an `Account` moves its money out of items
and into the account ledger**, and a *shared* account means one shared purse for every bot on it.
That is the concrete thing that rules out option (b) below.

*Confirms `REVIEW.md` §3's Banker inference*: an account is not mandatory for a purchase, and is
consequential rather than cosmetic where it exists.

---

## 4. How uo-offline survives with no client

Read at `E:\dev\UO\uo-offline` @ `7f38c7c`, source under `playerbots/source/CustomBots/`.

**They create no accounts at all — not one per bot, not one shared.** `class PlayerBot : PlayerMobile`
(`PlayerBot.cs:28`), and the only occurrence of the word `Account` in the entire bot source is
`BotBankTest.EmptyAccount` (`:313`), which is a `Banker.Withdraw` helper, not an `IAccount`. Every
bot they run is an accountless `PlayerMobile`.

**The `PlayerMobile` surface they actually override is small** — about seventeen members, which is
what this folder's README means by "the `PlayerMobile` dependency was shallow" (README:93):
`ShouldCheckStatTimers => false` (`:585`), `CheckShove => true` (`:595`), `Move(Direction)` opening
doors (`:611`), `CriminalAction` (`:633`), `IsHarmfulCriminal` (`:639`), `ApplyNameSuffix` (`:655`),
`OnAfterSpawn` (`:683`), `HandlesOnSpeech` / `OnSpeech` (`:851,854`), `OnBeforeDeath` (`:860`),
`OpenTrade` (`:874`), `OnDeath` (`:882`), `OnAfterDelete` (`:937`), `OnGuildChange` (`:170`), and
serialization (`:1055,1071`).

### The `NetState`-null paths, one at a time

- **Gumps and packets** — never sent, and nothing needed. Sends are null-safe on both engines.
- **The murder report** — `BotMurderReport.OnBotDeath` runs **before** `base.OnDeath`
  (`PlayerBot.cs:911`), walks `victim.Aggressors`, claims each entry with
  `ai.Reported = true; ai.CanReportMurder = false` and calls `PlayerMurderSystem.ReportMurder`
  directly (`BotMurderReport.cs:54-105`). The stock handler then finds nothing to ask about and no
  gump is queued into the void. **This is the shape of our F2 repair**, and it is worth reading in
  full before writing ours: the header explains why the flag is doing the real work — only a harmful
  *criminal* act sets it, so killing a red or a guild-war enemy is not murder, and healing to full
  clears it.
- **Trade** — `OpenTrade` → `BotTradeWindow.TryOpen` constructs `SecureTrade(player, bot)` and
  registers it on **the player's** `NetState.Trades` (`BotTradeWindow.cs:118-119`). This is the one
  that needed a ModernUO engine patch; ServUO does not ([§2.4](#24-be-traded-with--a-netstate-gate-on-a-virtual-method-and-servuo-needs-no-patch)).
- **Doors** — two halves. `PlayerBot.Move` opens the door the step failed on
  (`PlayerBot.cs:611-621`), and the **pathfinder** needed two separate engine patches
  (`INTEGRATION-NOTES.txt`, 2026-09-01 plus a 2026-09-03 follow-up) because both the cached bitmap
  and `MoveImpl.AlwaysIgnoreDoors` were gated on `BaseCreature`. Their follow-up note is the useful
  one: the first patch fixed cache hits and dungeon interiors kept failing, because a cache **miss**
  falls through to the real per-direction `CheckMovement`, which reads the statics. We have the same
  gate at `FastAStarAlgorithm.cs:77,93`.
- **Skill gain** — **they have none.** There is no `SkillCheck`, no `CheckSkill` and no skill
  mutation anywhere in their bot source; their crafting is a `MakeChance` roll and their mining is
  `new IronOre(2..6)` on a timer (README, **The yield is real, and upstream's was not**). So
  "`PlayerMobile` gives player-equivalent progression" is not a claim the reference supports — it is
  a claim only *our* shard is in a position to make, because only ours drives the real engines.
- **Stamina and regen** — `ShouldCheckStatTimers => false` (`PlayerBot.cs:585`). Worth copying, but
  worth less than it looks here: in ServUO that virtual only guards the deserialize path
  (`Mobile.cs:6205`), while the `Hits` / `Stam` / `Mana` setters call `CheckStatTimers` for every
  mobile regardless (`Mobile.cs:9718,9747,9774`).
- **Logout** — there is none. The session manager deletes the mobile.

### Guilds, houses and vendors: the three the reference does not answer

- **Their bot guilds are not guilds.** `BotGuilds.cs:1-16` is an explicit fake — a static catalog of
  tags rendered through `ApplyNameSuffix` — and the header gives the reason: real guilds "are
  account-backed, serialized entities; bots are transient". Real `Server.Guilds.Guild` membership
  exists **only** when a *player* recruits a bot, and their shard runs the **old** guild system
  (`BotGuildRecruits.cs:4`, "Core.SE is off"), so they watch the guild's `Accepted` list — a route
  that does not exist on our new-guild EJ shard, where the block is the target cast at
  `GuildRosterGump.cs:206`.
- **A recruited bot becomes permanent.** `GuildBound` / `IsPermanent` (`PlayerBot.cs:166-168`),
  serialized at v7 (`:1068,1136`), and every deletion path checks it (`BotStartupManager.cs:106`,
  `BotSessionManager.cs:228`, `GenerateBotsCommand.cs:174`, `ClearSpawnersCommand.cs:47`). **Guild
  membership and ephemerality are the same decision upstream too** — worth knowing before we wire
  guilds here.
- **They own no houses.** `BotHousing.cs:1-31` places *ownerless, ageless* scenery through a
  throwaway Player-level probe, then clears `Owner` and sets `RestrictDecay` — and needed a
  `BaseHouse.cs` patch to stop stock reaping ownerless houses ten seconds after load. That is
  decoration, not ownership. **The reference has no answer to "a bot buys and owns a house".**
- **They run no player vendors.** `PlayerVendor` appears nowhere in their bot source. Their economy
  is bank hawkers holding one real item (`BotShop.cs`), haggling by speech, and the real trade
  window.

### What still breaks for them, and one correction to our own docs

Their bots stay red forever for the same `GameTime` reason ours would
([§2.2](#22-pk-and-murder--the-player-flag-raises-it-playermobile-handles-it-the-facet-permits-it)),
they are never banished from a protected facet, and they took a login-killing crash from a corpse
with a null equip list that only bots could produce (`INTEGRATION-NOTES.txt`, 2026-09-01).

And one thing this folder's README currently gets wrong. Its **Ephemerality** section says ModernUO
writes player characters through their account, so an accountless bot "was never in the world save at
all — upstream got the guarantee for free". Their own `BotStartupManager.PurgeStaleBots`
(`:92-119`) sweeps `World.Mobiles` at boot and deletes every non-permanent `PlayerBot`, with the
comment *"any that appear in a fresh load are stale remnants and must be cleared so they don't
accumulate"*. **They pay for the guarantee too** — with a boot purge where we use
delete-on-deserialize. Recorded here rather than fixed, because this spike changes no files but its
own; it belongs in the next documentation pass.

---

## 5. What exists only because bots are `BaseCreature`s

| Mechanism | Where | Under `PlayerMobile` |
| --- | --- | --- |
| `BotAI : VendorAI` — `DoActionWander` stand-aside, `TransformMoveDelay` passthrough | `BotAI.cs:37,55,86` | **Vanishes.** Both overrides exist only to fight `BaseAI`/`VendorAI`; there is no AI underneath a `PlayerMobile` |
| `ForcedAI` | `PlayerBot.cs:1051` | **Vanishes** |
| `CheckIdle` — the fidget fix | `PlayerBot.cs:683` | **Vanishes.** No `BaseAI.DoActionWander`, no `WalkRandomInHome`; 11,925 idle steps were never ours to begin with |
| `IdleTolerance`, `PickScatteredHome`'s reachability guard | `PlayerBot.cs`, `BankSitterBehavior` | **Re-port.** The scattered *spot* still matters (it keeps arrival tiles clear); the walk-back becomes ours instead of the engine's pin idiom |
| `OnAfterSpawn` untether — `Home` / `RangeHome` / `IdleTolerance` back to zero | `PlayerBot.cs:737-760` | **Vanishes.** `XmlSpawner2.cs:9318` sets `Home` and `RangeHome` only `if (m is BaseCreature)`. `Mobile.OnAfterSpawn` (`Mobile.cs:9696`) still fires, so the population seed path survives untouched |
| **MODIFICATIONS entry 5** — the `BaseCreature.OnMoveOver` guard | `BaseCreature.cs:4546` | **Vanishes.** A `PlayerMobile` mover misses the `m is BaseCreature && !Controlled` branch (`:4564`) and falls to `Mobile.OnMoveOver` → the mover's `CheckShove` (`Mobile.cs:3506`), which on Trammel passes unconditionally because `TrammelRules` includes `FreeMovement` (`Map.cs:128`, `Mobile.cs:3518`). **One upstream edit deleted** |
| `BotShove.OnMoveOver`'s mover branch, and `IBotMover` | `BotShove.cs:75`, `NavWalkFailures.cs:67` | **Re-port, narrowed.** Only the walk-audit probe still needs the mover branch; the reverse half (`ConsentsToBotPass`) moves into `PlayerBot.OnMoveOver` over `PlayerMobile`'s rather than `BaseCreature`'s — same behaviour, different base |
| `MayBotPass`, `Describe`, and the rung log's occupant kind | `NavWalkFailures.cs:426,511`; `NavWalker.cs:1606` | **Re-port, inverted — the highest-risk item here.** All three encode *`PlayerMobile` means a real player*. `MayBotPass` refuses exactly one thing, a live `PlayerMobile` (`:432-444`), and `Describe` answers "player" to `mobile is PlayerMobile \|\| mobile.Player` (`:511`). The day bots are `PlayerMobile`s, the diagnostic refuses its own fleet and labels every bot a player. Every test must ask `is PlayerBot` first. **CLOSED, 12 September 2026** - `MayBotPass` is re-derived against `ConsentsToBotPass` and `DescribeMobiles` now calls `Describe` rather than carrying its own copy; the tree-wide sweep that checks the discipline is below, under *The sweep that closes "every test must ask `is PlayerBot` first"* |
| `Bots.Shove` — whose stand-in for a link-dead player **is** an accountless `PlayerMobile` | `BotShoveProbe.cs:27,85` | **Re-port.** The stand-in and the subject become the same class and must be told apart by type. It is also this tree's existing proof that an accountless `PlayerMobile` constructs, enters the world and behaves |
| `InitialInnocent`, `CanTeach`, `CanBeRenamedBy`, context-menu suppression | `PlayerBot.cs:597,603,582` | **Vanish.** `Notoriety.cs:441` and `BaseCreature.GetContextMenuEntries` (`:4574`) are `BaseCreature` surfaces; a `PlayerMobile` reads blue and shows a player's menu with nothing done |
| `ApplySkills` zeroing, to undo the pet-training stamp | README **The engine writes skills behind you**; `BaseCreature.cs:625-632` | **Vanishes.** `ChangeAIType` never touches a `PlayerMobile`, so nothing spends 80 points of the skill budget on `Focus` and `DetectHidden` behind us |
| `SetWearable` — `CheckEquip` then `PackItem` the loser | `EquipmentTable.cs:1590,2158`; `BaseCreature.cs:5706` | **Re-port, and it is mandatory.** `Mobile.AddItem` logs a layer conflict and then equips anyway (`Mobile.cs:6779,6807`), so the wrapper is the only thing standing between us and 107 ghost items. About thirty call sites already funnel through two helpers |
| Pace — `CurrentSpeed`, `ActiveSpeed`, `PassiveSpeed` | `BotMovement.cs:100-106,263` | **Re-port.** Becomes our own field, read by the locomotion adapter's step clock |
| **Step-level recovery** — a blocked step turns up to twice and retries in the turned direction, and `Direction` is assigned before the step so a heading change is not spent instead of one | `BaseAI.DoMoveImpl:2483-2499` and `:2346-2347`, reached through `BaseAI.MoveTo` | **Re-port — and this row is written after the fact, because it was the one the table missed.** The `INavActor` extraction took `DoMoveImpl`'s *tail* (run flag, `Move`, step clock) and left the rest, so a bot had no auto-turn, no `CheckMove` gate and no pre-step `Direction`. Nothing caught it until `[WalkAudit` grew a second probe class, which is the argument for the two-class instrument in one line. Ported 12 September 2026; the clause-by-clause account is in the Navigation README under *The port, and what it left standing* |
| `PlayerBot.OnThink` — the party-invite poll | `PlayerBot.cs:976`; called only from `BaseAI.cs:2204,3082` | **Re-port.** Moves to `BotTickManager`, as `REVIEW.md` §3 already asks |
| Pack animals — `SetControlMaster`, `Rider`, follower slots | `BotPackAnimal.cs:177`, `BotMovement.cs:480,489` | **Unchanged.** All take a plain `Mobile` |
| Ephemeral delete-on-deserialize | `PlayerBot.cs:1086` | **Unchanged.** `REVIEW.md` is right that this is policy, not inheritance — changing the base class still deletes the bot on reload |
| `LiveMapSnapshot.KindOf` | `LiveMapSnapshot.cs:525-534` | **Unchanged.** Already tests `is PlayerBot` before `mobile.Player`, which is exactly the discipline the rest of the diagnostics now need |
| `CheckShove`, `HandlesOnSpeech`, `OnSpeech`, `OnLocationChange`, `OnDeath`, `OpenTrade` | `PlayerBot.cs:619,995,1012,644,701` | **Unchanged.** All virtual on `Mobile` |
| **Doors, in the pathfinder** | `FastAStarAlgorithm.cs:77,93` | **A new upstream edit is required.** `MoveImpl.AlwaysIgnoreDoors` is set only when `p as BaseCreature` is non-null and is reset to `false` after every `GetSuccessors` call (`:102`), so no `Custom/`-side assignment survives a single loop iteration. Upstream hit this twice. **Net MODIFICATIONS count is unchanged: entry 5 out, a door entry in** |

### The sweep that closes "every test must ask `is PlayerBot` first"

*12 September 2026, the collision vocabulary session.* The table above set that discipline and the
row for `MayBotPass` called it **the highest-risk item here** — *"the one most likely to go green
while being wrong"*. A discipline is only checkable if somebody has actually walked the tree, so
here is the walk: **every place under `Scripts/Custom/` that tests a mobile's kind**, across 145
`.cs` files, in one of three states.

The fact that makes the sweep necessary, in one sentence: a `PlayerBot` **is** a `PlayerMobile`,
**does** set `Mobile.Player = true` (for the party gate), and has **no `NetState`** — so
`is PlayerMobile`, `as PlayerMobile` and `.Player` all match a bot, and `NetState == null` matches
both a bot and a link-dead human.

#### (a) Already asks the bot interface first — no change

| Site | What it decides |
| --- | --- |
| `NavWalkFailures.Describe:618` | `IBotActor`, then `IBotMover`, then `IDailyLifeActor`, then player. Every test is a type |
| `NavWalkFailures.MayBotPass` | re-derived this session; asks the occupant's override family, then the mover |
| `BotPathPolicy.IgnoreDoors:68` | `p is IBotActor` — the door gate |
| `LiveMapSnapshot.KindOf:533` | `is PlayerBot` **before** `.Player`, so a bot does not draw as an account |
| `BotSpeechResponder.cs:119` | `!(speaker is PlayerMobile) \|\| speaker is PlayerBot` — the echo-chamber guard |
| `BotSession.cs:386-387` | `is PlayerMobile && !(is PlayerBot) && .Player` — is a real human near |
| `PlayerBotBehavior.cs:450-451` | the same three-part test, and both halves are load-bearing |
| `VocabularySnapshot.cs:155` | `typeof(PlayerBot)`, deliberately not `typeof(PlayerMobile)` |
| ~200 further `as PlayerBot` sites | the bot layer asking about its own kind; `as` plus skip, never a hard cast |

**REVIEW.md section 4's second half was already closed before this session.** It reported that
`Describe`'s *"`Player` plus null-NetState classification also labels a disconnected player as a
PlayerBot"*. That split is gone: `Describe:633` reads `mobile is PlayerMobile || mobile.Player` and
its comment says *"Connected or not, staff or not. A link-dead player is a player."* **No test
anywhere in `Scripts/Custom/` now uses Player-flag-plus-null-`NetState` to mean "bot"** — verified
by the sweep, and the negative results below are part of that verification.

#### (b) Fixed in this session

| Site | Was | Now |
| --- | --- | --- |
| `NavWalker.DescribeMobiles` | its own three-branch copy of `Describe`, whose middle test `other.Player && !(other is BaseCreature)` was the pre-swap formulation | calls `NavWalkFailures.Describe`. **One vocabulary instead of two**, and `NavWalker.cs` left `Nav.Actor`'s type-test ledger as a result |
| `RestrictedZoneSystem.ShouldWarn` | every test passed for a bot, so a bot would be counted down by a gump that goes nowhere and then jailed | excludes `IBotActor`. One chokepoint covers `OnEnter`, `OnResurrect` and the jail — `RestrictedZoneRegion.cs:55,69,104` needed no edit of their own |
| `JailSystem.GetRefusalReason:130` | `!player.Player` passes a bot, so `[Jail <bot>` would jail one | refuses `IBotActor` with its own message |
| `JailCommands` name lookup `:248` | walks `World.Mobiles` for `is PlayerMobile`, so `[JailInfo Elowen` could resolve to a bot of that name | skips `IBotActor` |
| `ResetQuestCommands` both targets | `targeted as PlayerMobile` accepted a bot | refuses `IBotActor` in each of the two target classes |
| `OldMarta.OnMovement:129` | `!(m is PlayerMobile)` let bots trigger her greeting **and spend its cooldown**, so the next real player got silence | excludes `IBotActor` |

Two of those are worth more than a row.

**`ResetQuestCommands` was a persistence leak, not a cosmetic one.** `PlayerMobile.Quests` is not a
field: the getter is `MondainQuestData.GetQuests(this)`, which **inserts an empty list for anyone it
is asked about**, and the save writes every entry it holds (CLAUDE.md §11). So targeting a bot
minted a quest-data entry for a mobile `BotStartupPurge` deletes at the next boot, orphaning it in
`Saves/Quests/MLQuests.bin` with nothing left to point at it.

**The restricted-zone guard is the minimum fix for a mobile that cannot see the warning, and is
NOT a ruling that bots are exempt from zones.** Whether a bot should be subject to them — turned
back at the boundary, walked out, or given some consequence that does not need a client — is an
**open owner question** and nothing in the code settles it. Measured before the guard was written:
**`restricted-zones.json` holds an empty zone list**, so there is no zone for a bot to enter, no
overlap with bot territory and no bot route that reaches one. This has never fired. It is a guard
against the day somebody authors a zone near a bot road, not a repair of something that happened.

#### (c) Proved not to need it, with the reason

- **Protected by their source rather than by a type test.** Anything fed from `NetState.Instances`
  — `AutoCollectSystem.cs:123`, `JailStatusSystem.cs:61`, `BotChatProbe.cs:430`,
  `NavWalker.cs:2528`, `NavigationSystem.cs:1088`, `DailyLifeSystem.cs:437`,
  `RestrictedZoneSystem.cs:498`, `RequestPoller.cs:1099` — enumerates the online client list, and a
  bot has no `NetState`, so it cannot appear. Likewise anything gated on `NetState != null` before
  it acts: `ReportGump.cs:165`, `JailCommands.cs:49`, `JailStatusGump.cs:54`,
  `RestrictedZoneCountdownGump.cs:77`, `NavigationCommands.cs:588`, `NavWalkAudit.cs:1257`.
  **Recorded rather than changed — but recorded, because the protection is incidental.** Each of
  those is one refactor away from being a bug, and the sweep is where that is written down.
- **Intentional: the site wants a bot to qualify, and says so.** `BankSitterBehavior.cs:576`
  (`!mobile.Player` skips non-bots — bots deliberately pass), `BotDeathProbe.cs:173` (mirrors
  `ReportMurderer`'s own flag test, which is the point of the probe), `BotSmoke.cs:621` (asserts the
  flag is set so party invites work), `LiveMapSnapshot.cs:312` (`!mobile.Player` skips bots in the
  sector sweep because `KindOf` has already added them).
- **Unreachable by construction.** `ResetQuestCommands.cs:229`'s `state as PlayerMobile` is the
  confirm-gump callback, reached only after the target it came from has already refused a bot.
  `NavActor.cs:540`'s `mobile as PlayerMobile` is the factory choosing an adapter, and a bot
  landing there is correct — that branch exists for it.
- **Access-level tests are not kind tests.** `AccessLevel.Player` in a `CommandSystem.Register`
  call or an `AccessLevel > Player` staff check answers a different question and is left alone
  throughout.

#### Negative results, which are part of the answer

- `HasNetState` — **zero** occurrences under `Scripts/Custom/`.
- `.Account == null` / `!= null` — **zero**. The only `.Account` read is `ConsoleTap.cs:264`, for a
  username in a log line, on a path a bot cannot reach.
- `is BaseCreature` as a *runtime* test — **zero**; every `BaseCreature` test in `Custom/` is the
  `as` form, which is what makes `Nav.Actor`'s regex ledger countable.
- `Controlled` as a test — **zero** in `Custom/`. Its only occurrences are probe construction
  (`NavAudit.cs:477`, `NavMovement.cs:127`, both `Controlled = true` so the probe is not refused as
  an uncontrolled creature) and `ControlMaster` use in the pack-animal code.

**The item in the table above is closed.** What replaces it is this list, and the rule it leaves
behind: a kind test in `Custom/` either asks an interface, or is protected by a source that cannot
carry a bot, or carries a comment saying why a bot belongs in its answer.

### The locomotion adapter

The coupling is shallow and enumerable, which is the good news. `NavWalker(BaseCreature)` at
`NavWalker.cs:216`, and the member set it actually uses is `Location`/`Map`/`X`/`Y`/`Z` (47 reads),
`Move`, `MoveToWorld`, `InRange`, `InLOS`, `CanSee`, `CheckAlive`, `Direction`, `Name`, `Deleted` —
all `Mobile` — plus exactly **four** `BaseCreature`-only touchpoints:

- `ForceStayHome` (`:325`) — the refusal warning
- `AIObject` (`:643`) and `ai.NextMove` (`:666,692`) — the step drive and its gate
- `CurrentSpeed` / `ai.TransformMoveDelay` (`:693-694`) — the pace sampler
- `Home` (`:2207`) — the pin

There are eight construction sites (`CrafterBehavior.cs:323,389`, `GathererBehavior.cs:565`,
`ShopperBehavior.cs:290`, `TravelerBehavior.cs:361`, `NavWalkAudit.cs:978`,
`DailyLifeTownsfolk.cs:144`, `ShopScheduleSystem.cs:347`), and **three of them are daily-life
`BaseCreature`s that must keep working**. That is why this is an interface with two implementations
and not a rewrite.

Shape: `INavActor` over *{ position and map; `TryStep(goal, run, range)`; when the next step is due;
`MoveToWorld(landing)`; deleted/alive; door policy; home and tether; cleanup }*.

- **The `BaseCreature` implementation** delegates to `AIObject.MoveTo` (`BaseAI.cs:2621`) and
  `ai.NextMove`. Pure extraction — parity should be exact, and it should be proved green before the
  class changes at all.
- **The `PlayerMobile` implementation** mirrors `BaseAI.MoveTo`'s own two-step structure
  (`BaseAI.cs:2650-2656`): try a direct `Move(GetDirectionTo(goal))` first, and only on refusal build
  a `PathFollower(mobile, goal)` — which takes a plain `Mobile` (`PathFollower.cs:18`) and needs
  nothing from `BaseAI`. The step clock becomes ours, fed from the pace field. `ForceStayHome` is
  false and `Home` is a no-op. **This shape is proved, not invented**: it is what upstream's Traveler
  does (`Behaviors/TravelerBehavior.cs:1963,2037`).
- **The probe must use the same adapter.** `NavWalkAudit.WalkAuditProbe` is a `BaseCreature`
  (`NavWalkAudit.cs:268`), and `REVIEW.md` §3 is explicit that matching one interface while keeping a
  different movement implementation "would recreate the old probe problem".

One thing the migration does **not** lose: `NavWalker` exists as a single shared timer partly because
`BaseCreature.PlayerRangeSensitive` freezes a creature's AI when nobody is watching
(`NavWalker.cs:56`). A `PlayerMobile` has no AI timer to freeze, so the shared-timer design is still
right — it just stops being a workaround and starts being the only mechanism.

**Estimate, in sessions** — a session being the unit that produced bot sessions 1–6 (`SHARD.md:92`):

| Step | Sessions |
| --- | --- |
| `INavActor` plus two implementations; rewire the eight sites; `BaseCreature` parity green *first* | 1 |
| The class swap: `PlayerBot : PlayerMobile`, delete the vanishing overrides, equipment wrapper, pace field, `OnThink` → tick | 1 |
| Invert the collision and diagnostic vocabulary; rewrite `Bots.Shove`; drop MODIFICATIONS entry 5 | 1 |
| The door patch, logged as a new MODIFICATIONS entry; re-green `Bots.Travel`, `Bots.Shift` and `[WalkAudit` | 1 |
| Rebaseline: the step census, `[BotPace`, `Bots.Recipe`'s tick cost, the full `[BotSmoke` and `[CoreSmoke` chains | 1 |
| **Back to today's green suite** | **≈5 (4–6)** |

Highest risks, in order: **doors**; the **diagnostic inversion**, which is the one most likely to go
green while being wrong; **equipment layers**; and unobserved movement.

---

## 6. Cost per bot on the game thread

**`PlayerMobile` is cheaper, and the mechanism is the timer thread rather than the callback.**

A `BaseCreature` always carries one `AITimer` (`BaseAI.cs:75,3046`) at `TimerPriority.FiftyMS`, with
its interval set to `CurrentSpeed` — which `BotMovement.Settle` drops to `Mobile.WalkFoot`, 400 ms
(`BotMovement.cs:100-106`). It cannot be suppressed: `NavWalker` drives movement through `AIObject`
(`NavWalker.cs:643`), so a null AI is not an option while the walker is what it is.

`Timer.TimerThread` walks **the whole `FiftyMS` bucket every 50 ms** (`Timer.cs:332-343`, with the
priority delays `{0,10,25,50,250,1000,5000,60000}` at `:136`) and takes `lock (m_Queue)` per enqueue
(`:354`). So N bots cost **20·N due-checks per second**, plus **N ÷ 0.4 callbacks per second** each
running `OnThink` → `Think` → `DoActionWander` → `CheckIdle`. At 60 bots that is 1,200 checks and
150 passes a second; at 400, 8,000 and 1,000.

Under `PlayerMobile` all of that is **zero**. Nothing in `PlayerMobile` starts a per-instance
recurring timer, and `OnThink` has no caller outside `BaseAI` (`BaseAI.cs:2204,3082`). Movement does
not get more expensive to compensate: `NavWalker` already drives the entire fleet from one shared
50 ms timer (`NavWalker.cs:81`), and the adapter keeps it that way.

**The dominant per-bot cost is not a class difference at all, and neither option changes it.**
`Sector.OnEnter` activates a 5×5 block of sectors — `SectorActiveRange = 2`, `SectorSize = 16`
(`Map.cs:397-399`, `:1653-1666`) — for **any** mobile with `mob.Player` set (`Sector.cs:148-155`).
Sixty Player-flagged bots therefore keep roughly 80×80-tile blocks of the world permanently awake,
with every stock vendor, guard and animal inside them running its own AI timer, and no real player
online anywhere. That is the **flag**, not the class. Any conversation about raising the population
should start here rather than at the behaviour stopwatch, which is `REVIEW.md` §5's point made from a
different direction.

Two smaller notes:

- **Regen timers are a wash.** `BaseCreature.ShouldCheckStatTimers => false`
  (`BaseCreature.cs:3074`) and upstream's `PlayerBot` overrides it to `false` too
  (`PlayerBot.cs:585`) — but in ServUO that virtual guards only the deserialize path
  (`Mobile.cs:6205`), while the `Hits` / `Stam` / `Mana` setters call `CheckStatTimers` for every
  mobile (`Mobile.cs:9718,9747,9774`). Override it, but do not count it as a saving.
- **Allocation is the one point against.** A `PlayerMobile` constructor allocates `m_VisList`,
  `m_PermaFlags`, `m_AntiMacroTable`, `m_RecentlyReported`, `m_JusticeProtectors` and
  `m_ChampionTitles` (`PlayerMobile.cs:4199-4214`) and joins the static `Instances` list (`:4185`),
  costing one O(n) `Remove` per bot death (`:5241`). Against 20·N timer-bucket checks a second this
  is noise. Watch one thing though: `PlayerMobile.Quests` *inserts* an empty list for anyone it is
  asked about and the save writes every entry it holds (`CLAUDE.md` §11) — nothing must sweep bots
  for quests.

---

## 7. The four options

### (a) Stay `BaseCreature`; build the account-dependent features through a bot-account helper

*Unlocks* nothing new, and keeps free doors, a working `NavWalker` and today's green suite.
*Breaks*: guilds stay impossible (`GuildRosterGump.cs:206`); F2 stays reproduced; the stat-total-cap
breach stays (`SkillCheck.cs:588`); coloured ore, granite and gems stay unreachable
(`Mining.cs:206,216,221,286`); the per-bot AI timer stays.
*Sessions to today's green suite*: **0.**
*Risk to flag*: the migration bill does not shrink by waiting, and by 7f it grows — you would be
migrating economy state alongside the class.

### (b) `PlayerMobile` + one shared bot `Account`

*Unlocks* what (d) unlocks, plus house limits and decay eligibility — for exactly one bot's worth of
property. *Breaks*: `AccountGold.Enabled` is true here (`CurrentExpansion.cs:20`), so **every bot on
the account shares one purse** (`Banker.cs:38,99`); `Account.Length` is five character slots; and
`BaseHouse`'s account limits would treat the entire fleet as one owner.
*Sessions*: ≈5.5.
*Risk to flag*: a shared purse is an economy-bug generator that stays invisible until money exists.

### (c) `PlayerMobile` + an `Account` per bot

*Unlocks* everything (d) does, plus genuine house ownership under the stock limit and decay rules.
*Breaks*: 60–400 login-capable accounts in `Accounts.xml`; and since a bot account never logs in,
`Account.Inactive` eventually flips (`Account.cs:508-519` — 180 days, or 30 if empty) and
`BaseHouse.DecayType:102` **condemns the house anyway** unless something deliberately refreshes
`LastLogin`.
*Sessions*: ≈6–7.
*Risk to flag*: paying the whole account-policy bill now, for property that nothing owns yet.

### (d) `PlayerMobile`, accountless now; an `Account` per persistent economic bot later — **chosen**

*Unlocks*: guilds become possible at all; F2's cast goes; coloured ore, granite, gems and sand;
the stat-total-cap breach closes; the per-bot AI timer goes; MODIFICATIONS entry 5 goes.
*Breaks*: the door patch becomes required; the collision and diagnostic vocabulary inverts; the whole
probe chain needs rebaselining; and **an accountless bot must be forbidden from owning anything
durable** — `BaseHouse.HandleDeletion` (`:3536`) will throw on the game thread the first time one
owns a house and is deleted, and the house would have been `Condemned` in any case.
*Sessions to today's green suite*: **≈5.**
*Risk to flag*: a two-step identity change is exactly the "choosing persistent identity too late and
migrating valuable state twice" that `REVIEW.md` §9 warns about. Mitigate it with a **written rule** —
no durable property without an `Account` — rather than with a second migration.

### Why (d), in one paragraph

The case is narrower than "the reference uses `PlayerMobile`", and narrower than `REVIEW.md` §3
implies. Parties, banking and being traded with all work today on a `BaseCreature`: the party gate is
the Player flag (`AddPartyTarget.cs:30`), the bank box is the Player flag (`Banker.cs:48`), and
ServUO's already-null-safe `SecureTrade` (`SecureTrade.cs:25-29`, `Mobile.cs:7403`) means a bot-side
trade window needs neither a class change nor an engine patch — a real divergence from ModernUO,
which had to patch it. What actually forces the move is a short list of things the class fixes
outright and nothing else can. **Guilds are a hard `PlayerMobile` gate on this shard**: the recruit
target is cast at `GuildRosterGump.cs:206` and a `BaseCreature` is answered "that isn't a valid
player", so one of the seven wants is unreachable by any amount of custom code. Beside it sit three
live defects the class closes — F2's cast (`ReportMurderer.cs:44`), the stat-total-cap breach
(`SkillCheck.cs:588`), and plain-ore-only harvesting (`Mining.cs:206`), which lands directly on 7f —
and the per-bot `AITimer`, the largest per-bot cost this decision is able to remove at all
(`BaseAI.cs:3046`, `Timer.cs:332-343`). The price is bounded and honest: one upstream edit traded for
another (`FastAStarAlgorithm.cs:93` in, `BaseCreature.cs:4546` out), a locomotion adapter whose shape
upstream already runs in production, and about five sessions back to green. Doing it before 7f and
accountless keeps the two genuinely account-shaped wants — houses and vendors — as one deliberate
decision taken later, once something is actually owned.

---

## 8. What could not be answered from the code

Stated rather than guessed:

- **Whether player anti-macro and GGS *should* apply to automation.** The code says a
  `BaseCreature` skips them; it cannot say whether that is a bug or the point. `REVIEW.md`'s
  "fairness" question.
- **What it means for a named bot to end a session** — offline, retired, or destroyed.
  `REVIEW.md`'s "identity" question, and the thing that decides whether guild-bound bots persist the
  way upstream's do (`PlayerBot.cs:166-168`).
- **The real per-bot cost.** [§6](#6-cost-per-bot-on-the-game-thread) is arithmetic read off the
  timer bucket, not a measurement. `REVIEW.md` §5's instrumentation plan still stands and should run
  after the F1 rebaseline, not before.
- **Whether the murder-report adapter should award counts for bot-on-bot kills.** Upstream makes it a
  switch and defaults it on (`BotMurderReport.cs:48`, `ReportBotKillers`). That is a shard policy
  question, not an engine one.
