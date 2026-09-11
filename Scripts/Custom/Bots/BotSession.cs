// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotSession.cs — bots have play sessions, not eternal existence.
//
// uo-offline's BotSessionManager. Real players log in, play for a few hours,
// say "gtg", and vanish; a 24-hour curve says how many of them are on at each
// local hour, dead at 05:00 and packed at 19:00. Without it a population is a
// flat number and the town reads identically at every hour of the day.
//
// FIXTURES ARE OUTSIDE ALL OF IT. A BotRole.Fixed bot never logs out and is
// never counted toward the target, so the bank crowd and the staffed benches
// are there at the 05:00 trough exactly as they are at the peak. That is
// upstream's rule ("fixtures are furniture, not sessions") and it is also what
// makes the economy session's premise - a forge is staffed by construction -
// true at every hour rather than only at peak.
//
// FOUR DEVIATIONS, each marked at its site:
//
//  1. It rides BotTickManager's existing LiveRegistry pass instead of running a
//     60-second timer over World.Mobiles.
//  2. The way UP is gated in PlayerBot's seed rather than in Spawner.Spawn,
//     because XmlSpawner has no spawn hook to override.
//  3. The fixture count is read from the live pass rather than kept as a static
//     tally incremented and decremented by hand.
//  4. CanLogoutNow refuses a laden gatherer and a working crafter, which
//     upstream has no reason to and we do.

using System;
using System.Collections.Generic;

using Server.Mobiles;

namespace Server.Custom
{
    public static class BotSession
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// Off pins the population at whatever it is: spawners fill to their authored count and
        /// nobody logs out. [BotSessions off, and the probes use it to hold a population still.
        /// </summary>
        public static bool Enabled { get; set; }

        /// <summary>
        /// Upstream's MaxLogoutsPerTick (BotSessionManager.cs:47), and its reason: an hour edge
        /// drops the target by a fifth of the population at once, and four at a time is "a trickle
        /// of gtg instead of a mass exodus".
        /// </summary>
        private const int MaxLogoutsPerPass = 4;

        /// <summary>Upstream's window for pulling a nearly-finished session forward (BotSessionManager.cs:204-205).</summary>
        private static readonly TimeSpan CurvePressureWindow = TimeSpan.FromMinutes(20.0);

        private static long _nextPass;
        private static bool _initialStampDone;
        private static int _logoutsTotal;
        private static int _loginsTotal;

        static BotSession()
        {
            Enabled = true;
        }

        // ---- the curve ----

        private static BotPopulationConfig Config_
        {
            get { return BotSystem.Store.Population; }
        }

        /// <summary>
        /// The curve's multiplier now, on the LOCAL wall clock rather than the shard's.
        ///
        /// DateTime.Now, deliberately, and upstream's choice too: the curve is a claim about when
        /// the people at the keyboards are awake, not about when it is dark in Britannia. The
        /// shard has its own day and CustomTime for that.
        /// </summary>
        public static double CurveNow
        {
            get { return Config_.CurveAt(DateTime.Now.Hour); }
        }

        /// <summary>
        /// How many LIFECYCLE bots should be live right now. Fixtures are not in this number.
        ///
        /// THE MEASUREMENT PROFILE NO LONGER MULTIPLIES THIS, and the reason is worth keeping here
        /// rather than only where the dial was: this is a CEILING, not a population. The population
        /// is GG_BotPop.xml's spawner slots, derived from `population.target`, so a ceiling raised
        /// above the number of authored slots fills every slot and stops - which is exactly what
        /// window E measured, `target 120 now (peak 60)` beside `60 bot(s) live`. Raising the real
        /// population means raising `population.target` and regenerating. See BotMeasurementProfile.
        /// </summary>
        public static int TargetNow
        {
            get { return Math.Max(1, (int)(Config_.Target * CurveNow)); }
        }

        public static int LogoutsSinceBoot
        {
            get { return _logoutsTotal; }
        }

        public static int LoginsSinceBoot
        {
            get { return _loginsTotal; }
        }

        /// <summary>
        /// May a spawner's bot stay? Called at the end of PlayerBot's seed, once the role is known.
        ///
        /// DEVIATION 2. Upstream gates before construction, in an override of Spawner.Spawn
        /// (uo-offline PlayerBotSpawner.cs:75-82). XmlSpawner offers no such hook - it has no
        /// virtual Spawn and [XmlLoad constructs XmlSpawner by name, so it cannot be subclassed -
        /// and the only moment this shard knows whether a spawned bot is a fixture or a session is
        /// after the spawn string has been applied. So the bot is built and then turned away.
        ///
        /// The waste is one construction per refusal, and a bot spawner ticks every five to
        /// fifteen minutes: a handful an hour at the trough, none at the peak. That is the price of
        /// having one spawner mechanism on this shard instead of two.
        ///
        /// DEVIATION 3. The live count is read here, from the registry, rather than from a static
        /// tally. Upstream keeps FixedRoleCount incremented in OnAfterSpawn and decremented in
        /// OnAfterDelete (BotSessionManager.cs:97-102) because it wants an O(1) answer inside its
        /// own startup respawn loop. A hand-maintained tally is a thing that can drift, and
        /// Bots.Population already warns about exactly that failure in the name pool. LiveRegistry
        /// is tens of entries and this is asked only when a spawner fires.
        /// </summary>
        public static bool AllowSpawn()
        {
            if (!Enabled)
            {
                return true;
            }

            int live = 0;

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot != null && !bot.Deleted && !bot.LifecycleExempt && !bot.LoggingOut
                    && bot.Map != null && bot.Map != Map.Internal)
                {
                    live++;
                }
            }

            // `<=`, where upstream has `<`, and that is the translation rather than an off-by-one.
            // Upstream asks before the bot exists, so its count excludes the candidate; this asks
            // after, so `live` already includes it. Both mean "would this one be the (target+1)th".
            return live <= TargetNow;
        }

        // ---- the pass ----

        private static TimeSpan Interval
        {
            get { return TimeSpan.FromSeconds(Config.Get("Custom.BotSessionSeconds", 60.0)); }
        }

        /// <summary>
        /// DEVIATION 1. Riding BotTickManager's scan rather than a timer of its own.
        ///
        /// Upstream runs a 60-second timer that sweeps World.Mobiles into a scratch list
        /// (BotSessionManager.cs:126-140). CLAUDE.md section 15 forbids habitual World.Mobiles
        /// walks, LiveRegistry is the answer this shard already has, and BotTickManager already
        /// walks it every two seconds - so this is a third cadence on one scan, exactly as
        /// BotLifecycle.Pass is the second.
        /// </summary>
        public static void Pass(IList<PlayerBot> bots)
        {
            if (!Enabled || bots == null)
            {
                return;
            }

            // Wraparound-safe by subtraction (CLAUDE.md section 15), as the lifecycle's is.
            if (Core.TickCount - _nextPass < 0)
            {
                return;
            }

            _nextPass = Core.TickCount + (long)Interval.TotalMilliseconds;

            // THE FIRST PASS AFTER A BOOT IS NOT A NORMAL ONE.
            //
            // The whole standing population came up inside one startup sweep, but those bots did
            // not all just log in: stamped with full sessions they would all expire within the
            // same hour and the shard would empty at once, some hours after every restart.
            // Upstream stamps a random 10-100% of a session as already spent (BotSessionManager
            // .cs:147-161) so the logouts spread from the beginning.
            if (!_initialStampDone)
            {
                foreach (PlayerBot bot in bots)
                {
                    if (!bot.LifecycleExempt && bot.SessionEndsAt == DateTime.MinValue)
                    {
                        double full = RollSessionSeconds();
                        double remaining = full * (0.10 + (Utility.RandomDouble() * 0.90));

                        bot.SessionEndsAt = DateTime.UtcNow + TimeSpan.FromSeconds(remaining);
                    }
                }

                _initialStampDone = true;
                return;
            }

            int live = 0;

            foreach (PlayerBot bot in bots)
            {
                if (!bot.LifecycleExempt && !bot.LoggingOut)
                {
                    live++;
                }
            }

            int surplus = live - TargetNow;
            int logouts = 0;

            foreach (PlayerBot bot in bots)
            {
                if (bot.Deleted || bot.LifecycleExempt || bot.LoggingOut)
                {
                    continue;
                }

                // Arrived since the last pass: a login.
                if (bot.SessionEndsAt == DateTime.MinValue)
                {
                    bot.SessionEndsAt = DateTime.UtcNow + TimeSpan.FromSeconds(RollSessionSeconds());
                    _loginsTotal++;

                    BotLog.Note(bot, BotLogKind.Session, "logged in for {0:0} minute(s)",
                        (bot.SessionEndsAt - DateTime.UtcNow).TotalMinutes);

                    MaybeSayHello(bot);
                    continue;
                }

                if (logouts >= MaxLogoutsPerPass)
                {
                    continue;
                }

                bool due = DateTime.UtcNow >= bot.SessionEndsAt;

                // Over the curve's target: nudge out whoever was nearly finished anyway. Upstream's
                // reasoning, kept because it is the whole argument for the mechanism: "20 minutes
                // of leaving soon anyway is close enough that pulling it forward reads natural."
                bool pressured = surplus > 0
                    && bot.SessionEndsAt - DateTime.UtcNow < CurvePressureWindow;

                if (!due && !pressured)
                {
                    continue;
                }

                if (!CanLogoutNow(bot))
                {
                    continue;
                }

                BeginLogout(bot);
                logouts++;

                if (pressured && !due)
                {
                    surplus--;
                }
            }
        }

        private static double RollSessionSeconds()
        {
            BotPopulationConfig config = Config_;

            return Utility.RandomMinMax(config.SessionMinMinutes * 60, config.SessionMaxMinutes * 60);
        }

        /// <summary>
        /// A bot in the middle of something finishes it first; the next pass catches it.
        ///
        /// DEVIATION 4. Upstream's list (BotSessionManager.cs:227-234) also names GhostBehavior,
        /// CorpseReclaimBehavior and CorpseRunPending; this shard has no death layer yet, so those
        /// three are a severed seam rather than an omission. What is added is the pair upstream has
        /// no reason to check and we very much do: a gatherer carrying a load, and a crafter
        /// clocked in at its bench. Both are halves of the work loop, and a bot that says "gtg"
        /// mid-delivery leaves an ore load nobody ever collects and a smith standing dry - which
        /// looks exactly like the work layer being broken.
        /// </summary>
        private static bool CanLogoutNow(PlayerBot bot)
        {
            if (!bot.Alive || bot.LoggingOut || bot.Combatant != null)
            {
                return false;
            }

            if (BotParty.InParty(bot))
            {
                return false;
            }

            if (bot.HaulPending)
            {
                return false;
            }

            var crafter = bot.Behavior as CrafterBehavior;

            if (crafter != null && crafter.IsAtStation)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Say goodbye, then go - with a beat in between, "like a real logout timer".
        /// </summary>
        private static void BeginLogout(PlayerBot bot)
        {
            bot.LoggingOut = true;
            _logoutsTotal++;

            Speak(bot, "session_goodbye");

            BotLog.Note(bot, BotLogKind.Session, "logged out");

            Timer.DelayCall(TimeSpan.FromSeconds(Utility.RandomMinMax(3, 6)), () =>
            {
                if (!bot.Deleted)
                {
                    bot.Delete();
                }
            });
        }

        /// <summary>
        /// A mid-play login sometimes greets the room, but only when somebody is there to hear it,
        /// and a few seconds late "like a client finishing its load".
        ///
        /// The nearby check is not politeness, it is the cost control the speech layer already
        /// runs on: a line said to an empty street is a line nobody hears and a packet nobody
        /// wanted. See the three gates in the README's Speech section.
        /// </summary>
        private static void MaybeSayHello(PlayerBot bot)
        {
            if (Utility.RandomDouble() > 0.45)
            {
                return;
            }

            Timer.DelayCall(TimeSpan.FromSeconds(Utility.RandomMinMax(4, 15)), () =>
            {
                if (bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
                {
                    return;
                }

                if (PlayerNearby(bot))
                {
                    Speak(bot, "session_hello");
                }
            });
        }

        private static bool PlayerNearby(PlayerBot bot)
        {
            IPooledEnumerable nearby = bot.Map.GetMobilesInRange(bot.Location, 18);

            try
            {
                foreach (Mobile mobile in nearby)
                {
                    if (mobile is PlayerMobile && mobile.Player && !mobile.Deleted)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                // A non-generic IPooledEnumerable has to be freed or the pool leaks
                // (CLAUDE.md section 14).
                nearby.Free();
            }

            return false;
        }

        private static void Speak(PlayerBot bot, string category)
        {
            string line = ChatLibrary.PickRandom(category);

            if (String.IsNullOrEmpty(line))
            {
                return;
            }

            var context = new ChatTokenContext { Bot = bot };
            string resolved;

            if (!ChatTokens.TryResolve(line, context, out resolved))
            {
                return;
            }

            bot.Say(resolved);
            bot.SpeechLines++;

            BotLog.Note(bot, BotLogKind.Speech, "{0}: {1}", category, resolved);
        }

        /// <summary>One line for [BotSessions and the health checks.</summary>
        public static string Describe()
        {
            int hour = DateTime.Now.Hour;

            return String.Format(
                "sessions {0}. curve {1:P0} at {2:00}:00 of target {3} gives {4} lifecycle bot(s); "
                + "{5} login(s) and {6} logout(s) since boot",
                Enabled ? "ON" : "OFF (population pinned)",
                CurveNow,
                hour,
                Config_.Target,
                TargetNow,
                _loginsTotal,
                _logoutsTotal);
        }
    }
}
