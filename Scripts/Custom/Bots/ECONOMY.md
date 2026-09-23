# The bot economy — the transaction contract

**Written 23 September 2026, before the code (7f-1).** Every later 7f feature — a crafter selling
ingots to the NPC smith, a bot buying reagents, a player-facing bot shop, a house, a vendor wage —
goes through the one path this file describes. A feature that moves gold or goods between two
parties any other way is a bug in that feature, not an extension of this one.

Sean's decisions, 23 September 2026 (recorded in `CLASS-DECISION.md`; `REVIEW.md` owner question 3
is closed by them):

- **Gold's source and sink are the stock NPC shopkeepers, at player prices.** Nothing is invented,
  and nothing is minted or destroyed to make a scene play.
- **Bots trade with each other in the supply chain.** A gatherer delivers to a crafter and the
  crafter pays, at or a little above what the NPC would pay, so the walk is worth it.
- **Every bot, named or throwaway, starts with 10,000 gold at creation.** A throwaway's gold vanishes
  with it and is recorded as a loss, like its starting stash.
- **Named bots keep everything they bought across a restart;** throwaways lose it with the bot,
  recorded.
- **Coloured ore counts.** The whole ore family and the whole log family are carried,
  capacity-checked, ledgered and priced by colour.
- Player-facing bot shops are a later slice. Skill gain as a motive is a later slice.

---

## 1. The contract

A trade is **one atomic step**: the goods leave the seller, the gold leaves the buyer, the product
lands with the buyer, the gold lands with the seller, and the trade is written down — **or nothing
happens at all.** There is no intermediate state anybody can observe: no ore in flight, no gold
owed, no "paid but not delivered".

1. **One path.** `BotTrade.Quote` works out what would move; `BotTrade.Execute` moves it. Nothing
   else in the tree takes gold out of one bot's custody and puts it in another's.
2. **The buyer takes only what it can pay for** — the F5 rule, extended to gold. The accepted amount
   is `min(offered, room, affordable)`: what the seller has, what the buyer has room for, and what
   the buyer's gold covers at the quoted price. The remainder **stays with the seller**, in the
   stacks it was already in. A buyer with no gold accepts nothing, says so, and the seller keeps the
   whole load.
3. **Both parties' custody is updated in the same step.** The seller's goods and the buyer's gold
   are debited, the buyer's product and the seller's gold are credited, and both ledgers are told —
   `BotGoodsLedger` (units accepted) and `BotGoldLedger` (gold paid and received).
4. **Every step can be undone until the last one.** `Execute` validates everything first (the
   stacks are still there, the gold is still there, the buyer's pack will hold the product, the
   seller's pack will hold the coin), then moves in an order where each step records its own undo.
   A refusal or an exception at any step unwinds every step before it, in reverse. The one
   irreversible step — deleting the staged goods — comes last and cannot fail.
5. **The trade is recorded**: who (both parties, named or not), what (every stack, by concrete type
   and graphic), how much (offered, accepted, product), where (destination, map, x, y), when (UTC,
   boot id, persistence generation), and the price basis (worth, margin, price, the rule).
6. **Nothing is minted to cover a gap.** Upstream pays a gatherer out of nowhere when there is no
   buyer or the buyer is broke (`BotEconomy.cs:340-344`); here there is no trade, and the hauler
   keeps its load.

Outcome vocabulary, the same as the LoopQueue contract: a trade **completed** (everything moved),
was **refused** (nothing moved — no gold, no room, a pack that would not take the product), or
**rolled back** (something failed mid-way and every step was undone; nothing moved). There is no
fourth outcome. `trade-journal.jsonl` records all three.

## 2. Which gold a named bot uses: its pack

**The trade path spends and credits pack gold only, for every bot.** That is upstream's rule too —
`CrafterStock.SpendGold` reads the pack and nothing else (uo-offline `CrafterStock.cs:110-118`).
For a named bot it is also a decision, because a named bot has an `Account`, and on this shard
`AccountGold.Enabled` is true (`CurrentExpansion.cs:20`, EJ counts as TOL). Three reasons:

- **Account currency is a `double`.** `Account.TotalCurrency` is stored in platinum
  (`Account.cs:1597`) and `TotalGold` is `Math.Floor(frac × 1e9)` (`:1606-1609`). A deposit or a
  withdrawal can lose a coin to floating point. A ledger that is meant to balance to the coin cannot
  move money through it.
- **Pack gold never converts.** Gold becomes account currency only when it lands in a `BankBox`
  (`Gold.OnAdded`, `Gold.cs:91-121`). In a pack it stays an integer item stack, and the world save
  keeps it exactly as it keeps the named bot's goods.
- **`Banker.Withdraw` can create gold.** It checks the account-plus-physical balance but removes
  only physical gold (`Banker.cs:108-113`) — a stock bug that a bot calling it would exploit.

The census still **counts** bank gold, bank checks and account currency (rounded to the coin) in
`held`, so a named bot's whole wealth is visible. Nothing in 7f-1 moves gold into or out of a bank
box or an account. When a later slice does, it goes through this contract and names the rounding.

## 3. The price

**Data, not code — except the smelt ratio.** The prices, both ladders, the margin and the starting
purse are in `Data/Custom/bots.json`, section `economy`, validated at load (a ladder must name every
colour), with the stock files they came from cited in the section's `comment`. The smelt ratio is
code (`BotPrice`), because it is not a judgement: it is what `BaseOre` does to a pile of that
graphic, and a config that disagreed would price a load at one yield and hand the buyer another.

| Step | Ore | Log |
|---|---|---|
| **What an NPC pays a player** for the plain good | **No NPC buys ore**, so the base is the ingot it smelts into: `IronIngot` **3** gp. The smith lists 4 (`SBBlacksmith.cs:139`), but with the vendor economy on (`BaseVendor.cs:33`) `GenericSellInfo.GetSellPriceFor` pays `max(1, (int)(buy × .75))` against the buy entry of 5 (`SBBlacksmith.cs:34`; `GenericSell.cs:40-56`) — 3 | `Log` **1** gp (`SBCarpenter.cs:101`; no buy entry, so the list price stands) |
| **Smelt / cut ratio** | As `BaseOre` smelts, by the stack's graphic (`Ore.cs:385-398`): large `0x19B9` → **2** ingots, medium `0x19B8`/`0x19BA` → **1**, small `0x19B7` → **½** (pairs only; an odd one is left). Mined ore is 75% large (`Ore.cs:100-112`) | 1 log → 1 board |
| **Colour** | The stock collection ladder, `Bonnie.cs:70-78`: iron 1, dull copper 2, shadow iron 4, copper 8, bronze 12, gold 18, agapite 24, verite 31, valorite 39 | `Zorda.cs:69-75`: plain 1, oak 3, ash 6, yew 9, heartwood 12, bloodwood 24, frostwood 48 |
| **Margin** | **125%** of the load's total worth, rounded **up once** per trade | the same |

No stock NPC buys or sells any coloured ore, ingot, log or board; the collection ladder is the only
per-colour number in stock, so it is used as a ratio on the plain NPC price (Sean, 23 September).

The arithmetic is integer, in **half-gold**: an ore unit is worth `halfIngots(graphic) × ingotPrice
× ladder[colour]` (large 4, medium 2, small 1 half-ingots), a log `2 × logPrice × ladder[colour]`;
`price = ceil(worthHalf × marginPercent / 200)`.

| Load | Worth | Price |
|---|---|---|
| 60 large iron ore | 360 | **450** |
| 60 plain logs | 60 | **75** |
| 4 large dull copper + 2 medium valorite + 2 small iron | 285 | **357** |
| 60 large valorite ore | 14,040 | 17,550 — more than a fresh crafter has, so it takes what 10,000 buys |

**Why a quarter over, rounded once.** "At or a little above the NPC price so the walk is worth
it." Rounding per unit would turn a small ore's 1.5 gp into 2 — a third over on its own; rounding
once per trade keeps the margin the margin.

**The buyer receives what it paid for.** Ore becomes the smelt yield in ingots **of its own
colour** (`BaseOre.GetIngot()`); a log becomes one board of its own colour
(`CraftResources.GetInfo(resource).ResourceTypes[1]`). The crafter's `StockCap` (250) counts the
whole ingot or board family, so a load is also capped by room, in products.

## 4. The books

### Gold

```
in   = opening + starting + received + appeared
out  = paid + lost + vanished
held = a census of every bot's pack gold, bank gold and bank checks, and account currency,
       for every live bot and every OFFLINE named bot (re-read, not remembered)

in == out + held,   and appeared == vanished == 0
```

- **opening** — gold on the bots the world save handed this boot: a throwaway's, before the boot
  purge destroys it; a named bot's, which then stays.
- **starting** — the 10,000 each bot is given at creation. Spent first, like the starting stash, so
  a loss record says how much of it was never used.
- **received / paid** — the two halves of a trade, written only by `BotTrade`. A bot-to-bot trade
  adds the same number to both and leaves `held` unchanged; an NPC sale (a later slice) will be
  received with no matching paid, which is exactly what a source looks like.
- **lost** — gold destroyed with a bot, by reason (`boot-purge`, `death`, `logout`, `reclass`,
  `spawn-refused`, …), split **throwaway** from **named**. A named bot's lost should stay 0.
- **appeared / vanished** — the census compared a bot's actual gold against what the ledger was told
  and found a difference. Gold has no unrecorded legitimate source (ore does — mining), so both are
  alarms. Either being non-zero fails `Bots.Gold`.

Losses are written to `Data/Live/gold-lost.jsonl` (append-only, flushed each census and each save,
rotated at 4 MB), like `goods-lost.jsonl`.

### Goods

`BotGoodsLedger` is unchanged in shape. It now counts the **families** (`BaseOre`, `BaseLog`), so a
dull copper ore is a unit like an iron one, and a loss record names the concrete type
(`ValoriteOre`). A trade's accepted units are its `accepted`, as a free hand-over's were.

### The trade journal

`Data/Live/trade-journal.jsonl`, one JSON object per line, append-only, flushed each census and each
save, rotated at 4 MB:

```
{ "utc", "bootId", "generation", "trade",                      // when, and which boot wrote it
  "outcome": "completed" | "refused" | "rolled-back", "reason",
  "seller": { "name", "serial", "class", "named" },
  "buyer":  { "name", "serial", "class", "named" },
  "destination", "map", "x", "y",
  "goods": [ { "type", "itemId", "units", "product", "count" } ],
  "offered", "accepted", "worthHalf", "marginPercent", "price",
  "basis": "npc ingot 3 x smelt x ladder, margin 125%",
  "sellerGold": [before, after], "buyerGold": [before, after] }
```

`bootId` and `generation` are there because the journal is a file and the world is a save: a
trade written after the last completed save and before a crash is in the journal but not in the
world. A reader discards lines whose generation the next boot did not verify.

## 5. What upstream does, and where we do not

uo-offline @ `7f38c7c`, `playerbots/source/CustomBots/`.

- **`BotEconomy.DeliverMaterials`** (`BotEconomy.cs:273-351`) deletes the load first (`TakeYield`,
  356-379), prices it `hauled × RandomMinMax(2,4)` (:314), takes the gold from the crafter's pack
  (`CrafterStock.SpendGold`, :315-316), mints `new Gold(price)` for the gatherer (:324) and refines
  one-for-one (:325). It pays for the whole haul even past the stock cap, and when there is no buyer
  or the buyer is broke it **pays the gatherer anyway** (:340-344).
- **`BotBanking`** spends from the pack and tops up from the bank only in the speech path
  (`Settle` 219-249, `CoverInPack` 186-213). No account gold.
- **`BotShop` / `BotShopDeal`** — hawkers with invented price rows (`BotShop.cs:134-212`), paid by
  `SpendGold` then `new Gold(price)` (`BotShopDeal.cs:361-378`).
- **`CrafterProfiles`** sells nothing. `CrafterBehavior.SellOff` (598-630) deletes pieces for
  `RandomMinMax(15,40)` minted gold; `DrySpell` (270-301) restocks at 2 gp a unit from nowhere.
- **Starting gold** is tier-scaled, 40–1400 ±35%, in the pack (`EquipmentTable.cs:54-67`), with
  crafter extras (`CrafterProfiles.cs:133,184,232`).
- **No ledger, and no data file sets a price.**

**The named seams:**

1. **Nothing is minted.** A trade moves gold between packs; the pay-anyway branch is gone.
2. **The hand-over settles what is paid for**, and the remainder stays with the seller.
3. **10,000 starting gold for every bot**, replacing the tier table.
4. **Prices from stock NPC data**, not dice.
5. **Colour is carried and priced;** upstream only ever sees `IronOre` and `Log`.
6. **Ore refines at the stock smelt ratio**, so the crafter receives what it paid for.
7. **Pack gold for a named bot, account currency counted but untouched** (section 2).

## 6. Known consequences of doing only this much

- **Crafters only buy until 7f-2**, where they sell ingots to the NPC smith. A fresh crafter
  affords about 22 loads of large iron; one full load of valorite empties it; a broke bench still
  draws haulers, who keep their load and offer it again elsewhere.
- **Coloured stock counts against the cap, but crafting still uses the plain material.** A smith
  holding 250 coloured ingots accepts nothing more until a later slice spends them.
- **The purse weighs 67 stones** (`Gold.cs:34-40`, ML weight). A full miner was already over its
  limit before this slice — 60 large ore is 720 stones (tiledata weight 12) against a novice
  miner's `MaxWeight` of 310 (`PlayerMobile.cs:1101`) — because harvest drops merge into a stack
  and a merge skips `CheckHold`. Nobody is overloaded at birth; a fixture says so.
