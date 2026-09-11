// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotMeasurementProfile.cs - a switch that buys walks instead of wall time.
//
// WHY THIS EXISTS
// ---------------
// Windows A to D each took thirty, thirty, fifteen and thirty minutes of wall clock and produced
// between 8 and 18 terminal failures apiece. Window D's headline rate was 0.20 failures a minute,
// which is a good number and a slow one to measure: three hundred walks at that population's
// natural pace is well over an hour, and every change to the walker has to be measured against a
// window long enough to say anything.
//
// The quantity that costs time is not the walker, it is the WALKING. A bot spends most of its life
// standing in a shop it walked to. So this turns two dials that both buy the same thing:
//
//   visit windows / 3      each bot leaves sooner, so it walks again sooner
//   session curve flat     the curve exists to make 05:00 quieter than 19:00, which is exactly
//                          what a measurement must not have
//
// THERE WAS A THIRD, AND IT NEVER WORKED. See the note below.
//
// WHAT IT IS NOT
// --------------
// **A profile window is not comparable to A-D except per walk.** Doubling the population and
// thirding the visits changes crowding and traffic density, and a per-minute figure MEASURES
// crowding and traffic density - so a profile window will read worse per minute than window D
// while the roads are unchanged. That is the whole reason NavWalkFailures gained a denominator.
// Say so in any table that puts them side by side.
//
// It is also not a live-shard setting. Like the *OnStart flags it is `False` in the committed
// config and is restored to `False` before any commit; the banner exists because a flag that is
// quietly on is a flag that ships.

using System;

namespace Server.Custom
{
    public static class BotMeasurementProfile
    {
        // THE POPULATION DIAL IS GONE, BECAUSE MEASURING IT SAID IT WAS NEVER THERE.
        //
        // It multiplied BotSession.TargetNow, which is the SESSION ceiling - how many lifecycle
        // bots may be LIVE. It is not how many EXIST. The population is a file: BotPopulation.Build
        // derives the recipe from `config.Target` (BotPopulation.cs:287) and [GG_Reimport turns that
        // into GG_BotPop.xml's spawner slots, so a ceiling of 120 against 60 authored slots fills
        // all 60 and stops.
        //
        // Measured, window E: `target 120 now (peak 60)` beside `60 bot(s) live`. The doubling
        // contributed nothing to that window; what bought its walks was the visit divisor and the
        // flat curve, and it still ran at about 22 walks a minute.
        //
        // THE TWO WAYS TO FIX IT WERE BOTH WORSE THAN DELETING IT.
        //
        // Making it real means regenerating spawners into Spawns/Custom - a tracked file, rewritten
        // by a switch whose whole contract is that flipping one line back to False undoes it - and
        // a crash mid-window would leave the doubled file committed. Leaving it in place means a
        // banner, a health warning and two READMEs all claiming "population x2" about a number that
        // does not move, which is worse than a missing feature: it is a measurement that lies about
        // its own conditions.
        //
        // The honest way to raise the population is the one already written down - raise
        // `population.target` in Data/Custom/bots.json, then [BotPopulationGen, [GG_Reimport,
        // [BotPopulationAudit - and the tick cost says it is affordable: 0.4 ms mean and 13 ms max
        // against a 2000 ms budget at 60 bots. `Bots.Recipe` prints that cost beside the live count
        // precisely so the next value is arithmetic rather than nerve.
        //
        // THE TICK-BUDGET CLAMP WENT WITH IT. It existed only to bound a multiplier that no longer
        // exists, and a clamp with nothing to clamp is a branch nobody can ever see run. The
        // threshold it encoded - half the budget, so there is headroom for the max as well as the
        // mean - survives as the stopping rule for the population scale test, in the Bots README.

        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>What a visit window is divided by while the profile is on.</summary>
        public const double VisitDivisor = 3.0;

        private static Timer _banner;

        /// <summary>Read once at Configure. A measurement profile must not change mid-window.</summary>
        private static bool _enabled;

        public static bool Enabled
        {
            get { return _enabled; }
        }

        public static void Configure()
        {
            _enabled = Config.Get("Custom.MeasurementProfile", false);

            if (!_enabled)
            {
                return;
            }

            HealthCheck.Register("Bots.MeasurementProfile", BuildHealthResult);
        }

        public static void Initialize()
        {
            if (!_enabled)
            {
                return;
            }

            Banner();

            // Once a minute, not once. A run is measured in tens of minutes and its console is
            // read by scrolling back through it; a banner printed only at boot is a banner that
            // has scrolled off by the time anybody looks, which is the same as not printing it.
            _banner = Timer.DelayCall(
                TimeSpan.FromMinutes(1.0), TimeSpan.FromMinutes(1.0), Banner);
        }

        private static void Banner()
        {
            Utility.WriteConsoleColor(
                ConsoleColor.Yellow,
                "======================================================================");
            Utility.WriteConsoleColor(
                ConsoleColor.Yellow,
                "  MEASUREMENT PROFILE IS ON. This is NOT a live-shard configuration.");
            Utility.WriteConsoleColor(
                ConsoleColor.Yellow,
                String.Format(
                    "  visit windows /{0:F0}, session curve FLAT. Population is UNCHANGED.",
                    VisitDivisor));
            Utility.WriteConsoleColor(
                ConsoleColor.Yellow,
                "  Per-minute figures are NOT comparable to windows A-D. Read per walk.");
            Utility.WriteConsoleColor(
                ConsoleColor.Yellow,
                "  " + BotTickManager.DescribeCost());
            Utility.WriteConsoleColor(
                ConsoleColor.Yellow,
                String.Format(
                    "  walks {0} started / {1} completed, {2} terminal failure(s).",
                    NavWalkFailures.WalksStarted,
                    NavWalkFailures.WalksCompleted,
                    NavWalkFailures.Total));
            Utility.WriteConsoleColor(
                ConsoleColor.Yellow,
                "  Set Custom.MeasurementProfile=False before committing.");
            Utility.WriteConsoleColor(
                ConsoleColor.Yellow,
                "======================================================================");

            Log.Warn(
                "Measurement profile is ON: visits /{0:F0}, curve flat, population unchanged. {1}",
                VisitDivisor,
                BotTickManager.DescribeCost());
        }

        /// <summary>
        /// A Warn, not an Ok, and deliberately.
        ///
        /// [CoreSmoke is where somebody checks whether the shard is in a state they expect, and a
        /// measurement profile left on is precisely a state nobody expects. Warn is what makes it
        /// visible in a list somebody is skimming.
        /// </summary>
        private static HealthResult BuildHealthResult()
        {
            return HealthResult.Warn(String.Format(
                "MEASUREMENT PROFILE ON - visits /{0:F0}, curve flat, population unchanged. "
                + "Per-minute figures are not comparable to earlier windows; read per walk. {1}",
                VisitDivisor,
                BotTickManager.DescribeCost()));
        }

        /// <summary>
        /// Apply the visit divisor. Called from the one place visit windows are decided.
        ///
        /// Nullable in and nullable out, because null is a real answer there and means something
        /// specific: a Traveler and an Idle have no visit window at all - they end when their PHASE
        /// does - and a null shortened to zero would end them instantly.
        /// </summary>
        public static TimeSpan? Shorten(TimeSpan? window)
        {
            if (!_enabled || window == null || window.Value <= TimeSpan.Zero)
            {
                return window;
            }

            // At least a minute, whatever the divisor: a visit shorter than the walk that reached
            // it turns the population into a teleporting crowd and stops measuring travel at all.
            var shortened = TimeSpan.FromTicks((long)(window.Value.Ticks / VisitDivisor));

            return shortened < TimeSpan.FromMinutes(1.0) ? TimeSpan.FromMinutes(1.0) : shortened;
        }
    }
}
