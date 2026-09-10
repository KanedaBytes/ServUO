// NavWalkFailures.cs — the ledger of walks that ended in a teleport.
//
// WHY THIS EXISTS
// ---------------
// The terminal failure was the one event in the whole walker that recorded nothing usable.
//
// Every rung of the recovery ladder fires LogRung, which raises the RungFired seam, which a bot
// turns into a BotLog entry. The TOP of the ladder - the point where the ladder has been climbed
// twice, the bot has not moved, and NavWalker gives up and teleports it - calls neither. It writes
// one Log.Warn and moves the mobile. So the failure everybody wants to understand is the failure
// with the least evidence attached to it, and the console line it does write says:
//
//     could not walk 'brit-bank-2' -> '(arrival)' ... it was moved
//
// `(arrival)` because DescribeHop prints waypoint IDS and an arrival step has none. The goal tile's
// coordinates are nowhere. Nor is anything about what was standing on it: DescribeBlocker reports
// mobiles on the goal only when one is exactly there, and otherwise falls back to a list centred on
// the WALKER, which is a different set of mobiles from the one being asked about.
//
// And BotLog could not have held it anyway: fifty events per bot across sixty-four bots, written
// only while the live map is running. A half-hour at a busy bank rolls its own evidence off the end
// before anybody reads it.
//
// So: an in-memory ledger, capped, written out on request as Data/Live/walk-failures.json. The
// same arrangement nav-audit.json has, for the same reason - Core does not write snapshots, the
// Bridge does.
//
// WHAT IT RECORDS, AND WHY EACH FIELD IS THERE
// --------------------------------------------
//   the hop            which edge, in the same words the console uses
//   the goal tile      the coordinates the walk was aiming at, which nothing else captures
//   the near list      every mobile within two tiles OF THE GOAL, typed - a bot, a daily-life
//                      actor, a stock NPC, an animal, a player. The type is the whole question:
//                      a bot yields to a stock NPC and shoves another bot, so who was standing
//                      there decides whether this failure was avoidable.
//   goalUnstandable    whether a mobile fits on the goal tile AT ALL. 21 authored arrivals fail
//                      this, and a failure at one of them is not a crowding problem - it is a walk
//                      that could never have finished, and it has to be separable from the rest or
//                      it poisons the measurement.

using System;
using System.Collections.Generic;

using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>One walk that climbed the whole ladder and was teleported.</summary>
    public sealed class NavWalkFailure
    {
        public DateTime Utc;
        public string Who;
        public string WhoType;
        public string Hop;
        public string MapName;
        public int GoalX;
        public int GoalY;
        public int GoalZ;

        /// <summary>
        /// Where the bot actually was when it gave up.
        ///
        /// Not the same as the hop's authored start, and that difference is the whole reason it is
        /// recorded: a bot that has been sidestepping for eighty seconds is somewhere the route
        /// never named, so "walk this hop with the engine" has to start from HERE to reproduce the
        /// failure. Reconstructing it from the from-waypoint asks a different question and can
        /// answer it "walkable" about a hop the bot could not walk.
        /// </summary>
        public int FromX;
        public int FromY;
        public int FromZ;

        public bool Watched;

        /// <summary>Nothing fits on the goal tile at any height. See the header.</summary>
        public bool GoalUnstandable;

        /// <summary>
        /// WHY this walk ended, in the only two shapes that ask different questions.
        ///
        /// "goal empty" was one bucket and it hid two unrelated faults. A bot standing INSIDE the
        /// arrival's range that still could not finish has a stand-tile problem - the place was
        /// reached and no tile in it would take the mobile. A bot that ran out of ladder ten tiles
        /// short never reached the place at all, and its problem is the road behind it, which is
        /// why that case carries the last hop. Merged, they made 28 of 30 failures look like one
        /// thing; they are not, and the two fixes have nothing in common.
        /// </summary>
        public string Cause;

        /// <summary>
        /// WHICH KIND OF HOP this was - "arrival" or "waypoint" - so the split can be counted
        /// rather than read off the Hop string.
        ///
        /// The two are different failures with different fixes and they were only ever separable
        /// by eye, by noticing that Hop ended in '(arrival)'. An arrival is a picked point plus a
        /// scatter that nothing has asked the pathfinder about; a waypoint was authored against
        /// the engine and audited in both directions. A window where the arrival column is empty
        /// and the waypoint column is not says the graph regressed; the reverse says the arrival
        /// picker did.
        /// </summary>
        public string HopKind;

        /// <summary>
        /// Whether this was the FIRST step of the route, with no waypoint behind it.
        ///
        /// Recorded because it decides which recovery rungs even exist. On a one-step route
        /// TrySkipWaypoint refuses (there is nothing to skip to) and TryReAnchor returns
        /// immediately (PreviousWaypointId is null), so the whole ladder is Repath, Sidestep and
        /// Door - none of which changes the tile being aimed at. Four of one window's seventeen
        /// failures were this, and they are invisible in the cause split.
        /// </summary>
        public bool FromStart;

        /// <summary>The arrival range in force, so "within range" is reproducible from the file.</summary>
        public int ArrivalRange;

        /// <summary>
        /// HOW FAR ALONG THE ROUTE the walker had got, and how much of that it walked.
        ///
        /// The field that answers "where did the 33 come from". A terminal failure prints the
        /// distance from the mobile to `step.Point`, and on a hop authored at ten tiles that number
        /// came back as 33 - which cannot be the edge, so it is either the mobile or the step. Both
        /// are here now:
        ///
        ///   Index / RouteCount   which step of how many. A route whose index has run to the end
        ///                        while the mobile stood still is TrySkipWaypoint marching it:
        ///                        each skip advances the index and calls ResetHop, so the ladder
        ///                        starts again and the reported hop is one the walker never
        ///                        approached.
        ///   Skips                how many of those there were on this route, counted rather than
        ///                        inferred from the index, because Advance moves it too.
        ///   StartX/Y/Z           where the mobile was when Follow was called. If that is far from
        ///                        FromX/Y/Z the mobile travelled; if it is the same tile, it never
        ///                        moved and the index is the whole story.
        /// </summary>
        public int Index;
        public int RouteCount;
        public int Skips;
        public int StartX;
        public int StartY;
        public int StartZ;

        /// <summary>"Name (type) at x,y" for everything within two tiles of the GOAL.</summary>
        public readonly List<string> Near = new List<string>();
    }

    public static class NavWalkFailures
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>
        /// Enough for a long measurement run and small enough to forget about.
        ///
        /// Oldest-first eviction rather than newest-dropped: a run that overflows wants its recent
        /// failures, and a cap that silently kept the first 500 and threw away everything after
        /// would look like the problem stopped.
        /// </summary>
        public const int MaxKept = 500;

        private static readonly List<NavWalkFailure> _failures = new List<NavWalkFailure>();

        private static int _total;

        private static int _started;

        private static int _completed;

        /// <summary>
        /// How many walks were STARTED and how many reached their last step, since the last clear.
        ///
        /// WHY A DENOMINATOR. Windows A, B, C and D are thirty, thirty, fifteen and thirty
        /// minutes, and every table so far has been read per minute - which is a fact about how
        /// busy the shard was, not about how good the roads are. A window with the population
        /// doubled and the visit windows thirded (MeasurementProfile) produces several times the
        /// walks in the same wall time, so per minute would read as a regression while the roads
        /// were unchanged.
        ///
        /// A WALK IS A ROUTE, AND A FAILURE IS AN EVENT INSIDE ONE. `_total` counts hops that
        /// climbed the whole ladder and were rescued; a route of five hops can produce more than
        /// one of those and still reach its last step, because the rescue teleports and carries
        /// on. So "failures per 100 walks" is terminal-failure events per hundred routes BEGUN,
        /// which is well defined and is the number that stops depending on window length. It is
        /// not a percentage of walks that failed, and it can exceed 100 on a graph bad enough.
        ///
        /// Completed is kept beside it as routes that reached their last step. The gap between the
        /// two is the walks still in flight plus the ones that ended some other way - a bot
        /// deleted mid-route by a session ending, a Stop from a hand switch - and a gap that grows
        /// without failures growing is its own finding.
        /// </summary>
        public static int WalksStarted
        {
            get { return _started; }
        }

        public static int WalksCompleted
        {
            get { return _completed; }
        }

        /// <summary>Counted by NavWalker.Follow. Gated on NavWalker.Ledger - see that property.</summary>
        public static void NoteWalkStarted()
        {
            _started++;
        }

        /// <summary>Counted by NavWalker.Finish, the single completion point.</summary>
        public static void NoteWalkCompleted()
        {
            _completed++;
        }

        /// <summary>
        /// Events per hundred walks started, or -1 when nothing has walked yet.
        ///
        /// -1 rather than 0, because "no walks, so no failures" and "many walks and no failures"
        /// are opposite findings and a shared 0 would read as the good one.
        /// </summary>
        public static double PerHundredWalks(int events)
        {
            return _started <= 0 ? -1.0 : (events * 100.0) / _started;
        }

        /// <summary>Every failure since the last clear, oldest first.</summary>
        public static IList<NavWalkFailure> All
        {
            get { return _failures.AsReadOnly(); }
        }

        /// <summary>How many there have been, including any the cap evicted.</summary>
        public static int Total
        {
            get { return _total; }
        }

        public static void Clear()
        {
            _failures.Clear();
            _total = 0;

            // The denominator clears with the numerator or the rate is nonsense: a window opened
            // by `walk-failures clear` would otherwise divide this window's failures by every walk
            // since boot.
            _started = 0;
            _completed = 0;
        }

        /// <summary>
        /// WHY this walk ended, in the ledger's own vocabulary, and whether anything fits on the
        /// goal tile at all.
        ///
        /// EXTRACTED SO THERE IS ONE COPY. Record used to compute this inline, and [WalkAudit asks
        /// exactly the same question about a probe that ran out of ladder. Two copies of a
        /// three-way branch would drift the moment either grew a fourth case, and the whole value
        /// of the walk audit is that its causes read against the live ledger's without a
        /// translation table - so the classification is a function and both callers call it.
        ///
        /// The three causes are not degrees of the same thing. `goal-unstandable` is a tile
        /// nothing fits on and a walk that could never have finished. `arrived-no-stand-tile` is a
        /// bot INSIDE the arrival's range that still found nowhere to stand - the place, not the
        /// road. `short-of-goal` never reached the place at all, and its problem is the road
        /// behind it. Merged, they made 28 of 30 failures in one window look like one thing.
        /// </summary>
        public static string CauseFor(
            Mobile mobile, Map map, Point3D goal, int arrivalRange, out bool goalUnstandable)
        {
            goalUnstandable = false;

            if (mobile == null || map == null || map == Map.Internal)
            {
                return "unknown";
            }

            // Mobiles deliberately not counted: the question is whether the TILE can take anybody,
            // not whether somebody is on it right now - that is the near list's job.
            goalUnstandable =
                !map.CanFit(goal.X, goal.Y, goal.Z, 16, false, false, true)
                && !map.CanFit(goal.X, goal.Y, map.GetAverageZ(goal.X, goal.Y), 16, false, false, true);

            if (goalUnstandable)
            {
                return "goal-unstandable";
            }

            // Chebyshev, because that is the metric the walker's own arrival test uses; and at
            // least one, because a range-0 arrival is still "reached" when the bot is on the tile
            // beside it and the single last step is what failed.
            int reach = Math.Max(Math.Abs(mobile.X - goal.X), Math.Abs(mobile.Y - goal.Y));

            return reach <= Math.Max(arrivalRange, 1) ? "arrived-no-stand-tile" : "short-of-goal";
        }

        /// <summary>
        /// Called by NavWalker at the top of the ladder, immediately before the teleport.
        ///
        /// Everything expensive happens here rather than on every rung, because this is rare - a
        /// few dozen an hour across a sixty-bot population - and the two range queries it makes
        /// would not be worth it on a hot path.
        /// </summary>
        public static void Record(
            Mobile mobile, Map map, Point3D goal, string hop, bool watched, int arrivalRange,
            string hopKind, bool fromStart, int index, int routeCount, int skips, Point3D start)
        {
            if (mobile == null || map == null || map == Map.Internal)
            {
                return;
            }

            var failure = new NavWalkFailure
            {
                Utc = DateTime.UtcNow,
                Who = mobile.Name ?? mobile.GetType().Name,
                WhoType = Describe(mobile),
                Hop = hop,
                MapName = map.Name,
                GoalX = goal.X,
                GoalY = goal.Y,
                GoalZ = goal.Z,
                FromX = mobile.X,
                FromY = mobile.Y,
                FromZ = mobile.Z,
                Watched = watched,
                ArrivalRange = arrivalRange,
                HopKind = hopKind,
                FromStart = fromStart,
                Index = index,
                RouteCount = routeCount,
                Skips = skips,
                StartX = start.X,
                StartY = start.Y,
                StartZ = start.Z
            };

            // Shared with [WalkAudit, so a probe's cause and a live bot's cause are the same
            // words by construction rather than by two people keeping two branches in step.
            bool unstandable;

            failure.Cause = CauseFor(mobile, map, goal, arrivalRange, out unstandable);
            failure.GoalUnstandable = unstandable;

            // Centred on the GOAL, which is the whole point. NavWalker's own DescribeBlocker falls
            // back to a list centred on the walker, and a walker that has been shuffling for two
            // minutes is not necessarily anywhere near the tile it wanted.
            IPooledEnumerable nearby = map.GetMobilesInRange(goal, 2);

            try
            {
                foreach (Mobile other in nearby)
                {
                    if (other == mobile || other.Deleted || !other.Alive)
                    {
                        continue;
                    }

                    failure.Near.Add(String.Format(
                        "{0} ({1}) at {2},{3}",
                        other.Name ?? "?",
                        Describe(other),
                        other.X,
                        other.Y));
                }
            }
            finally
            {
                // A non-generic IPooledEnumerable has to be freed or the pool leaks
                // (CLAUDE.md section 14).
                nearby.Free();
            }

            _total++;
            _failures.Add(failure);

            while (_failures.Count > MaxKept)
            {
                _failures.RemoveAt(0);
            }
        }

        /// <summary>
        /// Whether a mobile of this kind consents to being walked through by a bot.
        ///
        /// The one line the shove question reduces to, kept beside Describe so the two can never
        /// disagree about which bucket a mobile is in. NavWalker counts rung entries with it; the
        /// answer is what separates "the world is busy" from "the rules refuse this".
        /// </summary>
        public static bool Shovable(Mobile mobile)
        {
            string kind = Describe(mobile);

            return kind == "PlayerBot" || kind == "daily-life";
        }

        /// <summary>
        /// What KIND of thing this is, in the terms the shove rule is written in.
        ///
        /// This is the classification the whole measurement turns on. A bot walks through another
        /// bot and through a daily-life actor, because both consent through BotShove. It is refused
        /// by a player and by any stock NPC or animal, because those two OnMoveOver overrides are
        /// upstream files and stay as they are. So "who was on the tile" decides whether a failure
        /// was avoidable with the rules we have, or needs the rules to change.
        /// </summary>
        public static string Describe(Mobile mobile)
        {
            if (mobile.Player)
            {
                // A PlayerBot sets Player = true to get past the party gate, so the real-player
                // test has to be the NetState, not the flag. Getting this backwards would file
                // every bot-on-bot jam under "a player was standing there".
                return mobile.NetState != null ? "player" : "PlayerBot";
            }

            var creature = mobile as BaseCreature;

            if (creature == null)
            {
                return mobile.GetType().Name;
            }

            // THE ONE QUESTION THAT MATTERS: does this mobile consent to being shoved?
            //
            // It is IDailyLifeActor, not IBotActor. IBotActor is the BOT's own interface - only
            // PlayerBot implements it, and PlayerBot is already answered above - so testing for it
            // here matched nothing at all, and every daily-life townsfolk, patron and GG vendor
            // came back as "vendor" or "npc": as something a bot cannot push, when it can.
            //
            // That mis-attribution ran the wrong way for the decision it feeds. It inflates the
            // "blocked by something unshovable" bucket, which is the bucket that argues for editing
            // two upstream files - so the fault made the expensive answer look better supported
            // than it is. Caught by reading the names in the first run's output: they were the
            // shopkeepers.
            if (creature is IDailyLifeActor)
            {
                return "daily-life";
            }

            if (creature.Body.IsAnimal || creature.Body.IsMonster)
            {
                return "animal";
            }

            return creature is BaseVendor ? "vendor" : "npc";
        }

        /// <summary>The Data/Live snapshot, written on request rather than on a timer.</summary>
        public static string BuildJson()
        {
            var builder = new System.Text.StringBuilder(4096);

            builder.Append("{\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"total\": ").Append(_total).Append(",\n");
            builder.Append("  \"kept\": ").Append(_failures.Count).Append(",\n");

            // The denominator, so a reader of this file can compute the rate without knowing how
            // long the window was or how many bots were on. See WalksStarted.
            builder.Append("  \"walksStarted\": ").Append(_started).Append(",\n");
            builder.Append("  \"walksCompleted\": ").Append(_completed).Append(",\n");
            builder.Append("  \"per100Walks\": ")
                .Append(PerHundredWalks(_total).ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                .Append(",\n");
            builder.Append("  \"failures\": [\n");

            for (int i = 0; i < _failures.Count; i++)
            {
                NavWalkFailure failure = _failures[i];

                builder.Append("    {\"utc\":").Append(Json.Quote(failure.Utc.ToString("o")));
                builder.Append(",\"who\":").Append(Json.Quote(failure.Who));
                builder.Append(",\"whoType\":").Append(Json.Quote(failure.WhoType));
                builder.Append(",\"hop\":").Append(Json.Quote(failure.Hop));
                builder.Append(",\"map\":").Append(Json.Quote(failure.MapName));
                builder.Append(",\"x\":").Append(failure.GoalX);
                builder.Append(",\"y\":").Append(failure.GoalY);
                builder.Append(",\"z\":").Append(failure.GoalZ);
                builder.Append(",\"fromX\":").Append(failure.FromX);
                builder.Append(",\"fromY\":").Append(failure.FromY);
                builder.Append(",\"fromZ\":").Append(failure.FromZ);
                builder.Append(",\"watched\":").Append(failure.Watched ? "true" : "false");
                builder.Append(",\"goalUnstandable\":").Append(failure.GoalUnstandable ? "true" : "false");
                builder.Append(",\"cause\":").Append(Json.Quote(failure.Cause));
                builder.Append(",\"hopKind\":").Append(Json.Quote(failure.HopKind));
                builder.Append(",\"fromStart\":").Append(failure.FromStart ? "true" : "false");
                builder.Append(",\"range\":").Append(failure.ArrivalRange);
                builder.Append(",\"index\":").Append(failure.Index);
                builder.Append(",\"routeCount\":").Append(failure.RouteCount);
                builder.Append(",\"skips\":").Append(failure.Skips);
                builder.Append(",\"startX\":").Append(failure.StartX);
                builder.Append(",\"startY\":").Append(failure.StartY);
                builder.Append(",\"startZ\":").Append(failure.StartZ);
                builder.Append(",\"near\":[");

                for (int n = 0; n < failure.Near.Count; n++)
                {
                    if (n > 0)
                    {
                        builder.Append(",");
                    }

                    builder.Append(Json.Quote(failure.Near[n]));
                }

                builder.Append("]}");

                if (i < _failures.Count - 1)
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

            if (!AtomicFile.Write("Data/Live/walk-failures.json", BuildJson(), out error))
            {
                message = error;
                Log.Error("Could not write walk-failures.json: {0}", error);
                return false;
            }

            message = String.Format(
                "{0} walk failure(s) since boot, {1} kept; {2} walk(s) started, {3} completed{4}",
                _total,
                _failures.Count,
                _started,
                _completed,
                _started > 0
                    ? String.Format(" ({0:F2} failures per 100 walks)", PerHundredWalks(_total))
                    : "");

            return true;
        }
    }
}
