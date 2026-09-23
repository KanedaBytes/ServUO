// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotGoldLedger.cs - where the gold went, beside BotGoodsLedger and shaped like it.
//
// 7f-1 (Sean, 23 September 2026; ECONOMY.md section 4 is the contract). Upstream keeps no books at
// all: gold is minted for a sale, a haul or a restock and destroyed for a supply run, and nothing
// asks whether the totals agree. Here nothing is minted, so the totals must agree, and this file is
// what makes that checkable.
//
// THE EQUATION, over every coin a bot holds:
//
//     in    = opening + starting + received + appeared
//     out   = paid + lost + vanished
//     held  = a census of every live bot's pack gold, bank gold and bank checks, and account
//             currency - plus every OFFLINE named bot's, re-read from the bot, never remembered
//
//     in == out + held,   and appeared == vanished == 0
//
// opening   gold on bots the world save handed this boot: a throwaway's, counted in before the
//           boot purge destroys it; a named bot's, which then stays.
// starting  the purse every bot is born with (economy.startingGold). Spent FIRST, like the goods
//           ledger's stash, so a loss record says how much of the purse was never used.
// received, paid   the two halves of a trade, written only by BotTrade. A bot-to-bot trade adds the
//           same number to both and leaves held unchanged; an NPC sale in a later slice is received
//           with no matching paid, which is exactly what a source looks like.
// lost      gold destroyed with a bot, by reason, split THROWAWAY from NAMED (Sean: a throwaway's
//           gold vanishes with it and is recorded; a named bot keeps everything).
// appeared, vanished   the census found a bot holding more, or less, than it was told. Gold has no
//           unrecorded legitimate source - ore does, which is mining - so both are alarms and
//           either one fails Bots.Gold.
//
// THE OFFLINE NAMED BOTS ARE A SET OF BOTS, NOT A BUCKET OF NUMBERS. The goods ledger remembers
// what a named bot held when it logged out; this one keeps the bot and re-reads it at every census.
// A named bot offline is a persisted mobile on Map.Internal with a real pack, bank box and account,
// so there is nothing to remember - and a change made while it was offline is caught rather than
// carried.
//
// WHY PACK GOLD IS WHAT MOVES and account currency is only counted: ECONOMY.md section 2.
// Account.TotalCurrency is a double, and a ledger that balances to the coin cannot route money
// through one.

using System;
using System.Collections.Generic;
using System.Text;

using Server.Accounting;
using Server.Items;

namespace Server.Custom
{
    public static class BotGoldLedger
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        public const string LossPath = "Data/Live/gold-lost.jsonl";

        /// <summary>A fixture's own bots and purses, so their records are obvious in the journal.</summary>
        public const string ReasonFixture = "fixture";

        private static readonly BotJournalFile _losses = new BotJournalFile(LossPath, BotGoodsLedger.MaxBytes);

        // ---- the ledger ----

        public static long Opening { get; private set; }
        public static long Starting { get; private set; }
        public static long Received { get; private set; }
        public static long Paid { get; private set; }
        public static long Lost { get; private set; }
        public static long LostThrowaway { get; private set; }
        public static long LostNamed { get; private set; }

        /// <summary>Of <see cref="Lost"/>, the part that was still the starting purse.</summary>
        public static long LostStarting { get; private set; }

        public static long Appeared { get; private set; }
        public static long Vanished { get; private set; }

        public static int Records { get; private set; }

        /// <summary>Bots given the starting purse this boot, and named bots given it late (once, ever).</summary>
        public static int Grants { get; private set; }
        public static int LateGrants { get; private set; }

        /// <summary>The last loss record, as written. Fixtures assert on the JSON itself.</summary>
        internal static string LastRecord
        {
            get { return _losses.Last; }
        }

        /// <summary>reason -> { throwaway, named }.</summary>
        private static readonly Dictionary<string, long[]> _byReason =
            new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);

        // ---- trades, as BotTrade reports them ----

        public static int TradesCompleted { get; private set; }
        public static int TradesPartGold { get; private set; }
        public static int TradesPartCap { get; private set; }
        public static int TradesRefusedGold { get; private set; }
        public static int TradesRefusedOther { get; private set; }
        public static int TradesRolledBack { get; private set; }

        // ---- the census ----

        private static readonly Dictionary<Serial, PlayerBot> _offline = new Dictionary<Serial, PlayerBot>();

        public static long Held { get; private set; }
        public static long HeldPack { get; private set; }
        public static long HeldBank { get; private set; }
        public static long HeldAccount { get; private set; }
        public static long OfflineHeld { get; private set; }
        public static int OfflineBots { get; private set; }
        public static int Censused { get; private set; }

        public static long OpeningAtCensus { get; private set; }
        public static long StartingAtCensus { get; private set; }
        public static long ReceivedAtCensus { get; private set; }
        public static long PaidAtCensus { get; private set; }
        public static long LostAtCensus { get; private set; }
        public static long LostThrowawayAtCensus { get; private set; }
        public static long LostNamedAtCensus { get; private set; }
        public static long AppearedAtCensus { get; private set; }
        public static long VanishedAtCensus { get; private set; }

        public static DateTime LastReconcileUtc { get; private set; }
        public static bool HasReconciled { get; private set; }

        private static bool _hooked;

        // ---- wiring ----

        public static void Configure()
        {
            if (_hooked)
            {
                return;
            }

            _hooked = true;

            EventSink.WorldSave += delegate { Flush(); };
        }

        public static void Initialize()
        {
            HealthCheck.Register("Bots.Gold", BuildHealthResult);

            // Deferred past ServerStarted for the goods ledger's reason: the population fill puts
            // its bots in the world first. Each bot is on the books from its constructor anyway,
            // so an early census would be right - just about fewer bots.
            Timer.DelayCall(TimeSpan.FromSeconds(20.0), BotGoodsLedger.ReconcileInterval, Tick);
        }

        private static void Tick()
        {
            try
            {
                Reconcile();
                Flush();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "The gold census faulted.");
            }
        }

        public static void Flush()
        {
            _losses.Flush();
            BotTrade.Journal.Flush();
        }

        // ---- what a bot holds ----

        /// <summary>Gold in the pack, anywhere inside it. Never mints a container.</summary>
        public static int PackGold(Mobile m)
        {
            Container pack = m == null ? null : m.Backpack;

            return pack == null ? 0 : pack.GetAmount(typeof(Gold), true);
        }

        /// <summary>Gold and bank checks in the bank box. FindBankNoCreate: a census never mints a box.</summary>
        public static long BankGold(Mobile m)
        {
            BankBox box = m == null ? null : m.FindBankNoCreate();

            if (box == null)
            {
                return 0;
            }

            long total = box.GetAmount(typeof(Gold), true);

            foreach (BankCheck check in box.FindItemsByType<BankCheck>(true))
            {
                total += check.Worth;
            }

            return total;
        }

        /// <summary>
        /// Account currency, in gold, rounded to the coin. Only a named bot has an account, and
        /// nothing in 7f-1 moves money into one - this is counted so a named bot's whole wealth is
        /// in `held`, not because the trade path touches it (ECONOMY.md section 2).
        /// </summary>
        public static long AccountGold(Mobile m)
        {
            var account = m == null ? null : m.Account as Account;

            if (account == null)
            {
                return 0;
            }

            return (long)Math.Round(account.TotalCurrency * Account.CurrencyThreshold);
        }

        public static long GoldOf(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return 0;
            }

            return PackGold(bot) + BankGold(bot) + AccountGold(bot);
        }

        // ---- the sources ----

        /// <summary>
        /// The purse every bot is born with. Dropped into the pack - DropItem, which never refuses,
        /// and never AddToBackpack, which drops at the bot's feet on a refusal and there is no map
        /// yet in a constructor to drop it on. One stack; BotEconomyConfig caps it at 60,000.
        /// </summary>
        public static void GrantStartingGold(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Backpack == null || bot.StartingGoldGranted)
            {
                return;
            }

            int amount = BotSystem.Store.Economy.StartingGold;

            bot.StartingGoldGranted = true;

            // An instrument - the walk audit's probe - is not a person and owns nothing. Asked here
            // because this runs in PlayerBot's constructor, before a subclass could take a purse
            // back (PlayerBot.IsInstrument).
            if (amount <= 0 || bot.IsInstrument)
            {
                return;
            }

            bot.Backpack.DropItem(new Gold(amount));

            Starting += amount;
            bot.StartingGoldRemaining += amount;
            bot.TrackedGold += amount;

            Grants++;
        }

        /// <summary>
        /// A named bot the world save brought back without ever having been given the purse - the
        /// cast built on 22 September, before the rule. Given it once, on top of whatever it holds
        /// (Sean, 23 September 2026), and the save flag stops it happening twice.
        /// </summary>
        public static bool GrantLate(PlayerBot bot)
        {
            if (bot == null || bot.StartingGoldGranted)
            {
                return false;
            }

            GrantStartingGold(bot);

            if (!bot.StartingGoldGranted)
            {
                return false;
            }

            LateGrants++;

            return true;
        }

        /// <summary>
        /// This bot came out of the world save. Count what it holds in as this boot's opening balance
        /// - only the part nothing this boot has counted yet, so a second pass adds nothing.
        /// </summary>
        public static void NoteOpening(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }

            long held = GoldOf(bot);
            long unaccounted = held - bot.TrackedGold;

            if (unaccounted > 0)
            {
                Opening += unaccounted;
            }

            bot.TrackedGold = held;
        }

        /// <summary>A NAMED bot came out of the world save: counted in, and kept - offline.</summary>
        public static void NoteNamedCarried(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }

            NoteOpening(bot);

            _offline[bot.Serial] = bot;
        }

        /// <summary>A named bot is going offline, or was just born offline.</summary>
        public static void NoteOffline(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            long pack, bank, account;

            ReconcileOne(bot, out pack, out bank, out account);

            _offline[bot.Serial] = bot;
        }

        /// <summary>A named bot has logged in. The live census counts it from here.</summary>
        public static void NoteOnline(PlayerBot bot)
        {
            if (bot != null)
            {
                _offline.Remove(bot.Serial);
            }
        }

        public static bool IsOffline(PlayerBot bot)
        {
            return bot != null && _offline.ContainsKey(bot.Serial);
        }

        /// <summary>
        /// FIXTURES ONLY: set a bot's pack purse to exactly this much, on the books. What it held
        /// leaves as a `fixture` loss and the new purse enters as starting gold, so a [CoreSmoke run
        /// can put a buyer at any balance without knocking Bots.Gold out of true.
        /// </summary>
        internal static void SetPurseForFixture(PlayerBot bot, int amount)
        {
            if (bot == null || bot.Deleted || bot.Backpack == null)
            {
                return;
            }

            int have = PackGold(bot);

            if (have > 0)
            {
                NoteLoss(bot, have, ReasonFixture, "pack");
                bot.Backpack.ConsumeTotal(typeof(Gold), have, true);
            }

            if (amount > 0)
            {
                bot.Backpack.DropItem(new Gold(amount));

                Starting += amount;
                bot.TrackedGold += amount;
            }
        }

        // ---- a trade (BotTrade only) ----

        /// <summary>The buyer's half. Spends the starting purse first, like the goods ledger's stash.</summary>
        internal static void NotePaid(PlayerBot buyer, int amount)
        {
            if (buyer == null || amount <= 0)
            {
                return;
            }

            Paid += amount;
            buyer.TrackedGold -= amount;
            buyer.StartingGoldRemaining -= Math.Min(amount, Math.Max(0, buyer.StartingGoldRemaining));
        }

        /// <summary>The seller's half.</summary>
        internal static void NoteReceived(PlayerBot seller, int amount)
        {
            if (seller == null || amount <= 0)
            {
                return;
            }

            Received += amount;
            seller.TrackedGold += amount;
        }

        internal static void NoteTrade(BotTradeOutcome outcome, BotTradeLimit limit)
        {
            switch (outcome)
            {
                case BotTradeOutcome.Completed:
                    TradesCompleted++;

                    if (limit == BotTradeLimit.Gold)
                    {
                        TradesPartGold++;
                    }
                    else if (limit == BotTradeLimit.Room)
                    {
                        TradesPartCap++;
                    }

                    break;

                case BotTradeOutcome.Refused:
                    if (limit == BotTradeLimit.Gold)
                    {
                        TradesRefusedGold++;
                    }
                    else
                    {
                        TradesRefusedOther++;
                    }

                    break;

                case BotTradeOutcome.RolledBack:
                    TradesRolledBack++;
                    break;
            }
        }

        // ---- the losses ----

        /// <summary>
        /// A bot is about to be destroyed. Records what it holds - pack, bank box and, for a named
        /// bot, account currency the census will no longer be able to see.
        /// </summary>
        public static void NoteBotLoss(PlayerBot bot, string why)
        {
            if (bot == null)
            {
                return;
            }

            NoteLoss(bot, PackGold(bot), why, "pack");
            NoteLoss(bot, BankGold(bot), why, "bank");
            NoteLoss(bot, AccountGold(bot), why, "account");
        }

        /// <summary>Gold in this container (a corpse, a stripped pack) is about to leave the bot for good.</summary>
        public static void NoteContainerLoss(PlayerBot bot, Container container, string why)
        {
            if (bot == null || container == null)
            {
                return;
            }

            long amount = container.GetAmount(typeof(Gold), true);

            foreach (BankCheck check in container.FindItemsByType<BankCheck>(true))
            {
                amount += check.Worth;
            }

            NoteLoss(bot, amount, why, container is Corpse ? "corpse" : "container");
        }

        /// <summary>The primitive: this much gold, gone, for this reason - recorded, never silent.</summary>
        public static void NoteLoss(PlayerBot bot, long amount, string why, string where)
        {
            if (bot == null || amount <= 0)
            {
                return;
            }

            string reason = String.IsNullOrEmpty(why) ? BotGoodsLedger.ReasonDeleted : why;
            long starting = Math.Min(amount, Math.Max(0, bot.StartingGoldRemaining));

            bot.StartingGoldRemaining -= (int)starting;
            bot.TrackedGold -= amount;

            Lost += amount;
            LostStarting += starting;

            if (bot.IsNamed)
            {
                LostNamed += amount;
            }
            else
            {
                LostThrowaway += amount;
            }

            Records++;

            long[] cell;

            if (!_byReason.TryGetValue(reason, out cell))
            {
                cell = new long[2];
                _byReason[reason] = cell;
            }

            cell[bot.IsNamed ? 1 : 0] += amount;

            var line = new StringBuilder(320);

            line.Append('{');
            line.Append("\"utc\":").Append(Json.Quote(DateTime.UtcNow.ToString("o")));
            line.Append(",\"bootId\":").Append(Json.Quote(PersistenceGeneration.BootId ?? ""));
            line.Append(",\"generation\":").Append(PersistenceGeneration.Current);
            line.Append(",\"why\":").Append(Json.Quote(reason));
            line.Append(",\"bot\":").Append(Json.Quote(bot.Name));
            line.Append(",\"serial\":").Append(Json.Quote(bot.Serial.ToString()));
            line.Append(",\"class\":").Append(Json.Quote(bot.Class.ToString()));
            line.Append(",\"named\":").Append(bot.IsNamed ? "true" : "false");
            line.Append(",\"map\":").Append(Json.Quote(bot.Map == null ? "none" : bot.Map.Name));
            line.Append(",\"x\":").Append(bot.X);
            line.Append(",\"y\":").Append(bot.Y);
            line.Append(",\"from\":").Append(Json.Quote(where ?? ""));
            line.Append(",\"amount\":").Append(amount);
            line.Append(",\"starting\":").Append(starting);
            line.Append(",\"earned\":").Append(amount - starting);
            line.Append('}');

            _losses.Append(line.ToString());
        }

        /// <summary>
        /// A bot is leaving, and everything it could be seen holding has been written down. What the
        /// ledger still believes it holds is gold nothing can account for, either way round.
        /// </summary>
        public static void NoteDeparture(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }

            _offline.Remove(bot.Serial);

            if (bot.TrackedGold > 0)
            {
                NoteVanished(bot, bot.TrackedGold);
            }
            else if (bot.TrackedGold < 0)
            {
                NoteAppeared(bot, -bot.TrackedGold);
            }

            bot.TrackedGold = 0;
        }

        private static void NoteVanished(PlayerBot bot, long amount)
        {
            Vanished += amount;

            Log.Warn(
                "{0} ({1}, {2}) lost {3} gold at {4},{5} with nothing to account for it. Bots.Gold has failed.",
                bot.Name,
                bot.Class,
                bot.DeletionReason ?? (bot.Deleted ? BotGoodsLedger.ReasonDeleted : "alive"),
                amount,
                bot.X,
                bot.Y);
        }

        private static void NoteAppeared(PlayerBot bot, long amount)
        {
            Appeared += amount;

            Log.Warn(
                "{0} ({1}, {2}) gained {3} gold at {4},{5} that nothing gave it. Bots.Gold has failed.",
                bot.Name,
                bot.Class,
                bot.DeletionReason ?? (bot.Deleted ? BotGoodsLedger.ReasonDeleted : "alive"),
                amount,
                bot.X,
                bot.Y);
        }

        // ---- the census ----

        /// <summary>
        /// Walk the live bots and the offline named ones, reconcile each against what the ledger was
        /// told, and total what they hold. LiveRegistry, not World.Mobiles (CLAUDE.md section 15).
        /// </summary>
        public static void Reconcile()
        {
            long pack = 0, bank = 0, account = 0, held = 0, offline = 0;
            int bots = 0;

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted || _offline.ContainsKey(bot.Serial))
                {
                    continue;
                }

                bots++;

                // Between its death and its corpse run a named bot's purse is on the corpse for a
                // second. Left as it was and counted as held; the run settles it (NamedBots.CorpseRun).
                // INTERIM, with the death rule - the goods ledger does the same.
                if (bot.CorpseRunPending)
                {
                    held += bot.TrackedGold;
                    pack += bot.TrackedGold;
                    continue;
                }

                long p, b, a;

                held += ReconcileOne(bot, out p, out b, out a);
                pack += p;
                bank += b;
                account += a;
            }

            foreach (PlayerBot bot in new List<PlayerBot>(_offline.Values))
            {
                if (bot == null || bot.Deleted)
                {
                    continue;
                }

                long p, b, a;
                long one = ReconcileOne(bot, out p, out b, out a);

                held += one;
                offline += one;
                pack += p;
                bank += b;
                account += a;
            }

            Held = held;
            HeldPack = pack;
            HeldBank = bank;
            HeldAccount = account;
            OfflineHeld = offline;
            OfflineBots = _offline.Count;
            Censused = bots;

            OpeningAtCensus = Opening;
            StartingAtCensus = Starting;
            ReceivedAtCensus = Received;
            PaidAtCensus = Paid;
            LostAtCensus = Lost;
            LostThrowawayAtCensus = LostThrowaway;
            LostNamedAtCensus = LostNamed;
            AppearedAtCensus = Appeared;
            VanishedAtCensus = Vanished;

            LastReconcileUtc = DateTime.UtcNow;
            HasReconciled = true;
        }

        private static long ReconcileOne(PlayerBot bot, out long pack, out long bank, out long account)
        {
            pack = PackGold(bot);
            bank = BankGold(bot);
            account = AccountGold(bot);

            long actual = pack + bank + account;
            long tracked = bot.TrackedGold;

            if (actual > tracked)
            {
                NoteAppeared(bot, actual - tracked);
            }
            else if (actual < tracked)
            {
                NoteVanished(bot, tracked - actual);
            }

            bot.TrackedGold = actual;

            return actual;
        }

        // ---- health ----

        public static HealthResult BuildHealthResult()
        {
            long input = OpeningAtCensus + StartingAtCensus + ReceivedAtCensus + AppearedAtCensus;
            long output = PaidAtCensus + LostAtCensus + VanishedAtCensus;
            long drift = input - (output + Held);

            var text = new StringBuilder(640);

            text.AppendFormat(
                "at the census - in: opening {0:N0} + starting {1:N0} + received {2:N0} = {3:N0}. "
                + "out: paid {4:N0}, lost {5:N0} ({6:N0} throwaway, {7:N0} named); unexplained {8:N0} "
                + "(appeared {9:N0}, vanished {10:N0}); held {11:N0} over {12} live bot(s) and {13} offline named "
                + "(pack {14:N0}, bank {15:N0}, account {16:N0}; offline named {17:N0}).",
                OpeningAtCensus,
                StartingAtCensus,
                ReceivedAtCensus,
                OpeningAtCensus + StartingAtCensus + ReceivedAtCensus,
                PaidAtCensus,
                LostAtCensus,
                LostThrowawayAtCensus,
                LostNamedAtCensus,
                AppearedAtCensus + VanishedAtCensus,
                AppearedAtCensus,
                VanishedAtCensus,
                Held,
                Censused,
                OfflineBots,
                HeldPack,
                HeldBank,
                HeldAccount,
                OfflineHeld);

            text.AppendFormat(
                " {0} purse(s) granted this boot ({1} late, to named bots built before the rule).",
                Grants,
                LateGrants);

            text.AppendFormat(
                " trades: {0} completed ({1} cut short by the buyer's gold, {2} by its room), "
                + "{3} refused for want of gold, {4} refused otherwise, {5} rolled back.",
                TradesCompleted,
                TradesPartGold,
                TradesPartCap,
                TradesRefusedGold,
                TradesRefusedOther,
                TradesRolledBack);

            if (_byReason.Count > 0)
            {
                text.Append(" lost by reason:");

                bool first = true;

                foreach (var entry in _byReason)
                {
                    text.AppendFormat(
                        "{0} {1:N0} {2} ({3:N0} throwaway, {4:N0} named)",
                        first ? "" : ",",
                        entry.Value[0] + entry.Value[1],
                        entry.Key,
                        entry.Value[0],
                        entry.Value[1]);

                    first = false;
                }

                text.Append('.');
            }

            if (!HasReconciled)
            {
                text.Append(" No census has run yet.");

                return HealthResult.Warn(text.ToString());
            }

            text.AppendFormat(" Census at {0:HH:mm:ss}Z.", LastReconcileUtc);

            if (Appeared > 0 || Vanished > 0)
            {
                text.AppendFormat(
                    " {0:N0} gold appeared and {1:N0} vanished with nothing to account for it. The console names each one.",
                    Appeared,
                    Vanished);

                return HealthResult.Fail(text.ToString());
            }

            if (drift != 0)
            {
                // Every way gold enters or leaves is counted above, and a difference the census found
                // would be appeared or vanished rather than this - so a drift is arithmetic here.
                text.AppendFormat(" The books are out by {0:N0} gold.", drift);

                return HealthResult.Warn(text.ToString());
            }

            text.Append(" Balanced.");

            return HealthResult.Ok(text.ToString());
        }
    }
}
