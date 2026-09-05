using System;
using System.Collections.Generic;

using Server.Commands;

namespace Server.Custom
{
    /// <summary>
    /// Drives a whole day/night cycle in a few milliseconds and checks the town reacts.
    ///
    /// This is a PERMANENT REGRESSION TEST, not scaffolding. A real cycle takes two real hours,
    /// so without this the only way to find out that a later change stopped the tavern filling
    /// is to sit and watch Britain for an evening. It forces each phase in turn, asserts what
    /// should have happened, and puts the clock back.
    ///
    /// It does not run itself on a live shard: [DailyLifeSmoke, or Custom.DailyLifeSmokeOnStart
    /// for a headless run, since ServUO's console cannot invoke staff commands. Its last result
    /// is reported by [CoreSmoke through the DailyLife.Smoke health check, so the whole-shard
    /// report says at a glance whether daily life was last seen working - without [CoreSmoke
    /// itself forcing phases and spawning crowds on a live shard.
    /// </summary>
    public static class DailyLifeSmoke
    {
        private static readonly CustomLogger Log = CustomLogger.For("DailyLife");

        private static DateTime? _lastRunUtc;
        private static string _firstFailure;
        private static int _checks;

        public static void Initialize()
        {
            CommandSystem.Register("DailyLifeSmoke", AccessLevel.Administrator, DailyLifeSmoke_OnCommand);

            HealthCheck.Register("DailyLife.Smoke", BuildHealthResult);

            if (Config.Get("Custom.DailyLifeSmokeOnStart", false))
            {
                EventSink.ServerStarted += () => Run(null);
            }
        }

        [Usage("DailyLifeSmoke")]
        [Description("Forces a full day cycle and checks the town reacts. Restores the clock afterwards.")]
        private static void DailyLifeSmoke_OnCommand(CommandEventArgs e)
        {
            Run(e.Mobile);
        }

        public static void Run(Mobile from)
        {
            var report = new List<string>();

            _firstFailure = null;
            _checks = 0;

            bool hadOverride = DayCycleSystem.HasOverride;

            report.Add("-- daily life smoke --");

            try
            {
                CheckSettled(report);

                CheckPhase(report, DayPhase.Dusk, true);
                CheckPhase(report, DayPhase.Night, true);
                CheckPhase(report, DayPhase.Dawn, false);
                CheckPhase(report, DayPhase.Day, false);
            }
            catch (Exception ex)
            {
                Fail(report, "the smoke test threw " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                // Always put the clock back, whatever happened above. Leaving a forced phase
                // behind would be a worse bug than anything this test could find.
                DayCycleSystem.ClearOverride();
            }

            _lastRunUtc = DateTime.UtcNow;

            if (hadOverride)
            {
                report.Add("  note: an override was active before this ran; it has been cleared.");
            }

            report.Add(_firstFailure == null
                ? String.Format("  PASSED - {0} check(s).", _checks)
                : String.Format("  FAILED - {0}", _firstFailure));

            Emit(from, report);
        }

        /// <summary>
        /// The steady state, before any phase is forced: every shopkeeper should already be
        /// where the current phase wants it.
        ///
        /// This is the one that catches the real bug - a shard that booted at night with no
        /// shopkeepers and imported them afterwards. The per-phase check below is weaker,
        /// because immediately after a transition the vendors are still walking and a walker is
        /// deliberately left alone.
        /// </summary>
        private static void CheckSettled(List<string> report)
        {
            report.Add("  settled:");

            ShopScheduleSystem.Reconcile();

            if (ShopScheduleSystem.ManagedVendorCount == 0)
            {
                report.Add("    shops: no managed vendors in the world - run [GG_MigrateVendors then [GG_Reimport");
                return;
            }

            int misplaced = ShopScheduleSystem.CountMisplaced();

            Assert(report, misplaced == 0,
                String.Format("all {0} shopkeeper(s) reconcile to the {1} phase, {2} left misplaced",
                    ShopScheduleSystem.ManagedVendorCount,
                    DayCycleSystem.Current.ToFriendlyString(),
                    misplaced));
        }

        /// <summary>
        /// Forces one phase and asserts the whole town agreed with it.
        ///
        /// Everything reacts synchronously: SetOverride calls Poll, which raises PhaseChanged,
        /// and every consumer applies it in the handler. So the assertions can run immediately
        /// rather than waiting for a timer.
        /// </summary>
        private static void CheckPhase(List<string> report, DayPhase phase, bool afterDark)
        {
            DayCycleSystem.SetOverride(phase);

            report.Add(String.Format("  {0}:", phase.ToFriendlyString()));

            Assert(report, DayCycleSystem.Current == phase,
                String.Format("the phase is {0}", phase.ToFriendlyString()));

            Assert(report, phase.IsAfterDark() == afterDark,
                String.Format("{0} is {1}after dark", phase.ToFriendlyString(), afterDark ? "" : "not "));

            CheckTavern(report, afterDark);
            CheckWatch(report, afterDark);
            CheckShops(report, afterDark);
        }

        private static void CheckTavern(List<string> report, bool afterDark)
        {
            TavernConfig tavern = DailyLifeSystem.Config.Tavern;

            if (tavern == null || Nav.Destination(tavern.Destination) == null)
            {
                report.Add("    tavern: not configured, skipped");
                return;
            }

            if (afterDark)
            {
                Assert(report, TavernSystem.PatronCount == tavern.PatronCount,
                    String.Format("the tavern holds {0} patron(s), found {1}",
                        tavern.PatronCount, TavernSystem.PatronCount));
            }
            else
            {
                Assert(report, TavernSystem.PatronCount == 0,
                    String.Format("the tavern is empty, found {0}", TavernSystem.PatronCount));
            }
        }

        private static void CheckWatch(List<string> report, bool afterDark)
        {
            int posts = DailyLifeSystem.Config.Watch.Count;

            if (posts == 0)
            {
                report.Add("    watch: no posts configured, skipped");
                return;
            }

            if (afterDark)
            {
                Assert(report, NightWatchSystem.WatchCount == posts,
                    String.Format("the watch deploys {0} post(s), found {1}",
                        posts, NightWatchSystem.WatchCount));
            }
            else
            {
                Assert(report, NightWatchSystem.WatchCount == 0,
                    String.Format("the watch stands down, found {0}", NightWatchSystem.WatchCount));
            }
        }

        private static void CheckShops(List<string> report, bool afterDark)
        {
            Assert(report, ShopScheduleSystem.ShopsAreClosed == afterDark,
                String.Format("the shops are {0}", afterDark ? "closed" : "open"));

            Assert(report, ShopScheduleSystem.LastGoingHome == afterDark,
                String.Format("the shopkeepers were sent {0}", afterDark ? "home" : "to work"));

            if (ShopScheduleSystem.ManagedVendorCount == 0)
            {
                // Not a failure: the shopkeepers only exist once the migration and import have
                // been run, and the rest of the cycle is still worth checking without them.
                report.Add("    shops: no managed vendors in the world - run [GG_MigrateVendors then [GG_Reimport");
                return;
            }

            // Anything not walking must already be where this phase wants it. Most will be
            // walking right after a transition, which is why CheckSettled exists as well.
            ShopScheduleSystem.Reconcile();

            int misplaced = ShopScheduleSystem.CountMisplaced();
            int walking = ShopScheduleSystem.WalkingCount;

            Assert(report, misplaced == 0,
                String.Format("no settled shopkeeper is in the wrong place ({0} walking, {1} misplaced)",
                    walking, misplaced));
        }

        private static void Assert(List<string> report, bool condition, string description)
        {
            _checks++;

            report.Add(String.Format("    [{0}] {1}", condition ? "ok" : "FAIL", description));

            if (!condition && _firstFailure == null)
            {
                _firstFailure = description;
            }
        }

        private static void Fail(List<string> report, string message)
        {
            report.Add("    [FAIL] " + message);

            if (_firstFailure == null)
            {
                _firstFailure = message;
            }
        }

        private static void Emit(Mobile from, List<string> report)
        {
            bool passed = _firstFailure == null;

            foreach (string line in report)
            {
                if (passed)
                {
                    Log.Info(line);
                }
                else
                {
                    Log.Warn(line);
                }

                if (from != null)
                {
                    from.SendMessage(passed ? 0x40 : 0x25, line);
                }
            }
        }

        private static HealthResult BuildHealthResult()
        {
            if (!_lastRunUtc.HasValue)
            {
                return HealthResult.Warn("never run - [DailyLifeSmoke, or Custom.DailyLifeSmokeOnStart");
            }

            string when = _lastRunUtc.Value.ToString("HH:mm:ss") + "Z";

            if (_firstFailure != null)
            {
                return HealthResult.Fail(String.Format("FAILED at {0}: {1}", when, _firstFailure));
            }

            return HealthResult.Ok(String.Format("passed at {0}, {1} check(s)", when, _checks));
        }
    }
}
