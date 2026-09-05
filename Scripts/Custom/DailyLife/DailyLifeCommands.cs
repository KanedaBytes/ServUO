using System;
using System.Collections.Generic;

using Server.Commands;

namespace Server.Custom
{
    public static class DailyLifeCommands
    {
        private static readonly CustomLogger Log = CustomLogger.For("DailyLife");

        public static void Initialize()
        {
            CommandSystem.Register("DayPhase", AccessLevel.GameMaster, DayPhase_OnCommand);
            CommandSystem.Register("DailyLifeReload", AccessLevel.GameMaster, DailyLifeReload_OnCommand);
            CommandSystem.Register("ReloadDailyLife", AccessLevel.GameMaster, DailyLifeReload_OnCommand);
        }

        // ---- [DayPhase ----

        [Usage("DayPhase [dawn|day|dusk|night|clear]")]
        [Description("Reports the town's day phase, or forces one for testing.")]
        private static void DayPhase_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length == 0)
            {
                Report(from);
                return;
            }

            string argument = e.GetString(0);

            if (Insensitive.Equals(argument, "clear"))
            {
                DayCycleSystem.ClearOverride();
                from.SendMessage("Day phase override cleared; the town is back on the clock.");

                CommandLogging.WriteLine(
                    from,
                    String.Format("{0} {1} clearing the day phase override",
                        from.AccessLevel, CommandLogging.Format(from)));
                return;
            }

            Server.Custom.DayPhase phase;

            if (!DayPhaseExtensions.TryParse(argument, out phase))
            {
                from.SendMessage(0x35, "Usage: [DayPhase [dawn|day|dusk|night|clear]");
                return;
            }

            DayCycleSystem.SetOverride(phase);

            from.SendMessage(String.Format(
                "Day phase forced to {0}. It will stay there until [DayPhase clear, a reload, or a restart.",
                phase.ToFriendlyString()));

            CommandLogging.WriteLine(
                from,
                String.Format("{0} {1} forcing the day phase to {2}",
                    from.AccessLevel, CommandLogging.Format(from), phase.ToFriendlyString()));
        }

        private static void Report(Mobile from)
        {
            int hours, minutes;
            DayCycleSystem.GetAnchorTime(out hours, out minutes);

            from.SendMessage(String.Format(
                "It is {0} - {1:D2}:{2:D2} in game at the town anchor.",
                DayCycleSystem.Current.ToFriendlyString(),
                hours,
                minutes));

            // Say so plainly. A pinned night that outlives the reason for it is an evening spent
            // wondering why the sun never comes up.
            if (DayCycleSystem.HasOverride)
            {
                from.SendMessage(0x35,
                    "An override is ACTIVE - the phase is forced and the light level is pinned. [DayPhase clear to release it.");
            }
            else
            {
                from.SendMessage("No override is active.");
            }

            from.SendMessage(String.Format(
                "Tavern patrons: {0}. Night watch: {1}. Route walkers: {2}. Shopkeepers walking: {3}.",
                TavernSystem.PatronCount,
                NightWatchSystem.WatchCount,
                TownsfolkSystem.WalkerCount,
                ShopScheduleSystem.WalkingCount));
        }

        // ---- [DailyLifeReload ----

        [Usage("DailyLifeReload")]
        [Aliases("ReloadDailyLife")]
        [Description("Re-reads britain-daily-life.json and rebuilds the town.")]
        private static void DailyLifeReload_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            string error;

            if (!TryReload(out error))
            {
                from.SendMessage(0x35, String.Format("Daily life config NOT reloaded: {0}", error));
                from.SendMessage(0x35, "The town is still running on the previously loaded config.");
                return;
            }

            from.SendMessage(String.Format(
                "Daily life reloaded. Tavern patrons: {0}. Night watch: {1}. Route walkers: {2}.",
                TavernSystem.PatronCount,
                NightWatchSystem.WatchCount,
                TownsfolkSystem.WalkerCount));

            ReportWarnings(from);

            CommandLogging.WriteLine(
                from,
                String.Format("{0} {1} reloading the daily life config",
                    from.AccessLevel, CommandLogging.Format(from)));
        }

        /// <summary>
        /// The single entry point for the staff command and, later, the admin API, so a bad
        /// config is reported the same way whichever asked for the reload.
        /// </summary>
        public static bool TryReload(out string error)
        {
            IList<string> ignored;
            return TryReload(out error, out ignored);
        }

        /// <summary>As TryReload, keeping the validator's errors as a list.</summary>
        public static bool TryReload(out string error, out IList<string> errors)
        {
            if (!DailyLifeSystem.TryReload(out error, out errors))
            {
                return false;
            }

            Reload();
            return true;
        }

        /// <summary>
        /// Rebuilds every daily-life system from the config currently in memory.
        ///
        /// Note that neither the day-cycle poll nor any repeating timer is re-armed here - they
        /// are started once at boot, and re-arming would leave a second one running and leak
        /// another on every reload.
        /// </summary>
        public static void Reload()
        {
            // A forced phase must not survive a reload: the whole point of reloading is to see
            // the config as it now is, on the clock as it now is.
            DayCycleSystem.ClearOverride();

            TavernSystem.Reload();
            NightWatchSystem.Reload();
            TownsfolkSystem.Reload();
            ShopScheduleSystem.Reload();

            Log.Info("Daily life systems reloaded.");
        }

        /// <summary>
        /// Brings the whole town into line with the current phase without re-reading the config.
        ///
        /// Actors do not all exist when a phase changes: a shard can boot at night with no
        /// shopkeepers, import them a minute later, and nothing would ever have sent them home
        /// because the only thing that moved them was a transition they were not there for.
        /// This is the catch-up, and it is called after a reload, after [GG_Reimport, and a
        /// second after any managed vendor spawns.
        /// </summary>
        public static void Reconcile()
        {
            DayPhase phase = DayCycleSystem.Current;

            TavernSystem.ApplyPhase(phase);
            NightWatchSystem.ApplyPhase(phase);
            TownsfolkSystem.EnsureSpawned();
            ShopScheduleSystem.Reconcile();
        }

        private static void ReportWarnings(Mobile from)
        {
            IList<string> warnings = DailyLifeSystem.ConfigWarnings;

            if (warnings.Count == 0)
            {
                return;
            }

            from.SendMessage(0x35, String.Format("{0} config warning(s):", warnings.Count));

            for (int i = 0; i < warnings.Count && i < 20; i++)
            {
                from.SendMessage(0x35, "  " + warnings[i]);
            }

            if (warnings.Count > 20)
            {
                from.SendMessage(0x35, "  ... see the console for the rest.");
            }
        }
    }
}
