using System;
using System.Collections.Generic;

using Server.Engines.Quests;
using Server.Items;
using Server.Mobiles;
using Server.Network;

namespace Server.Custom
{
    /// <summary>
    /// Counts matching backpack items toward any active ObtainObjective automatically, instead of
    /// making the player open their paperdoll context menu and target each stack through
    /// "Toggle Quest Item".
    ///
    /// Shard-wide by design: every quest with an obtain objective is covered, the ~100 stock ML
    /// quests included. That is the analogue of the ModernUO shard's AutoCollectInstaller, which
    /// wrapped every registered CollectObjective.
    ///
    /// It works by calling the engine's own QuestHelper.CheckItem rather than reimplementing the
    /// count. That matters: on ServUO the Item.QuestItem flag IS the turn-in ledger
    /// (QuestHelper.TryDeleteItems -> CountQuestItems counts only flagged items), so incrementing
    /// CurProgress alone would produce an objective that reads complete but cannot be handed in.
    /// CheckItem flags, adds progress, fires the update sound and completes the quest, all in the
    /// engine's own order.
    ///
    /// Because the flag also makes an item undroppable, untradeable and recoloured
    /// (Item.Nontransferable => QuestItem), every candidate must first pass QuestItemSafety.
    /// Anything it rejects is reported to the player and still works through the manual toggle.
    ///
    /// One shared timer sweeps online players. There is deliberately no per-player timer: at 1 Hz
    /// across a populated shard that would be hundreds of Timer instances competing for the same
    /// tick.
    /// </summary>
    public static class AutoCollectSystem
    {
        private static readonly CustomLogger Log = CustomLogger.For("Quests");

        private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1.0);

        /// <summary>How stale LastTickUtc may be before the health check calls the sweep dead.</summary>
        private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(5.0);

        private static readonly Dictionary<Mobile, DateTime> _nextNotice = new Dictionary<Mobile, DateTime>();

        private static Timer _timer;

        /// <summary>Config: Custom.AutoCollectEnabled</summary>
        public static bool Enabled { get; private set; }

        /// <summary>Config: Custom.AutoCollectNoticeSeconds</summary>
        public static TimeSpan NoticeCooldown { get; private set; }

        public static bool Running
        {
            get
            {
                return _timer != null && _timer.Running;
            }
        }

        public static DateTime LastTickUtc { get; private set; }

        public static long TotalFlagged { get; private set; }

        /// <summary>
        /// Matching items the safety filter rejected on the most recent sweep. Deliberately not a
        /// running total: the same untouchable item is re-examined every second, so a cumulative
        /// count would just measure uptime.
        /// </summary>
        public static int LastSkipped { get; private set; }

        public static long Faults { get; private set; }

        public static string LastFault { get; private set; }

        /// <summary>Called from QuestBootstrap.Initialize(), after the world has loaded.</summary>
        public static void Start()
        {
            Enabled = Config.Get("Custom.AutoCollectEnabled", true);
            NoticeCooldown = TimeSpan.FromSeconds(Config.Get("Custom.AutoCollectNoticeSeconds", 60.0));

            EventSink.Disconnected += OnDisconnected;

            if (!Enabled)
            {
                Log.Warn("Auto-collect is disabled (Custom.AutoCollectEnabled=False). Quest items must be toggled by hand.");
                return;
            }

            if (_timer != null)
            {
                _timer.Stop();
            }

            // count 0 = repeat forever.
            _timer = Timer.DelayCall(SweepInterval, SweepInterval, 0, new TimerCallback(Sweep));

            LastTickUtc = DateTime.UtcNow;

            Log.Info("Auto-collect sweeping every {0:F0}s.", SweepInterval.TotalSeconds);
        }

        private static void Sweep()
        {
            LastTickUtc = DateTime.UtcNow;

            // A save freezes the world on this very thread and a load has no players yet; either
            // way there is nothing to do and mutating items across a save is not worth the risk.
            if (World.Loading || World.Saving)
            {
                return;
            }

            int skipped = 0;

            try
            {
                // Online players only. Walking World.Mobiles at 1 Hz would sweep every character
                // ever created (CLAUDE.md section 15).
                foreach (NetState ns in NetState.Instances)
                {
                    var player = ns.Mobile as PlayerMobile;

                    if (player == null || player.Deleted || !player.Alive)
                    {
                        continue;
                    }

                    Container pack = player.Backpack;

                    if (pack == null || pack.Items.Count == 0)
                    {
                        continue;
                    }

                    List<BaseQuest> quests;

                    // Read the store directly rather than through PlayerMobile.Quests: that getter
                    // is MondainQuestData.GetQuests, which INSERTS an empty list for anyone it is
                    // asked about, and MondainQuestData.OnSave writes every entry it holds. At 1 Hz
                    // the property would grow and then persist one junk row per online player.
                    if (!MondainQuestData.QuestData.TryGetValue(player, out quests) || quests == null || quests.Count == 0)
                    {
                        continue;
                    }

                    skipped += SweepPlayer(player, pack, quests);
                }

                LastSkipped = skipped;
            }
            catch (Exception ex)
            {
                // A fault must never kill the timer - it would silently take auto-collect off the
                // whole shard with no symptom but players complaining that quests do not count.
                Faults++;
                LastFault = ex.Message;

                Log.Error(ex, "Auto-collect sweep faulted.");
            }
        }

        private static int SweepPlayer(PlayerMobile player, Container pack, List<BaseQuest> quests)
        {
            if (!HasActiveObtain(quests))
            {
                return 0;
            }

            // Top level only, matching the manual toggle (ToggleQuestItem_Callback requires
            // item.Parent == player.Backpack, cliloc 1074769). A sub-container is therefore a safe
            // stash for items the player does not want consumed.
            //
            // Snapshot it once per player, not once per objective: QuestHelper.CheckItem flags
            // items and, on the flag that completes the quest, the engine sends gumps and runs
            // whatever OnCompleted the quest defines - all from inside the loop below.
            Item[] snapshot = pack.Items.ToArray();

            int skipped = 0;
            bool shortfall = false;

            for (int i = 0; i < quests.Count; i++)
            {
                BaseQuest quest = quests[i];

                if (quest == null || quest.Objectives == null)
                {
                    continue;
                }

                List<BaseObjective> objectives = quest.Objectives;

                for (int j = 0; j < objectives.Count; j++)
                {
                    var obtain = objectives[j] as ObtainObjective;

                    if (obtain == null || obtain.Completed || obtain.Failed)
                    {
                        continue;
                    }

                    skipped += Collect(player, snapshot, obtain);

                    // Only nag when the filter is actually the reason they are short.
                    if (!obtain.Completed)
                    {
                        shortfall = true;
                    }
                }
            }

            if (shortfall && skipped > 0)
            {
                Notify(player, skipped);
            }

            return skipped;
        }

        private static bool HasActiveObtain(List<BaseQuest> quests)
        {
            for (int i = 0; i < quests.Count; i++)
            {
                BaseQuest quest = quests[i];

                if (quest == null || quest.Objectives == null)
                {
                    continue;
                }

                for (int j = 0; j < quest.Objectives.Count; j++)
                {
                    var obtain = quest.Objectives[j] as ObtainObjective;

                    if (obtain != null && !obtain.Completed && !obtain.Failed)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Flags every safe, unflagged, matching item in the snapshot until the objective reads
        /// complete. Returns how many matching items the safety filter rejected.
        /// </summary>
        private static int Collect(PlayerMobile player, Item[] snapshot, ObtainObjective obtain)
        {
            int skipped = 0;

            for (int i = 0; i < snapshot.Length; i++)
            {
                if (obtain.Completed)
                {
                    break;
                }

                Item item = snapshot[i];

                // Already flagged items must be skipped: ObtainObjective.Update is a TOGGLE, so
                // passing one back through CheckItem would un-flag it and subtract its progress.
                if (item == null || item.Deleted || item.QuestItem)
                {
                    continue;
                }

                if (!obtain.IsObjective(item))
                {
                    continue;
                }

                if (!QuestItemSafety.CanAutoCount(item))
                {
                    skipped++;
                    continue;
                }

                // CheckItem re-walks every quest and credits the first matching objective, which
                // may not be this one. That is harmless - QuestHelper.CanOffer already refuses to
                // hand out two quests sharing an objective type, and the next tick re-evaluates.
                if (QuestHelper.CheckItem(player, item))
                {
                    TotalFlagged++;
                }
            }

            return skipped;
        }

        private static void Notify(PlayerMobile player, int skipped)
        {
            DateTime next;

            if (_nextNotice.TryGetValue(player, out next) && DateTime.UtcNow < next)
            {
                return;
            }

            _nextNotice[player] = DateTime.UtcNow + NoticeCooldown;

            player.SendMessage(
                0x35,
                String.Format(
                    "{0} matching item{1} in your pack {2} not counted automatically - named, hued, insured, player-made or magical items are left alone. Use Toggle Quest Item on your paperdoll to hand one in anyway.",
                    skipped,
                    skipped == 1 ? "" : "s",
                    skipped == 1 ? "was" : "were"));
        }

        private static void OnDisconnected(DisconnectedEventArgs e)
        {
            if (e.Mobile != null)
            {
                _nextNotice.Remove(e.Mobile);
            }
        }

        internal static HealthResult BuildHealthResult()
        {
            if (!Enabled)
            {
                return HealthResult.Warn("disabled (Custom.AutoCollectEnabled=False) - quest items must be toggled by hand");
            }

            if (!Running)
            {
                return HealthResult.Fail("enabled, but the sweep timer is not running");
            }

            TimeSpan since = DateTime.UtcNow - LastTickUtc;

            if (since > StallThreshold)
            {
                return HealthResult.Fail(String.Format("sweep has not ticked for {0:F0}s", since.TotalSeconds));
            }

            string detail = String.Format(
                "sweeping every {0:F0}s; {1} item(s) flagged since boot, {2} skipped by the safety filter on the last sweep",
                SweepInterval.TotalSeconds,
                TotalFlagged,
                LastSkipped);

            if (Faults > 0)
            {
                return HealthResult.Warn(String.Format("{0}; {1} fault(s), last: {2}", detail, Faults, LastFault));
            }

            return HealthResult.Ok(detail);
        }
    }
}
