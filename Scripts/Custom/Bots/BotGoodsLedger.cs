// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotGoodsLedger.cs - where the raw goods went, and a refusal to lose any of them quietly.
//
// REVIEW.md F5, the second half. BotHaul stops delivery destroying a load; this is what makes
// the claim CHECKABLE, and what writes down the losses that happen anyway.
//
// THE EQUATION, over raw haul goods only (BotHaul.TrackedTypes - ore and logs):
//
//     in    = mined + seeded
//     out   = accepted + lost          both terminal: the crafter turned it into something else,
//                                      or it was destroyed
//     held  = a live census of every bot's pack, panniers and bank box
//
//     in == out + held,  and unexplained == 0
//
// BANKED IS NOT AN EXIT. A load in a bot's own bank box is still that bot's, so it is inside
// `held` and is reported beside it rather than added to it. Counting it as delivered is part of
// what F5 is about: the old NoteDelivery(hauled) said a load had arrived when it had gone into
// the hauler's own bank box, or nowhere at all.
//
// WHY `mined` IS RECONCILED AND NOT METERED. GathererBehavior.NoticeYield polls the pack for a
// rise and the instance holding that poll is thrown away at EndShift, so ore the harvest timer
// delivers after the brain swaps is never counted. A meter that misses units cannot prove
// conservation. So a timer walks the live bots and compares what each one ACTUALLY holds against
// PlayerBot.TrackedCustody:
//
//     actual > tracked  ->  units appeared, which is harvesting: NoteMined the difference
//     actual < tracked  ->  units vanished WITHOUT passing through this file, which is exactly
//                           the F5 failure. Counted as `unexplained`, and the health check fails.
//
// Every deliberate change of custody updates TrackedCustody at the same moment it happens, so
// the reconcile only ever sees what nothing told it about. That makes the residual a detector
// rather than a fudge factor.
//
// IT RUNS ON ITS OWN TIMER, not inside the health check: HealthCheck.RunAll is reachable from
// the admin API off the game thread, and a census that walks World state must not be. The check
// reports the last reconcile.
//
// THE STASH IS REPORTED APART FROM THE HAUL (Sean, 22 September 2026). EquipmentTable gives every
// Miner and Lumberjack 3-15 units at spawn as "a working stash from the last shift", and the boot
// purge destroys them along with the bot. Those are real units and they are counted in - but if
// they were tallied together with lost deliveries, a genuinely lost load would disappear into the
// noise of a restart. PlayerBot.StashRemaining carries the seeded part, settlement spends it
// FIRST, and every loss record and the health line splits the two.
//
// UPSTREAM'S SHAPE for the file itself: BotEventJournal writes Data/Live/event-journal.jsonl, one
// JSON object per line, append-only. This is that, for one kind of event.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Server.Items;

namespace Server.Custom
{
    public static class BotGoodsLedger
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        public const string Path = "Data/Live/goods-lost.jsonl";

        /// <summary>
        /// Rotate past this, to Path + ".1". A boot purge writes one line per laden gatherer, so
        /// the file grows by a couple of hundred lines every restart and would otherwise be
        /// unbounded. One generation back is enough to read the last restart's losses.
        /// </summary>
        public const long MaxBytes = 4L * 1024L * 1024L;

        /// <summary>How often the census runs. Not a config key: nothing tunes it.</summary>
        public static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30.0);

        public const string ReasonBootPurge = "boot-purge";
        public const string ReasonLogout = "logout";
        public const string ReasonDeath = "death";
        public const string ReasonDeleted = "deleted";
        public const string ReasonReclass = "reclass";

        /// <summary>
        /// The curve had no room for a bot the spawner had already built and dressed. Its own
        /// reason because it is the one loss that is nobody's mistake: EquipmentTable seeds the
        /// stash in the constructor and BotSession.AllowSpawn refuses at the end of the seed,
        /// so the kit exists for as long as it takes to decide it should not.
        /// </summary>
        public const string ReasonSpawnRefused = "spawn-refused";
        public const string ReasonProbeStrip = "probe-strip";
        public const string ReasonProbe = "probe";
        public const string ReasonPackAnimal = "pack-animal-released";

        // ---- the ledger ----

        /// <summary>
        /// Units this boot inherited: what the bots in the world save were holding when
        /// BotStartupPurge counted them, before it deleted them.
        ///
        /// An opening balance, and it has to be a source or the books could never balance on the
        /// boot that destroys it. The matching entry is on the other side - every one of these
        /// units is written down again as a `boot-purge` loss moments later - so a restart reads
        /// as "inherited 8,412, destroyed 8,412" rather than as a hole.
        /// </summary>
        public static int Opening { get; private set; }

        /// <summary>Units handed to bots at spawn by EquipmentTable. A source, like mining.</summary>
        public static int Seeded { get; private set; }

        /// <summary>Units a crafter took over the counter. Terminal: they became refined stock.</summary>
        public static int Accepted { get; private set; }

        /// <summary>
        /// Units now sitting in bot bank boxes, as of the last census. Inside <see cref="Held"/>
        /// and reported beside it rather than added to it - a bank box is the bot's own, so
        /// banking is a place a load is standing, not a delivery it has made.
        /// </summary>
        public static int Banked { get; private set; }

        /// <summary>Units destroyed and written down. Terminal.</summary>
        public static int Lost { get; private set; }

        /// <summary>Of <see cref="Lost"/>, the part that came from the spawn stash.</summary>
        public static int LostStash { get; private set; }

        /// <summary>Of <see cref="Lost"/>, the part that was mined or hauled. The number that matters.</summary>
        public static int LostHaul { get; private set; }

        /// <summary>Loss records written since boot.</summary>
        public static int Records { get; private set; }

        /// <summary>
        /// The last record, as the line that went to the journal. Internal: HaulFixtures asserts
        /// on the JSON itself rather than on the counters, so "item, amount, bot, where and why"
        /// is proved to be IN the record and not merely to have been passed to this method.
        /// </summary>
        internal static string LastRecord { get; private set; }

        /// <summary>Units that left a bot's custody without passing through this file. Should be zero.</summary>
        public static int Unexplained { get; private set; }

        /// <summary>Hand-overs where the receiver took some but not all of the load.</summary>
        public static int PartRefused { get; private set; }

        /// <summary>Hand-overs where the receiver took nothing at all.</summary>
        public static int Refused { get; private set; }

        /// <summary>Hand-overs that settled, whole or partial.</summary>
        public static int Handovers { get; private set; }

        /// <summary>Units held by live bots at the last census: pack, panniers and bank box.</summary>
        public static int Held { get; private set; }

        /// <summary>Live bots the last census walked.</summary>
        public static int Censused { get; private set; }

        public static DateTime LastReconcileUtc { get; private set; }

        public static bool HasReconciled { get; private set; }

        /// <summary>reason -> { stash units, haul units }.</summary>
        private static readonly Dictionary<string, int[]> _byReason =
            new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<string> _pending = new List<string>();

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private static bool _hooked;

        // ---- wiring ----

        public static void Configure()
        {
            if (_hooked)
            {
                return;
            }

            _hooked = true;

            // A save is a good moment to get the buffer onto disk, and writing a file is not a
            // change to the world - Core.SaveIntegrity's rule is about CONSTRUCTING entities
            // during a save, which nothing here does. BotOrphans.WriteCensus already hangs off
            // this event for the same reason.
            EventSink.WorldSave += delegate { Flush(); };
        }

        public static void Initialize()
        {
            HealthCheck.Register("Bots.Conservation", BuildHealthResult);

            // The first pass is deferred past ServerStarted so the population fill has put its
            // bots in the world; a census that ran at Initialize would set every TrackedCustody
            // from an empty world and then read the spawn stash as mined.
            Timer.DelayCall(TimeSpan.FromSeconds(20.0), ReconcileInterval, Tick);
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
                Log.Error(ex, "The goods census faulted.");
            }
        }

        // ---- the sources and the exits ----

        /// <summary>
        /// This bot came out of the world save holding goods. Count them in before the boot purge
        /// takes them, so the loss it is about to record has something to be a loss of.
        ///
        /// Called once per bot, from BotStartupPurge, and nowhere else: a bot that was spawned
        /// this boot got its units through NoteSeeded or through the ground.
        /// </summary>
        public static void NoteOpening(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }

            int held = BotHaul.Custody(bot);

            // ONLY THE PART NOTHING HAS COUNTED YET. At boot that is all of it - a deserialized
            // bot's TrackedCustody is zero, because the field is transient and the census has
            // never seen it. On a bot this boot already accounted for, it is nothing, and saying
            // so is what stops the opening balance double-counting units the seed or the ground
            // had already put on the books.
            int unaccounted = held - bot.TrackedCustody;

            if (unaccounted > 0)
            {
                Opening += unaccounted;
            }

            bot.TrackedCustody = held;
        }

        /// <summary>
        /// Units came out of the ground into this bot's pack. A source, and NOT stash.
        ///
        /// Both halves matter. BotWorkSites.Mined is the number the work probe and the Bots.Work
        /// line read; TrackedCustody is what stops the census counting the same units a second
        /// time thirty seconds later, which is what a bare NoteMined would have cost.
        /// </summary>
        public static void NoteMined(PlayerBot bot, int amount)
        {
            if (bot == null || amount <= 0)
            {
                return;
            }

            BotWorkSites.NoteMined(amount);
            bot.TrackedCustody += amount;
        }

        /// <summary>A bot was given goods at spawn. They are a source, and they are stash.</summary>
        public static void NoteSeeded(PlayerBot bot, int amount)
        {
            if (bot == null || amount <= 0)
            {
                return;
            }

            Seeded += amount;
            bot.StashRemaining += amount;
            bot.TrackedCustody += amount;
        }

        /// <summary>A receiver took units over the counter. They have left this bot for good.</summary>
        public static void NoteAccepted(PlayerBot bot, int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            Accepted += amount;

            if (bot != null)
            {
                bot.TrackedCustody -= amount;
                SpendStash(bot, amount);
            }
        }

        /// <summary>What the hand-over settled at, for the health line and the next loss record.</summary>
        public static void NoteHandover(PlayerBot bot, string destinationId, int offered, int accepted)
        {
            Handovers++;

            if (accepted <= 0)
            {
                Refused++;
            }
            else if (accepted < offered)
            {
                PartRefused++;
            }

            if (bot != null)
            {
                bot.LastHandoverUtc = DateTime.UtcNow;
                bot.LastHandoverId = destinationId;
                bot.LastHandoverOffered = offered;
                bot.LastHandoverAccepted = accepted;
            }
        }

        // ---- the losses ----

        /// <summary>
        /// A bot is about to be destroyed with goods on it. Records the pack and the bank box.
        ///
        /// NOT the panniers: the beast is its own loss, recorded by BotPackAnimals.Release, and
        /// counting them here as well would double the number.
        /// </summary>
        public static void NoteBotLoss(PlayerBot bot, string why)
        {
            if (bot == null)
            {
                return;
            }

            NoteContainerLoss(bot, bot.Backpack, why);
            NoteContainerLoss(bot, bot.FindBankNoCreate(), why);
        }

        /// <summary>Everything tracked in this container is about to stop existing.</summary>
        public static void NoteContainerLoss(PlayerBot bot, Container container, string why)
        {
            if (bot == null || container == null)
            {
                return;
            }

            IList<Type> tracked = BotHaul.TrackedTypes;

            for (int i = 0; i < tracked.Count; i++)
            {
                int amount = BotHaul.InContainer(container, tracked[i]);

                if (amount > 0)
                {
                    NoteLoss(bot, tracked[i], amount, why);
                }
            }
        }

        /// <summary>
        /// The primitive: this many units of this good, gone, for this reason.
        ///
        /// Rule 4. Item, amount, bot, where, why - and the split between the spawn stash and a
        /// real haul, so a lost delivery is never hidden under a restart's worth of stash. When
        /// the bot's last hand-over left a remainder, that is recorded too: "it reached a bench,
        /// the bench was full, and then this happened" is a different fact from "a bot with ore
        /// vanished".
        /// </summary>
        public static void NoteLoss(PlayerBot bot, Type raw, int amount, string why)
        {
            if (bot == null || raw == null || amount <= 0)
            {
                return;
            }

            int stash = Math.Min(amount, Math.Max(0, bot.StashRemaining));
            int haul = amount - stash;

            bot.StashRemaining -= stash;
            bot.TrackedCustody -= amount;

            Lost += amount;
            LostStash += stash;
            LostHaul += haul;
            Records++;

            Tally(why, stash, haul);

            var line = new StringBuilder(320);

            line.Append('{');
            line.Append("\"utc\":").Append(Json.Quote(DateTime.UtcNow.ToString("o")));
            line.Append(",\"why\":").Append(Json.Quote(why ?? ReasonDeleted));
            line.Append(",\"bot\":").Append(Json.Quote(bot.Name));
            line.Append(",\"serial\":").Append(Json.Quote(bot.Serial.ToString()));
            line.Append(",\"class\":").Append(Json.Quote(bot.Class.ToString()));
            line.Append(",\"map\":").Append(Json.Quote(bot.Map == null ? "none" : bot.Map.Name));
            line.Append(",\"x\":").Append(bot.X);
            line.Append(",\"y\":").Append(bot.Y);
            line.Append(",\"item\":").Append(Json.Quote(raw.Name));
            line.Append(",\"amount\":").Append(amount);
            line.Append(",\"stash\":").Append(stash);
            line.Append(",\"haul\":").Append(haul);

            if (bot.LastHandoverUtc != DateTime.MinValue)
            {
                line.Append(",\"handover\":{");
                line.Append("\"utc\":").Append(Json.Quote(bot.LastHandoverUtc.ToString("o")));
                line.Append(",\"destination\":").Append(Json.Quote(bot.LastHandoverId));
                line.Append(",\"offered\":").Append(bot.LastHandoverOffered);
                line.Append(",\"accepted\":").Append(bot.LastHandoverAccepted);
                line.Append('}');
            }

            line.Append('}');

            LastRecord = line.ToString();

            _pending.Add(LastRecord);
        }

        /// <summary>
        /// Units that left custody with nothing to say where they went.
        ///
        /// This is the F5 alarm. It is not written to the journal per bot - there is no item type
        /// to name, only a difference - but it fails the health check, which is louder.
        /// </summary>
        private static void NoteUnexplained(PlayerBot bot, int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            Unexplained += amount;

            Log.Warn(
                "{0} lost {1} unit(s) of raw goods with nothing to account for it. Bots.Conservation has failed.",
                bot == null ? "a bot" : bot.Name,
                amount);
        }

        private static void SpendStash(PlayerBot bot, int amount)
        {
            // The stash is spent FIRST, deliberately. It is the conservative direction: whatever
            // is still classified as stash at a loss is as small as the arithmetic allows, so a
            // real lost delivery cannot be filed under the spawn kit.
            bot.StashRemaining -= Math.Min(amount, Math.Max(0, bot.StashRemaining));
        }

        private static void Tally(string why, int stash, int haul)
        {
            string key = String.IsNullOrEmpty(why) ? ReasonDeleted : why;
            int[] cell;

            if (!_byReason.TryGetValue(key, out cell))
            {
                cell = new int[2];
                _byReason[key] = cell;
            }

            cell[0] += stash;
            cell[1] += haul;
        }

        // ---- the census ----

        /// <summary>
        /// Walk the live bots, reconcile what each one holds against what we were told, and total
        /// the held units.
        ///
        /// LiveRegistry rather than World.Mobiles (CLAUDE.md section 15), and FindBankNoCreate
        /// rather than the BankBox property, which mints a box on a miss.
        /// </summary>
        public static void Reconcile()
        {
            int held = 0;
            int banked = 0;
            int bots = 0;

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted)
                {
                    continue;
                }

                bots++;

                int actual = BotHaul.Custody(bot);
                int tracked = bot.TrackedCustody;

                if (actual > tracked)
                {
                    // Units appeared. The only thing that makes raw goods appear in a bot's pack
                    // is the harvest timer, so this is mining that NoticeYield's poll missed.
                    BotWorkSites.NoteMined(actual - tracked);
                }
                else if (actual < tracked)
                {
                    NoteUnexplained(bot, tracked - actual);
                }

                bot.TrackedCustody = actual;

                held += actual;
                banked += BotHaul.InContainer(bot.FindBankNoCreate());
            }

            Held = held;
            Banked = banked;
            Censused = bots;
            LastReconcileUtc = DateTime.UtcNow;
            HasReconciled = true;
        }

        // ---- the file ----

        /// <summary>
        /// Append whatever has accumulated. One write, however many records - a boot purge hands
        /// this a couple of hundred at once.
        /// </summary>
        public static void Flush()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var text = new StringBuilder(_pending.Count * 320);

            for (int i = 0; i < _pending.Count; i++)
            {
                text.Append(_pending[i]).Append('\n');
            }

            _pending.Clear();

            try
            {
                string full = System.IO.Path.Combine(Core.BaseDirectory, Path);
                string directory = System.IO.Path.GetDirectoryName(full);

                if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Rotate(full);

                File.AppendAllText(full, text.ToString(), Utf8);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "The goods-lost journal could not be written.");
            }
        }

        private static void Rotate(string full)
        {
            var info = new FileInfo(full);

            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            string previous = full + ".1";

            if (File.Exists(previous))
            {
                File.Delete(previous);
            }

            File.Move(full, previous);
        }

        // ---- health ----

        public static HealthResult BuildHealthResult()
        {
            int mined = BotWorkSites.Mined;
            int input = Opening + mined + Seeded;
            int output = Accepted + Lost;
            int drift = input - (output + Held);

            var text = new StringBuilder(480);

            text.AppendFormat(
                "in: opening {0} + mined {1} + seeded {2} = {3}. "
                + "out: accepted {4}, lost {5} ({6} stash, {7} haul); "
                + "held {8} over {9} bot(s), {10} of it banked.",
                Opening,
                mined,
                Seeded,
                input,
                Accepted,
                Lost,
                LostStash,
                LostHaul,
                Held,
                Censused,
                Banked);

            text.AppendFormat(
                " {0} hand-over(s) settled, {1} part-refused, {2} refused outright.",
                Handovers,
                PartRefused,
                Refused);

            if (_byReason.Count > 0)
            {
                text.Append(" lost by reason:");

                bool first = true;

                foreach (var entry in _byReason)
                {
                    text.AppendFormat(
                        "{0} {1} {2} ({3} stash, {4} haul)",
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

            if (Unexplained > 0)
            {
                text.AppendFormat(
                    " {0} unit(s) left a bot's custody with nothing to account for it - that is REVIEW.md F5.",
                    Unexplained);

                return HealthResult.Fail(text.ToString());
            }

            if (drift != 0)
            {
                // Everything above is counted, so this can only be a bookkeeping mistake in this
                // file rather than a lost load. It is still wrong, and saying so is the point.
                text.AppendFormat(" The books are out by {0} unit(s).", drift);

                return HealthResult.Warn(text.ToString());
            }

            text.Append(" Balanced.");

            return HealthResult.Ok(text.ToString());
        }
    }
}
