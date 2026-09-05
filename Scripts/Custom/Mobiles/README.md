# Custom/Mobiles

Custom NPCs. Quest givers derive from `MondainQuester` and bind their quests with
`public override Type[] Quests => new[] { typeof(SomeQuest) };` — ServUO has no central quest
registry. See CLAUDE.md §11.

| File | What it is |
| --- | --- |
| `OldMarta.cs` | A fishwife in Britain Trammel offering `MartasFishRequest` |

## Old Marta

Spawned by `Spawns/Custom/trammel/GG_OldMarta.xml` at (1475, 1645, 20) on Trammel. Import with
`[GG_Reimport`.

Ported from the ModernUO shard, where she was a `BaseCreature` using `CanShout` / `Shout`.

### `MondainQuester` is a `BaseVendor` — but needs almost no suppression

`MondainQuester` already overrides `IsActiveVendor => false`, which alone kills `VendorBuy` and
`VendorSell` (both return silently on `!IsActiveSeller` / `!IsActiveBuyer`, so "vendor buy" is a
no-op rather than an error) **and** the entire `BaseVendor.AddCustomContextEntries` block — Buy,
Sell, Bulk Order Info, Bribe and Claim Rewards. `InitSBInfo()` is empty, `OnDoubleClick` is
overridden to open the quest gump instead of a paperdoll, and `IsInvulnerable => true` /
`CanBeDamaged() => false` come free.

The one live side effect is **`CanTeach`**, which `MondainQuester` inherits as `true`. That makes
`BaseCreature.GetContextMenuEntries` add a Teach entry for every skill at 60.0 or above and lets
`BaseAI` answer `*train*` / `*teach*`. Marta sets no skills so nothing shows today; she overrides
`CanTeach => false` so that stays true if anyone later gives her one.

### Stationary and invulnerable

`CantWalk = true` in `InitBody`. There is no `SetSpeed` in ServUO, and `RangeHome = 0` alone does
not stop an `AI_Vendor` wandering. `MondainQuester.Serialize`/`Deserialize` mirror `CantWalk` onto
`Frozen`, so a freshly spawned NPC is `CantWalk` only and a reloaded one is both — upstream
behaviour, and `CantWalk` is what actually does the work.

`InitBody` is written out in full rather than chaining to base: `MondainQuester.InitBody` only
rolls hair and skin hue — it sets neither `Body` nor stats.

### Greeting — why `OnMovement` is overridden wholesale

ServUO has no `CanShout` / `Shout` / `ShoutRange`. The nearest idiom is
`MondainQuester.OnMovement` calling `Advertise()`, and it is wrong here in two ways:

1. It speaks **publicly and unconditionally**, whether or not the NPC has anything to offer.
2. Its cooldown is a single private `m_Spoken` field **shared by every player**, so one passer-by
   silences the NPC for everyone for a minute.

So `OldMarta` overrides `OnMovement` and does not chain to base (the shape stock
`Scripts/Quests/CloakOfHumility/Mobiles/Gareth.cs` uses; base's only other job is
`AutoTalkRange`, which is `-1`/off). It keeps the edge trigger and the LOS test, adds a
**per-player** cooldown map, and greets with `SayTo` only when she could actually still offer the
quest.

The availability test is hand-rolled from `QuestHelper.HasQuest` and
`QuestHelper.GetRestartInfo` rather than `QuestHelper.RandomQuest`, which constructs a `BaseQuest`
on every call — this runs on movement.

`GreetRange` (5) and the one-minute cooldown are judgement calls, not ported constants.

### Speech — the perception-range trap

`HandlesOnSpeech` **must** be overridden to hear "help" at three tiles.
`BaseCreature.HandlesOnSpeech` ands the AI's answer with
`from.InRange(this, RangePerception)`, and `BaseVendor`'s constructor passes a perception of
**2** — so without the override she is deaf at three tiles even though `VendorAI` claims four.

`OnSpeech` matches the text with `Insensitive.Contains` rather than a speech keyword: client-side
keywords are empty on ASCII clients and there is no stock `*help*` id. It always chains to
`base.OnSpeech`, because swallowing the event breaks the AI's own speech handling.
