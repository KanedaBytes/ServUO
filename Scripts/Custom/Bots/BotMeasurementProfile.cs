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
// standing in a shop it walked to. So this turns three dials that all buy the same thing:
//
//   population target x2   more walkers, so more walks per minute
//   visit windows / 3      each bot leaves sooner, so it walks again sooner
//   session curve flat     the curve exists to make 05:00 quieter than 19:00, which is exactly
//                          what a measurement must not have
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
        // THE POPULATION DIAL IS BOUNDED BY THE SPAWNER FILE, AND MEASURING IT SAID SO.
        //
        // Multiplier doubles BotSession.TargetNow, which is the SESSION ceiling - how many
        // lifecycle bots may be live. It is not how many exist. The population is a file:
        // BotPopulation.Build derives the recipe from `config.Target` (BotPopulation.cs:287) and
        // [GG_Reimport turns that into GG_BotPop.xml's spawner slots, so a target of 120 against
        // 60 authored slots fills all 60 and stops.
        //
        // Measured, window E: `target 120 now (peak 60)` beside `60 bot(s) live`. The doubling
        // therefore contributed nothing to that window; what bought its walks was the visit
        // divisor and the flat curve, and it still ran at about 22 walks a minute.
        //
        // NOT FIXED HERE, deliberately. Doubling the real population means regenerating spawners
        // into Spawns/Custom - a tracked file, rewritten by a switch that is supposed to be
        // reversible by flipping one line back to False. Raising `population.target` in
        // Data/Custom/bots.json and running [GG_Reimport is the honest way to do it, and the tick
        // cost says it is affordable: 0.5 ms mean and 11 ms max against a 2000 ms budget at 60
        // bots leaves room for a great deal more than 120.

        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>What the target is multiplied by while the profile is on.</summary>
        public const double TargetMultiplier = 2.0;

        /// <summary>What a visit window is divided by while the profile is on.</summary>
        public const double VisitDivisor = 3.0;

        /// <summary>
        /// The share of the tick budget above which the profile stops doubling.
        ///
        /// The doubling is CLAMPED BY WHAT A TICK COSTS, because "twice as many bots" is a request
        /// and not a promise: BotTickManager measures the mean and max cost of a population pass
        /// against Custom.BotTickSeconds, and a population that cannot be ticked inside its own
        /// budget does not walk faster, it walks in slow motion - which would corrupt the very
        /// measurement the profile exists to take.
        ///
        /// Half, so there is headroom for the max as well as the mean: the tick cost is a mean over
        /// passes and a single pass can be several times it.
        /// </summary>
        public const double TickBudgetShare = 0.5;

        private static Timer _banner;

        private static bool _clamped;

        /// <summary>Read once at Configure. A measurement profile must not change mid-window.</summary>
        private static bool _enabled;

        public static bool Enabled
        {
            get { return _enabled; }
        }

        /// <summary>Whether the tick cost has forced the multiplier back down. See TickBudgetShare.</summary>
        public static bool Clamped
        {
            get { return _clamped; }
        }

        /// <summary>
        /// The multiplier in force right now: 1.0 with the profile off, 2.0 with it on, and back
        /// toward 1.0 while a tick pass is costing more than half its budget.
        /// </summary>
        public static double Multiplier
        {
            get
            {
                if (!_enabled)
                {
                    return 1.0;
                }

                double budgetMs = BotTickManager.Interval.TotalMilliseconds;

                if (budgetMs <= 0.0 || BotTickManager.PassCount <= 0)
                {
                    return TargetMultiplier;
                }

                if (BotTickManager.PassMeanMs <= budgetMs * TickBudgetShare)
                {
                    _clamped = false;
                    return TargetMultiplier;
                }

                // Over budget: fall back to plain 1.0 rather than to something in between. A
                // multiplier that drifts with the tick cost would make the population oscillate,
                // and a population that oscillates is a worse thing to measure than a small one.
                _clamped = true;
                return 1.0;
            }
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
                    "  population x{0:F0}{1}, visit windows /{2:F0}, session curve FLAT.",
                    TargetMultiplier,
                    _clamped ? " (CLAMPED to x1 by tick cost)" : "",
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
                "Measurement profile is ON: population x{0:F0}{1}, visits /{2:F0}, curve flat. {3}",
                TargetMultiplier,
                _clamped ? " (clamped to x1 by tick cost)" : "",
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
                "MEASUREMENT PROFILE ON - population x{0:F0}{1}, visits /{2:F0}, curve flat. "
                + "Per-minute figures are not comparable to earlier windows; read per walk. {3}",
                TargetMultiplier,
                _clamped ? " (clamped to x1 by tick cost)" : "",
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
