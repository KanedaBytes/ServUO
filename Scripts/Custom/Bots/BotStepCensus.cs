// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotStepCensus.cs - how much an idle bot actually moves, and what moved it.
//
// WHY THIS EXISTS
// ---------------
// Sean's observation was that bots idle at banks and shops move around far too much, and nothing
// in the tree could put a number on it. The two obvious sources both cannot:
//
//   botlog.json   has seven event kinds (BotLog.cs:50-78) and NOT ONE OF THEM IS A STEP. A
//                 BankSitter writes nothing at all; a Shopper does not even log its hops.
//   entities.json is a SNAPSHOT on a 2-second timer (LiveMapSnapshot.cs:83). A settled bot's AI
//                 timer ticks at CurrentSpeed, which BotMovement.Settle drops to Mobile.WalkFoot
//                 = 400ms, so five steps can happen between two snapshots and be read as one - or
//                 as none, if it shuffled back. And a snapshot can never name a CAUSE.
//
// NavPaceSampler is the nearest instrument and it measures the wrong thing on purpose: it counts
// MoveTo calls the WALKER made, so every wander step is invisible to it, and [BotPace refuses any
// bot that is not a Traveler with an active walker (BotCommands.cs:59-68). An idle bot is exactly
// the case it cannot sample.
//
// So this counts steps at the one place every step passes through, whatever moved the mobile.
//
// THE CHOKE POINT IS OnLocationChange, NOT A MOVEMENT CALL SITE. There are at least four things
// that move a bot - the walker's MoveTo, the recovery ladder's NudgeAway and its rescue teleport,
// the gatherer's own bot.Move, and BaseAI.DoMoveImpl's wander - and hooking each would mean
// touching NavWalker, which is Core and is not allowed to know that bots exist (the same inversion
// NavigationSystem.RegisterAuditor uses). Mobile.OnLocationChange (Server/Mobile.cs:10284) is
// downstream of all of them and cannot be bypassed.
//
// THE CAUSE IS READ OFF THE STATE, NOT PASSED IN. Four buckets, decided by what is true at the
// moment of the step:
//
//   teleport    the step moved more than one tile - the ladder's rescue MoveToWorld
//   walk        Commuting is true, so a walker is steering: a route leg or a ladder nudge
//   drift-back  not steered, but the step got CLOSER to Home - the engine walking a bot back to
//               the tile its behaviour pinned (BaseAI.cs:2573-2578)
//   wander      not steered, not toward Home. THIS IS THE ONE THE MEASUREMENT IS ABOUT: it is
//               BaseAI.WalkRandomInHome, which runs under every behaviour that is not commuting
//               because BotAI.DoActionWander only stands aside while Commuting (BotAI.cs:48-58).
//
// THE DENOMINATOR IS BOT-SECONDS IN THAT PHASE, not wall time. "Steps per minute" over a window
// measures how many bots were on and how long they stayed as much as it measures fidgeting -
// which is the same mistake NavWalkFailures gained WalksStarted to stop making
// (NavWalkFailures.cs:172-176). A bot contributes seconds to whichever behaviour it is holding,
// split by whether it was being steered, so the reported figure is steps per bot-minute IN that
// phase and two windows with different populations are comparable.
//
// GAME THREAD ONLY. Every writer is either OnLocationChange or the BotTickManager pass.

using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>What moved a bot one tile. See the header for how each is decided.</summary>
    public enum BotStepCause
    {
        Wander,
        DriftBack,
        Walk,
        Teleport
    }

    public static class BotStepCensus
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// One phase's tally. Seconds and steps are kept apart by whether a walker was steering,
        /// because the idle rate is the whole question and a Shopper mid-hop is not idle.
        /// </summary>
        private sealed class Tally
        {
            public double IdleSeconds;
            public double WalkingSeconds;

            public int Wander;
            public int DriftBack;
            public int Walk;
            public int Teleport;

            public int IdleSteps
            {
                get { return Wander + DriftBack; }
            }
        }

        /// <summary>
        /// Per-bot idle wander, so "is it every bot or one wedged bot" is answerable.
        ///
        /// Keyed by Serial rather than by the mobile, the same choice BotLog.cs:29-33 made and for
        /// the same reason: a bot that logs out mid-window took its steps and the window should
        /// still be able to report them without pinning a deleted mobile.
        /// </summary>
        private sealed class BotTally
        {
            public string Name;
            public int Wander;
            public double IdleSeconds;
        }

        private static readonly Dictionary<string, Tally> _phases =
            new Dictionary<string, Tally>(StringComparer.Ordinal);

        private static readonly Dictionary<Serial, BotTally> _bots = new Dictionary<Serial, BotTally>();

        private static DateTime _openedUtc = DateTime.UtcNow;
        private static long _lastObservedAt = Core.TickCount;
        private static int _total;

        /// <summary>Steps counted since the window opened, all phases and causes.</summary>
        public static int Total
        {
            get { return _total; }
        }

        /// <summary>
        /// A bot's location changed. Called from PlayerBot.OnLocationChange, which is the only
        /// caller and must stay the only caller - two would double-count.
        /// </summary>
        public static void Note(PlayerBot bot, Point3D oldLocation)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            // First placement. A bot is constructed, then MoveToWorld'd, and that arrival is not a
            // step by anybody - it reads as a teleport from the origin and would put one bogus
            // entry in the ledger per bot spawned.
            if (oldLocation == Point3D.Zero)
            {
                return;
            }

            int tiles = Math.Max(
                Math.Abs(bot.X - oldLocation.X), Math.Abs(bot.Y - oldLocation.Y));

            // Z-only. Standing still on a tile whose Z was re-resolved is not a step, and the
            // engine does re-resolve: a mount coming out from under a rider sets both.
            if (tiles == 0)
            {
                return;
            }

            BotStepCause cause;

            if (tiles > 1)
            {
                cause = BotStepCause.Teleport;
            }
            else if (bot.Commuting)
            {
                cause = BotStepCause.Walk;
            }
            else if (bot.Home != Point3D.Zero && CloserTo(bot.Home, bot.Location, oldLocation))
            {
                cause = BotStepCause.DriftBack;
            }
            else
            {
                cause = BotStepCause.Wander;
            }

            _total++;

            Tally tally = TallyFor(bot);

            switch (cause)
            {
                case BotStepCause.Wander:
                    tally.Wander++;
                    BotTallyFor(bot).Wander++;
                    break;
                case BotStepCause.DriftBack: tally.DriftBack++; break;
                case BotStepCause.Walk: tally.Walk++; break;
                case BotStepCause.Teleport: tally.Teleport++; break;
            }
        }

        /// <summary>Did the step reduce the tile distance to the pinned tile?</summary>
        private static bool CloserTo(Point3D home, Point3D now, Point3D before)
        {
            int after = Math.Max(Math.Abs(now.X - home.X), Math.Abs(now.Y - home.Y));
            int prior = Math.Max(Math.Abs(before.X - home.X), Math.Abs(before.Y - home.Y));

            return after < prior;
        }

        /// <summary>
        /// Accumulate the denominator: how long each bot spent in its phase, steered or not.
        ///
        /// Rides the pass BotTickManager already makes over LiveRegistry rather than taking a timer
        /// of its own - one scan, another cadence, exactly as BotLifecycle and BotSession do. The
        /// elapsed time is MEASURED from the last call rather than assumed to be BotTickSeconds,
        /// because the pass skips entirely while the world is saving or loading and a nominal
        /// interval would silently credit those seconds to whatever phase came back.
        /// </summary>
        public static void Observe(IList<PlayerBot> bots)
        {
            long now = Core.TickCount;
            double elapsed = (now - _lastObservedAt) / 1000.0;

            _lastObservedAt = now;

            if (bots == null || elapsed <= 0.0)
            {
                return;
            }

            // A pass that was held off by a long save would otherwise dump the whole gap into one
            // phase. Clamped rather than dropped: the steps taken during it were still counted.
            if (elapsed > 30.0)
            {
                elapsed = 30.0;
            }

            for (int i = 0; i < bots.Count; i++)
            {
                PlayerBot bot = bots[i];

                if (bot == null || bot.Deleted)
                {
                    continue;
                }

                Tally tally = TallyFor(bot);

                if (bot.Commuting)
                {
                    tally.WalkingSeconds += elapsed;
                }
                else
                {
                    tally.IdleSeconds += elapsed;
                    BotTallyFor(bot).IdleSeconds += elapsed;
                }
            }
        }

        private static Tally TallyFor(PlayerBot bot)
        {
            PlayerBotBehavior behavior = bot.Behavior;
            string name = behavior == null ? "none" : behavior.SerializableName;

            Tally tally;

            if (!_phases.TryGetValue(name, out tally))
            {
                tally = new Tally();
                _phases[name] = tally;
            }

            return tally;
        }

        private static BotTally BotTallyFor(PlayerBot bot)
        {
            BotTally tally;

            if (!_bots.TryGetValue(bot.Serial, out tally))
            {
                tally = new BotTally { Name = bot.Name ?? "?" };
                _bots[bot.Serial] = tally;
            }

            return tally;
        }

        /// <summary>
        /// Open a fresh window.
        ///
        /// The denominator clears with the numerator, for NavWalkFailures.Clear's reason: a window
        /// that divided this minute's steps by every bot-second since boot would read as zero.
        /// </summary>
        public static void Clear()
        {
            _phases.Clear();
            _bots.Clear();
            _total = 0;
            _openedUtc = DateTime.UtcNow;
            _lastObservedAt = Core.TickCount;
        }

        private static double PerBotMinute(int steps, double botSeconds)
        {
            return botSeconds <= 0.0 ? 0.0 : steps / (botSeconds / 60.0);
        }

        /// <summary>The lines, which are the whole point - a rate reported as a count is no answer.</summary>
        public static List<string> Describe()
        {
            var lines = new List<string>();

            lines.Add(String.Format(
                "window opened {0:u}, {1} step(s) counted",
                _openedUtc,
                _total));

            var names = new List<string>(_phases.Keys);
            names.Sort(StringComparer.Ordinal);

            foreach (string name in names)
            {
                Tally tally = _phases[name];

                lines.Add(String.Format(
                    "{0}: idle {1:F1} bot-min, {2:F2} idle step(s)/bot-min "
                        + "({3} wander, {4} drift-back); walking {5:F1} bot-min, {6} walk, {7} teleport",
                    name,
                    tally.IdleSeconds / 60.0,
                    PerBotMinute(tally.IdleSteps, tally.IdleSeconds),
                    tally.Wander,
                    tally.DriftBack,
                    tally.WalkingSeconds / 60.0,
                    tally.Walk,
                    tally.Teleport));
            }

            // The worst few, because "20 a minute each" and "one bot doing all of it" are different
            // faults with the same total.
            var worst = new List<KeyValuePair<Serial, BotTally>>(_bots);

            worst.Sort((a, b) => PerBotMinute(b.Value.Wander, b.Value.IdleSeconds)
                .CompareTo(PerBotMinute(a.Value.Wander, a.Value.IdleSeconds)));

            int shown = Math.Min(5, worst.Count);

            for (int i = 0; i < shown; i++)
            {
                BotTally bot = worst[i].Value;

                if (bot.Wander == 0)
                {
                    break;
                }

                lines.Add(String.Format(
                    "  worst: {0} - {1:F2} wander step(s)/min over {2:F1} idle min",
                    bot.Name,
                    PerBotMinute(bot.Wander, bot.IdleSeconds),
                    bot.IdleSeconds / 60.0));
            }

            return lines;
        }

        /// <summary>The Data/Live snapshot, written on request rather than on a timer.</summary>
        public static string BuildJson()
        {
            var builder = new System.Text.StringBuilder(2048);
            var culture = System.Globalization.CultureInfo.InvariantCulture;

            builder.Append("{\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"openedUtc\": ").Append(Json.Quote(_openedUtc.ToString("o"))).Append(",\n");
            builder.Append("  \"total\": ").Append(_total).Append(",\n");
            builder.Append("  \"phases\": [\n");

            var names = new List<string>(_phases.Keys);
            names.Sort(StringComparer.Ordinal);

            for (int i = 0; i < names.Count; i++)
            {
                Tally tally = _phases[names[i]];

                builder.Append("    {\"phase\":").Append(Json.Quote(names[i]));
                builder.Append(",\"idleBotSeconds\":")
                    .Append(tally.IdleSeconds.ToString("F1", culture));
                builder.Append(",\"walkingBotSeconds\":")
                    .Append(tally.WalkingSeconds.ToString("F1", culture));
                builder.Append(",\"wander\":").Append(tally.Wander);
                builder.Append(",\"driftBack\":").Append(tally.DriftBack);
                builder.Append(",\"walk\":").Append(tally.Walk);
                builder.Append(",\"teleport\":").Append(tally.Teleport);
                builder.Append(",\"idleStepsPerBotMinute\":")
                    .Append(PerBotMinute(tally.IdleSteps, tally.IdleSeconds).ToString("F3", culture));
                builder.Append("}");

                if (i < names.Count - 1)
                {
                    builder.Append(",");
                }

                builder.Append("\n");
            }

            builder.Append("  ]\n}\n");

            return builder.ToString();
        }

        public static bool TryWrite(out string message)
        {
            string error;

            if (!AtomicFile.Write("Data/Live/bot-steps.json", BuildJson(), out error))
            {
                message = error;
                Log.Error("Could not write bot-steps.json: {0}", error);
                return false;
            }

            var lines = Describe();

            message = lines.Count > 0 ? lines[0] : "nothing counted";

            return true;
        }
    }
}
