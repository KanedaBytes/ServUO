// NavWalkAudit.cs - the second instrument: real walkers, walking.
//
// WHY THIS EXISTS, AND WHY [NavAudit CANNOT BE IT
// ----------------------------------------------
// [NavAudit asks the ENGINE whether a step could work. It hands MovementPath a Point3D and reads
// the answer, and its own README lists the three things that answer cannot see:
//
//   - a Point3D probe sets checkMobs false, so every mobile in the world is invisible to it;
//   - a Point3D probe is refused by a closed door that a walker with CanOpenDoors walks through;
//   - and above all, the probe starts on the AUTHORED WAYPOINT, exactly, while a real walker
//     starts wherever its last hop stopped - anywhere inside the arrival box.
//
// The third is the one that matters most, because FastAStarAlgorithm is greedy with a 300-node
// budget and therefore DIRECTIONAL: the same tiles path from nine tiles out and do not from ten.
// So a clean audit report is compatible with bots failing walks all day, and four measured windows
// have now been read against exactly that - 655 edges, 0 blocked, 0 over cap, and a failure ledger
// with entries in it.
//
// This asks the other question. It spawns probe walkers and makes them walk every edge in both
// directions and every arrival from each of its approach waypoints, and records what happened to
// each: pass or fail, how many steps it took against the straight line, how long, and on a failure
// where it stopped, in whose way, and which rungs it burned getting there.
//
// WHAT THE REFERENCE HAS, AND WHY THIS IS NOT A PORT OF IT
// --------------------------------------------------------
// uo-offline has no whole-graph walk audit. Its [auditedges (CustomBots/AuditEdgesCommand.cs) is a
// flood-fill geometry check - our [NavAudit, arrived at independently and with the same two
// verdicts, BLOCKED and FAR. Its [testroute (Nav/HpaCommands.cs:58) plans an abstract route and
// walks nothing. The one thing in that tree that moves a real mobile to answer a navigation
// question is [testapproach (Nav/FieldCommands.cs:88): ONE nearby PlayerBot, ONE destination, the
// final approach only, and its own header calls it "Diagnostic only".
//
// Their fleet-wide instrument is passive rather than active - BotNavWatch plus StuckTelemetry,
// aggregating what the live population happened to do into Data/Live/stuck_report.json as
// { asOf, bootedAt, windowMinutes, window:{kind->n}, total:{kind->n}, hotspots:[...], edges:[...] }
// (BotStuckTelemetry.cs:200-245). walk-audit.json mirrors that header and its `edges` array, and
// adds the per-walk rows their shape has no place for, because theirs reports a WINDOW and this
// reports a SWEEP. A window tells you which roads the fleet happened to try; a sweep tells you
// about the ones nobody has walked yet.
//
// THREE PROPERTIES THIS HAD TO HAVE
// ---------------------------------
//   1. It leaves nothing behind. Probes are deleted in a finally, they consent to being walked
//      through so they cannot manufacture each other's obstructions, and they carry
//      NavWalker.Ledger = false so a fourteen-hundred-walk sweep does not swamp the fleet's own
//      failure rate or strike every edge it found hard.
//   2. Its vocabulary is the ledger's, by construction. The cause comes from
//      NavWalkFailures.CauseFor and the blocker from NavWalker.NextStepBlocker - the same two
//      functions a live bot's failure goes through - so a walk-audit row reads against
//      walk-failures.json without a translation table.
//   3. Occupancy is separable from geometry. A bot standing in a doorway is the world being busy;
//      a wall is a road that does not exist. Merged, the second is invisible inside the first, so
//      a row that failed with somebody on the next-step tile is counted apart.

using System;
using System.Collections.Generic;
using System.Text;

using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>One edge or arrival, walked.</summary>
    public sealed class NavWalkAuditRow
    {
        /// <summary>"edge" or "arrival".</summary>
        public string Kind;

        /// <summary>The waypoint walked from. Both kinds have one.</summary>
        public string From;

        /// <summary>The waypoint walked to. Null for an arrival, which is not a graph node.</summary>
        public string To;

        /// <summary>The destination an arrival belongs to. Null for an edge.</summary>
        public string Destination;

        public string MapName;

        public bool Pass;

        /// <summary>Steps the mobile actually took, counted by the probe's own Move override.</summary>
        public int Steps;

        /// <summary>Chebyshev distance from start to goal - the straight line the road is measured against.</summary>
        public int Tiles;

        public double Seconds;

        public int StartX;
        public int StartY;
        public int StartZ;

        public int GoalX;
        public int GoalY;
        public int GoalZ;

        /// <summary>Where the probe was standing when the walk ended.</summary>
        public int StopX;
        public int StopY;
        public int StopZ;

        /// <summary>The arrival's own range, so "within range" is reproducible from the file.</summary>
        public int Range;

        /// <summary>
        /// NavWalkFailures' vocabulary - goal-unstandable / arrived-no-stand-tile / short-of-goal.
        /// Null on a pass, and ALWAYS one of those three on a failure, however the walk ended.
        /// </summary>
        public string Cause;

        /// <summary>
        /// HOW the walk ended: "teleport" (the walker climbed the whole ladder and gave up) or
        /// "timeout" (the audit's deadline passed with it still trying) or "abandoned".
        ///
        /// SEPARATE FROM Cause, because they answer different questions and the first version of
        /// this conflated them. Cause is about the GOAL - is it standable, did the mobile reach
        /// the place, is it short of it - and it is a fact about the data. EndedBy is about the
        /// AUDIT - did the walker give up or did the clock run out - and it is a fact about how
        /// long this run was willing to wait. Merged into one field, every hard failure came back
        /// as "timeout" and the ledger's vocabulary never appeared at all, which is the whole
        /// value of sharing that vocabulary gone.
        /// </summary>
        public string EndedBy;

        /// <summary>"Jeanette (npc) at 1457,1526" when a mobile stood on the next-step tile.</summary>
        public string Occupied;

        /// <summary>Whether that mobile is one a bot may not push. Decides the occupancy split.</summary>
        public bool OccupiedUnshovable;

        /// <summary>"Repath 2, Sidestep 1" - which rungs this walk burned. Empty when it was clean.</summary>
        public string Rungs;

        public int RungTotal;

        /// <summary>
        /// How many tiles the ENGINE's own route for this hop is, asked once before the walk.
        ///
        /// THE STABLE HALF, and the reason it exists is that the step count is not stable. See
        /// Ratio.
        /// </summary>
        public int PathTiles;

        /// <summary>
        /// The detour factor: the engine's route length over the authored straight line.
        ///
        /// THIS IS THE RE-BASE EVIDENCE, and it took three sweeps to arrive at because the obvious
        /// number is the wrong one. Steps over straight line is what a walker actually did, and on
        /// a shard with sixty bots walking about it is dominated by them: measured, the top of that
        /// list RESHUFFLED COMPLETELY between two consecutive sweeps of an unchanged graph -
        /// brit-tav-1 -> brit-prov-4 at 21.7 in one run and absent from the top twenty in the next,
        /// brit-tink-1 -> brit-shop-tinker at 30.0 in the second and 5.4 in the first. A list that
        /// reorders itself every run is not evidence of anything about the road.
        ///
        /// The engine's planned route is deterministic, contention-free and costs one MovementPath.
        /// It is also the exact question the re-base asks: an edge authored at twelve tiles whose
        /// real road is thirty walks a bot round three sides of a building EVERY TIME, whoever else
        /// is on the map.
        ///
        /// StepRatio keeps the other number, because "what actually happened" is worth having next
        /// to "what was always going to happen" - the gap between them IS the contention.
        /// </summary>
        public double Ratio
        {
            get { return Tiles <= 0 ? 0.0 : PathTiles / (double)Tiles; }
        }

        /// <summary>Steps actually taken over the straight line. Contention included, by nature.</summary>
        public double StepRatio
        {
            get { return Tiles <= 0 ? 0.0 : Steps / (double)Tiles; }
        }

        public string Label
        {
            get
            {
                return Kind == "arrival"
                    ? String.Format("{0} -> {1} (arrival)", From, Destination)
                    : String.Format("{0} -> {1}", From, To);
            }
        }
    }

    public static class NavWalkAudit
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        public const string SnapshotPath = "Data/Live/walk-audit.json";

        /// <summary>
        /// How many probes walk at once. Twelve, because the work is wall-clock-bound rather than
        /// CPU-bound - each probe spends its time waiting for step delays - and because twelve
        /// spread over a shuffled work list are rarely in the same town at the same moment.
        /// </summary>
        public static int Probes
        {
            get { return Math.Max(1, Math.Min(64, Config.Get("Custom.WalkAuditProbes", 12))); }
        }

        /// <summary>
        /// The hard deadline on one walk.
        ///
        /// The recovery ladder runs normally during an audit - that IS the behaviour under test,
        /// and a pass that took three rungs is a different fact from a clean pass - but a walker
        /// that never reaches the top rung would otherwise hold a probe for as long as it liked.
        ///
        /// A HUNDRED AND TWENTY, AND THE FIRST NUMBER WAS FORTY-FIVE, WHICH WAS TOO SHORT BY
        /// CONSTRUCTION. The ladder is five rungs twenty seconds apart, so a walk that is going to
        /// give up needs a hundred seconds to do it; a forty-five second deadline cut every hard
        /// failure off before the top rung, which meant the audit never saw the Failed seam and
        /// never reported one of the ledger's causes. Caught by the self-test: the water under the
        /// Trinsic pier is goal-unstandable by construction and came back as `timeout`.
        ///
        /// It is the frozen watchdog's window too (six hop timeouts, Bots/README.md), which is the
        /// same quantity asked from the other side - how long before a mobile that is not getting
        /// anywhere is definitely not getting anywhere.
        /// </summary>
        public static TimeSpan WalkTimeout
        {
            get
            {
                return TimeSpan.FromSeconds(
                    Math.Max(10, Config.Get("Custom.WalkAuditWalkSeconds", 120)));
            }
        }

        /// <summary>How often the driver looks at its probes. Short: a walk can end at any tick.</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(0.25);

        private static Job _job;

        /// <summary>True while a sweep is running. A second [WalkAudit is refused rather than queued.</summary>
        public static bool IsRunning
        {
            get { return _job != null; }
        }

        // ---- the probe -------------------------------------------------------------------------

        /// <summary>
        /// The stand-in for the bot that will walk this road.
        ///
        /// Modelled on NavCorridor.CorridorProbe and for its reason: a non-animal Body is what
        /// makes CanOpenDoors true, and therefore what makes FastAStarAlgorithm ignore doors the
        /// way it does for a real bot. Three things are its own:
        ///
        ///   MOUNTED PACE, without a mount. Mobile.RunMount is 100 ms, so a twelve-tile hop is
        ///   about a second and a half and the whole graph is minutes rather than an afternoon.
        ///   Read off Server/Mobile.cs:3063-3071 directly rather than through BotMovement.DelayFor,
        ///   because this file is Core and Core must not reach up into Bots.
        ///
        ///   IT CONSENTS TO EVERYTHING. Twelve probes walking one town would otherwise refuse each
        ///   other's steps exactly as two stock NPCs do, and every one of those would be recorded
        ///   as an occupied road. A probe is an instrument and must not appear in its own readings.
        ///
        ///   IT COUNTS ITS STEPS. Nothing else does - NavWalker knows hops, not steps - and steps
        ///   against the straight line is the ratio the whole re-base argument rests on.
        /// </summary>
        private class WalkAuditProbe : BaseCreature, IBotMover
        {
            private int _steps;

            public WalkAuditProbe()
                : base(AIType.AI_Melee, FightMode.None, 10, 1, 0.2, 0.4)
            {
                Body = 0x190;
                Name = "a walk probe";

                Blessed = true;
                Hidden = true;
                CantWalk = false;

                // Nothing should react to it: not a player, not a guard, not another creature.
                Karma = 0;
                Fame = 0;

                // Both speeds, not just the active one: BaseAI picks between ActiveSpeed and
                // PassiveSpeed by its own ActionType, and a walker crossing a town is "wandering"
                // as far as the AI is concerned - the same trap BotMovement documents.
                double delay = Mobile.RunMount / 1000.0;

                ActiveSpeed = delay;
                PassiveSpeed = delay;
                CurrentSpeed = delay;
            }

            public WalkAuditProbe(Serial serial)
                : base(serial)
            {
            }

            public override bool CanOpenDoors { get { return true; } }

            /// <summary>
            /// THE PROBE NEVER WANDERS, AND THIS IS THE ONLY LEVER THAT ACTUALLY STOPS IT.
            ///
            /// BaseAI.DoActionWander is what a FightMode.None creature does on nearly every AI
            /// tick, and it ends in WalkRandomInHome (BaseAI.cs:1061-1066). Every step that takes
            /// is a step this probe's own counter records - so a wandering probe does not merely
            /// add noise to the steps-per-tile ratio, it MANUFACTURES the finding that ratio
            /// exists to report.
            ///
            /// Measured, on three sweeps of the same graph:
            ///
            ///   Home at Point3D.Zero, RangeHome default. The wander falls through to WalkRandom
            ///   (BaseAI.cs:2516-2543) - pure random steps. Worst ratio 82:1 on a ten-tile hop,
            ///   822 steps in 82 seconds.
            ///
            ///   KeepHomeAligned with RangeHome = 0, so the wander became `DoMove(toward Home)`
            ///   with Home on the goal. Better - worst ratio 21.7 - but still one step per tick
            ///   for fifteen seconds on a seven-tile hop, because GetDirectionTo is a STRAIGHT
            ///   LINE: the wander walks into the wall the walker's PathFollower is routing round,
            ///   the two alternate, and half the steps are spent bouncing.
            ///
            ///   This. CheckIdle is the gate DoActionWander asks before it wanders at all, it is
            ///   virtual on BaseCreature (BaseCreature.cs:4735), and a probe that is always idling
            ///   never takes a step that NavWalker did not ask for.
            ///
            /// Nothing else reads CheckIdle for a creature with no combatant, so there is nothing
            /// else to break.
            /// </summary>
            public override bool CheckIdle()
            {
                return true;
            }

            public int Steps { get { return _steps; } }

            public void ResetSteps()
            {
                _steps = 0;
            }

            /// <summary>The only honest source for how far this walk actually went on foot.</summary>
            public override bool Move(Direction d)
            {
                bool moved = base.Move(d);

                if (moved)
                {
                    _steps++;
                }

                return moved;
            }

            /// <summary>
            /// A probe is transparent. See the class comment: an instrument that obstructs the
            /// thing it is measuring reports its own presence as a fault in the road.
            /// </summary>
            public override bool OnMoveOver(Mobile m)
            {
                return true;
            }

            /// <summary>
            /// AND IT SHOVES LIKE ONE, which the transparency above does not buy.
            ///
            /// OnMoveOver is the probe consenting to be walked THROUGH, and it only ever covered
            /// probe-on-probe in practice. The other direction was refused by everybody: BotShove
            /// keyed its mover branch on `PlayerBot`, a probe is a plain BaseCreature, so a probe
            /// stepping onto a GG vendor, a daily-life patron, a stock NPC (through MODIFICATIONS
            /// entry 5's guard, which calls the same predicate) or another bot was turned away
            /// where a real bot walks straight through.
            ///
            /// That is one-way strictness, and it falsifies in the direction that matters: every
            /// one of those refusals cost the probe a rung, and a pass that climbed a rung is what
            /// the FRAGILE list is. An instrument must be neither stricter nor looser than the
            /// thing it stands in for, and this was the stricter half.
            ///
            /// `true` unconditionally, which is uo-offline's PlayerBot.CheckShove verbatim and is
            /// what PlayerBot answers here too.
            /// </summary>
            public override bool CheckShove(Mobile shoved)
            {
                return true;
            }

            public override void Serialize(GenericWriter writer)
            {
                base.Serialize(writer);
                writer.Write(0);
            }

            public override void Deserialize(GenericReader reader)
            {
                base.Deserialize(reader);
                reader.ReadInt();

                // Belt and braces, as CorridorProbe does. These are created and deleted inside one
                // run, so none should ever reach a save - but a crash mid-sweep would otherwise
                // leave twelve hidden mobiles in the world for ever.
                Timer.DelayCall(Delete);
            }
        }

        // ---- the work list ---------------------------------------------------------------------

        /// <summary>One thing to walk: from a tile, to a tile, and what to call it.</summary>
        private sealed class Item
        {
            public string Kind;
            public string From;
            public string To;
            public string Destination;
            public Map Map;
            public Point3D Start;
            public Point3D Goal;
            public int Range;

            /// <summary>The engine's route length, filled in by Begin before the probe moves.</summary>
            public int PathTiles;

            /// <summary>The step handed to the walker. Built once here so the row and the walk agree.</summary>
            public NavStep Step;

            /// <summary>The authored straight line this hop is measured against.</summary>
            public int Tiles()
            {
                return NavGraph.Chebyshev(Start, Goal);
            }
        }

        /// <summary>A probe and the item it is currently walking.</summary>
        private sealed class Runner
        {
            public WalkAuditProbe Probe;
            public NavWalker Walker;
            public Item Item;
            public long StartedTick;
            public bool Failed;
            public string FailCause;
            public bool Done;

            /// <summary>
            /// The first rung reason on this walk that named somebody on the next-step tile.
            ///
            /// CAPTURED FOR PASSES TOO, which is the point of it. The occupancy question is asked
            /// at the end for a failure - who is standing where the walk died - but a walk that
            /// PASSED after twenty seconds of shuffling has no such moment: by the time it
            /// finishes the probe is on the goal and whoever was in its way has been walked
            /// through or has moved on. Measured on the first full sweep: brit-bank-w ->
            /// brit-bank-0 is six tiles and took 202 steps in 20.7s, and the only reason anybody
            /// can tell that from a long road is that a rung fired and named the Minter standing
            /// on it. LogRung already builds the string; this keeps the first one that has a
            /// blocker in it.
            /// </summary>
            public string BlockedReason;

            /// <summary>
            /// Where the probe was standing when the walk gave up, captured in the Failed seam.
            ///
            /// NOT READ OFF THE PROBE AFTERWARDS, and it was, and that was wrong. Teleport moves
            /// the mobile ONTO the goal and only then does the route run out, so a stop tile read
            /// at completion is the goal tile for every rescued walk - the one place that is
            /// demonstrably fine. Measured: all three of one sweep's failures reported a stop tile
            /// identical to their goal, which read as "it arrived and failed anyway" and is not
            /// what happened.
            ///
            /// This is the same reason NavWalkFailures.Record is called before the teleport rather
            /// than after it, met from the other side.
            /// </summary>
            public Point3D StoppedAt;

            public bool HaveStopped;
        }

        private sealed class Job
        {
            public readonly List<Item> Pending = new List<Item>();
            public readonly List<NavWalkAuditRow> Rows = new List<NavWalkAuditRow>();
            public readonly List<Runner> Runners = new List<Runner>();

            public int Skipped;
            public int Total;
            public long StartedTick;

            /// <summary>When progress was last written out. See Step.</summary>
            public long WroteTick;
            public Timer Poll;
            public Mobile Caller;
            public bool SelfTest;

            /// <summary>
            /// The approach-tile scan, run once before the probes go out. Null on a self-test,
            /// which seeds two hand-written walks and has no graph to scan.
            /// </summary>
            public NavNeighbourhoodResult Cliffs;
        }

        // ---- starting ---------------------------------------------------------------------------

        /// <summary>
        /// Build the work list and start walking it.
        ///
        /// GAME THREAD ONLY, like everything else in this folder: it constructs mobiles, reads the
        /// nav store and runs MovementPath, whose working set is shared static state on a
        /// singleton. The token path reaches it through RequestPoller.Poll, which is already a
        /// Timer callback.
        /// </summary>
        public static bool TryStart(Mobile caller, int probes, bool selfTest, out string error)
        {
            error = null;

            if (_job != null)
            {
                error = "a walk audit is already running";
                return false;
            }

            if (World.Saving || World.Loading)
            {
                error = "the world is saving or loading";
                return false;
            }

            var job = new Job { Caller = caller, SelfTest = selfTest, StartedTick = Core.TickCount };

            if (selfTest)
            {
                AddSelfTest(job);
            }
            else
            {
                BuildWorkList(job);

                // THE APPROACH TILES, before a single probe is spawned.
                //
                // Engine-only and synchronous, unlike everything below it, and it belongs here
                // rather than in the walk because it asks a question the walk structurally cannot:
                // every item in the list above starts ON an authored waypoint, and a real bot
                // starts wherever its last hop stopped. See NavNeighbourhood.
                //
                // Its cost is reported beside the sweep's rather than hidden inside it, because
                // the two are paid for different things and a reader deciding whether to run this
                // again needs to see both. The default [NavAudit deliberately does NOT run it -
                // the editor re-runs that after every save - so a walk audit and [NavAudit full
                // are the only two places it happens.
                job.Cliffs = NavNeighbourhood.Scan(Map.Trammel);

                Log.Info("Walk audit approach-tile scan: {0}", job.Cliffs.Summary);
            }

            if (job.Pending.Count == 0)
            {
                error = "nothing to walk";
                return false;
            }

            job.Total = job.Pending.Count;

            // Shuffled, so twelve concurrent probes are spread over the facet rather than queued
            // behind one another through a single plaza. The work list comes out of the store in
            // graph order, which is very nearly geographic order.
            Shuffle(job.Pending);

            int count = Math.Min(probes > 0 ? probes : Probes, job.Pending.Count);

            for (int i = 0; i < count; i++)
            {
                job.Runners.Add(new Runner());
            }

            _job = job;

            job.WroteTick = Core.TickCount;
            WriteSnapshot(job, false);

            Log.Info(
                "Walk audit starting: {0} walk(s) over {1} probe(s).", job.Total, job.Runners.Count);

            job.Poll = Timer.DelayCall(PollInterval, PollInterval, () => Step(job));

            return true;
        }

        /// <summary>
        /// Every edge in both directions, and every arrival from each of its approach waypoints.
        ///
        /// BOTH DIRECTIONS because reachability is directional here - FastAStarAlgorithm is greedy
        /// with a budget, and the README's own measurement has the same two tiles pathing one way
        /// and not the other. [NavAudit tests both for the same reason.
        ///
        /// EVERY APPROACH, not one, because Nav.TryRouteFrom refuses a start with no waypoint
        /// inside the hop cap: an arrival that only one approach can reach is a one-way trip from
        /// every other, and "does this arrival work" has as many answers as it has approaches.
        /// </summary>
        private static void BuildWorkList(Job job)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (NavEdge edge in NavigationSystem.Store.Edges)
            {
                if (edge.Kind != NavEdgeKind.Walk)
                {
                    continue;
                }

                NavWaypoint a = Nav.Waypoint(edge.From);
                NavWaypoint b = Nav.Waypoint(edge.To);

                if (a == null || b == null || a.Map == null || a.Map != b.Map)
                {
                    continue;
                }

                // Edges are symmetrised in the graph, so take each unordered pair once and then
                // emit BOTH directions from it - otherwise a symmetrised pair produces four walks.
                string key = String.CompareOrdinal(a.Id, b.Id) < 0
                    ? a.Id + ">" + b.Id
                    : b.Id + ">" + a.Id;

                if (!seen.Add(key))
                {
                    continue;
                }

                job.Pending.Add(EdgeItem(a, b));
                job.Pending.Add(EdgeItem(b, a));
            }

            foreach (NavDestination destination in NavigationSystem.Store.Destinations)
            {
                if (destination == null || destination.Map == null)
                {
                    continue;
                }

                // A pending-road destination is one somebody has already accepted a gap to. Walking
                // it would report a fault that is on a list rather than a fault nobody knows about,
                // and a list everybody skims is a list with the real warning hidden in it.
                if (destination.HasTag(NavigationSystem.PendingRoadTag))
                {
                    job.Skipped += destination.ArrivalList == null ? 1 : destination.ArrivalList.Count;
                    continue;
                }

                if (destination.ArrivalList == null)
                {
                    continue;
                }

                foreach (NavArrival arrival in destination.ArrivalList)
                {
                    if (arrival == null)
                    {
                        continue;
                    }

                    foreach (NavWaypoint approach in ApproachesFor(destination, arrival))
                    {
                        job.Pending.Add(ArrivalItem(destination, arrival, approach));
                    }
                }
            }
        }

        /// <summary>
        /// The waypoints an arrival declares, falling back to the destination's, falling back to
        /// the nearest inside the hop cap.
        ///
        /// The same order Nav.TryRoute resolves a destination in - declared first, never the other
        /// way round - so the audit walks the approaches a bot would actually be given.
        /// </summary>
        private static List<NavWaypoint> ApproachesFor(NavDestination destination, NavArrival arrival)
        {
            var found = new List<NavWaypoint>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddApproaches(found, seen, arrival.WaypointList, destination.Map);
            AddApproaches(found, seen, destination.WaypointList, destination.Map);

            if (found.Count == 0)
            {
                NavWaypoint nearest = Nav.NearestWaypoint(
                    arrival.Location, destination.Map, NavigationSystem.HopMaxTiles);

                if (nearest != null)
                {
                    found.Add(nearest);
                }
            }

            return found;
        }

        private static void AddApproaches(
            List<NavWaypoint> into, HashSet<string> seen, string[] ids, Map map)
        {
            if (ids == null)
            {
                return;
            }

            for (int i = 0; i < ids.Length; i++)
            {
                NavWaypoint waypoint = Nav.Waypoint(ids[i]);

                if (waypoint != null && waypoint.Map == map && seen.Add(waypoint.Id))
                {
                    into.Add(waypoint);
                }
            }
        }

        private static Item EdgeItem(NavWaypoint from, NavWaypoint to)
        {
            Point3D goal = new Point3D(
                to.Location.X, to.Location.Y, NavWalker.ResolveZ(to.Map, to.Location));

            return new Item
            {
                Kind = "edge",
                From = from.Id,
                To = to.Id,
                Map = from.Map,
                Start = new Point3D(
                    from.Location.X, from.Location.Y, NavWalker.ResolveZ(from.Map, from.Location)),
                Goal = goal,
                Range = to.ArrivalRange,
                Step = new NavStep(to.Location, to.Map, to.Id, NavStepKind.Walk)
            };
        }

        private static Item ArrivalItem(
            NavDestination destination, NavArrival arrival, NavWaypoint approach)
        {
            Point3D goal = new Point3D(
                arrival.Location.X,
                arrival.Location.Y,
                NavWalker.ResolveZ(destination.Map, arrival.Location));

            return new Item
            {
                Kind = "arrival",
                From = approach.Id,
                Destination = destination.Id,
                Map = destination.Map,
                Start = new Point3D(
                    approach.Location.X,
                    approach.Location.Y,
                    NavWalker.ResolveZ(approach.Map, approach.Location)),
                Goal = goal,
                Range = arrival.Range,
                Step = new NavStep(
                    arrival.Location, destination.Map, null, NavStepKind.Arrival, arrival.Range)
            };
        }

        /// <summary>
        /// ONE HOP THE AUDIT IS REQUIRED TO FAIL, so a green report can be believed.
        ///
        /// An instrument that reads zero because it is broken looks exactly like an instrument
        /// reading zero because everything is fine, and the whole point of this file is that
        /// [NavAudit's clean 655 rows were the first kind of zero about a question it could not
        /// ask. So: a hop straight through a building, walked with everything else.
        ///
        /// The tile is brit-shop-tinker's old arrival, 1421,1651 - a plaster wall static 0x0134
        /// twenty tall, where CanFit is false and all eight neighbour steps are refused, measured
        /// last session and the reason that destination was moved. The walk starts on the road
        /// outside it. If this row comes back a PASS, the audit is not walking anything.
        ///
        /// A SEEDED ITEM, NOT A SEEDED EDGE. The obvious way to do this is to add the edge to
        /// navigation.json, run, and take it out again - and a session that crashes in between
        /// leaves a road through a wall in the shard's data. The work list is the only thing that
        /// needs to know, so it is the only thing that is told.
        ///
        /// TWO ROWS, NOT ONE: one that must FAIL and one that must PASS.
        ///
        /// A test that can only say one word cannot tell a working instrument from one that is
        /// stuck saying that word. The control is a two-tile hop along the road outside the
        /// Trinsic alchemist; the seed is the water under the Trinsic pier.
        ///
        /// TWO EARLIER SEEDS PASSED, BOTH OF THEM CORRECTLY, AND THAT IS WORTH KEEPING.
        ///
        ///   1. A Walk step aimed at brit-shop-tinker's old arrival, 1421,1651 - a plaster wall
        ///      static twenty tall. It passed: the probe stopped at 1423,1653 and the walker
        ///      called that arrived, because ArrivalRangeFor gives a Walk step its waypoint's
        ///      tolerance and DefaultArrivalRange (2) when there is no such waypoint. "Walk into a
        ///      wall" with two tiles of latitude is a goal you can meet from outside the wall.
        ///
        ///   2. The same tile as an Arrival step at range 0, which forces the exact tile. It
        ///      passed too, at 1422,1651 - because TryShiftWithinArrival did precisely what it was
        ///      built to do last session and retargeted onto the standable, pathable neighbour.
        ///
        /// Both were the shard being right and the test being wrong, and a self-test that is wrong
        /// in that direction is the only kind that matters here. What they show is that with the
        /// recovery ladder and the arrival shift both running, very nearly nothing is impossible -
        /// so the seed has to be a tile where the shift has nowhere to shift TO.
        ///
        /// 2072,2865 is that tile, measured rather than assumed ([TileProbe): land 0x0064
        /// Impassable at z -15, static 0x1797 'water', CanFit false, TryResolveZ NO, and all eight
        /// neighbour steps refused for a creature AND for a point. It is the tile the README's
        /// trinsic-dock-2 cascade was aimed at - eight of one window's seventeen failures - and it
        /// is still water, because that fault was fixed by moving the arrival off it rather than
        /// by filling in the sea. So the expected cause is `goal-unstandable`, which exercises the
        /// classifier as well as the walker.
        ///
        /// A SEEDED ITEM, NOT A SEEDED EDGE. The obvious way to do this is to add the edge to
        /// navigation.json, run, and take it out again - and a session that crashes in between
        /// leaves a road through a wall in the shard's data. The work list is the only thing that
        /// needs to know, so it is the only thing that is told.
        /// </summary>
        private static void AddSelfTest(Job job)
        {
            Map map = Map.Trammel;

            // MUST FAIL. Open water under the Trinsic pier, with no standable neighbour.
            var drowned = new Point3D(2072, 2865, NavWalker.ResolveZ(map, new Point3D(2072, 2865, -15)));

            job.Pending.Add(new Item
            {
                Kind = "arrival",
                From = "selftest-pier",
                Destination = SelfTestMustFail,
                Map = map,
                Start = new Point3D(2070, 2863, NavWalker.ResolveZ(map, new Point3D(2070, 2863, -2))),
                Goal = drowned,
                Range = 0,
                Step = new NavStep(drowned, map, null, NavStepKind.Arrival, 0)
            });

            // MUST PASS. Two tiles of road at the Trinsic alchemist, verified last session as
            // standable and reachable from every one of its eight neighbours.
            var road = new Point3D(1849, 2711, NavWalker.ResolveZ(map, new Point3D(1849, 2711, 11)));

            job.Pending.Add(new Item
            {
                Kind = "arrival",
                From = "selftest-road",
                Destination = SelfTestMustPass,
                Map = map,
                Start = new Point3D(1851, 2712, NavWalker.ResolveZ(map, new Point3D(1851, 2712, 11))),
                Goal = road,
                Range = 0,
                Step = new NavStep(road, map, null, NavStepKind.Arrival, 0)
            });
        }

        private const string SelfTestMustFail = "selftest-must-fail-water-under-the-pier";

        private const string SelfTestMustPass = "selftest-must-pass-road";

        /// <summary>
        /// Did the self-test pass AS A TEST - the seed failed and the control passed?
        ///
        /// Separate from the rows' own pass/fail and deliberately inverted for one of them,
        /// because "the audit reported a failure" is the SUCCESS condition for the seed. A caller
        /// reading `edges.failed &gt; 0` would have to know that, and the one thing a self-test must
        /// not need is a reader who already knows what it means.
        /// </summary>
        private static string SelfTestVerdict(Job job)
        {
            bool seedFailed = false;
            bool controlPassed = false;
            string seedCause = "(the seed row is missing)";

            foreach (NavWalkAuditRow row in job.Rows)
            {
                if (row.Destination == SelfTestMustFail)
                {
                    seedFailed = !row.Pass;
                    seedCause = row.Cause ?? "passed, which it must not";
                }
                else if (row.Destination == SelfTestMustPass)
                {
                    controlPassed = row.Pass;
                }
            }

            if (seedFailed && controlPassed)
            {
                return String.Format(
                    "SELF-TEST OK: the unwalkable seed failed with '{0}' and the control passed.",
                    seedCause);
            }

            return String.Format(
                "SELF-TEST BROKEN: seed {0} (cause '{1}'), control {2}. "
                + "Do not trust a walk audit until this reads OK.",
                seedFailed ? "failed as required" : "PASSED and must not have",
                seedCause,
                controlPassed ? "passed" : "FAILED and should not have");
        }

        // ---- driving ----------------------------------------------------------------------------

        private static void Step(Job job)
        {
            if (_job != job)
            {
                return;
            }

            try
            {
                for (int i = 0; i < job.Runners.Count; i++)
                {
                    Service(job, job.Runners[i]);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Walk audit step threw; stopping.");
                Finish(job);
                return;
            }

            // PROGRESS, PERIODICALLY, because the only other write is at the end.
            //
            // The editor's Walk audit button polls this file for `status`, and a sweep of fourteen
            // hundred walks takes minutes - so a file written once at the start and once at the
            // end shows "0 of 1450" for the whole run and then jumps to done. That is
            // indistinguishable from a run that has hung, which is the one thing a long-running
            // job's progress indicator must never be.
            //
            // Five seconds: a walk is seconds and a failure is a hundred, so nothing meaningful
            // happens faster, and the write is a whole-file serialise of every row so far.
            if (Core.TickCount - (job.WroteTick + 5000) >= 0)
            {
                job.WroteTick = Core.TickCount;
                WriteSnapshot(job, false);
            }

            if (job.Pending.Count > 0)
            {
                return;
            }

            for (int i = 0; i < job.Runners.Count; i++)
            {
                if (job.Runners[i].Item != null)
                {
                    return;
                }
            }

            Finish(job);
        }

        /// <summary>Advance one probe: finish what it is on, then give it the next thing.</summary>
        private static void Service(Job job, Runner runner)
        {
            if (runner.Item != null)
            {
                if (!TryComplete(job, runner))
                {
                    return;
                }
            }

            if (job.Pending.Count == 0)
            {
                return;
            }

            Item item = job.Pending[job.Pending.Count - 1];
            job.Pending.RemoveAt(job.Pending.Count - 1);

            Begin(job, runner, item);
        }

        private static void Begin(Job job, Runner runner, Item item)
        {
            if (runner.Probe == null || runner.Probe.Deleted)
            {
                runner.Probe = new WalkAuditProbe();

                runner.Walker = new NavWalker(runner.Probe);

                // The whole reason this property exists. See NavWalker.Ledger.
                runner.Walker.Ledger = false;

                runner.Walker.Run = false;
                runner.Walker.Arrived = walker => { };
            }

            runner.Item = item;
            runner.Failed = false;
            runner.FailCause = null;
            runner.BlockedReason = null;
            runner.HaveStopped = false;
            runner.Done = false;
            runner.StartedTick = Core.TickCount;

            runner.Probe.ResetSteps();
            runner.Probe.MoveToWorld(item.Start, item.Map);

            // THE ENGINE'S OWN ROUTE, asked once, from the start tile, before anything moves.
            //
            // Here rather than in BuildWorkList because MovementPath needs a real mobile standing
            // at the real start - it is the same call NavWalker's MoveTo makes on every step, and
            // asking it from anywhere else would answer a different question. One path per row.
            //
            // A hop of one tile has no path: MovementPath returns nothing for an adjacent goal
            // (MovementPath.cs:34), which is why [NavAudit skips those too. Its route length is its
            // straight line by definition, so say so rather than recording a zero that would read
            // as a detour factor of zero.
            item.PathTiles = 0;

            if (item.Tiles() <= 1)
            {
                item.PathTiles = item.Tiles();
            }
            else
            {
                var path = new MovementPath(runner.Probe, item.Goal);

                if (path.Success && path.Directions != null)
                {
                    item.PathTiles = path.Directions.Length;
                }
            }

            NavWalker walker = runner.Walker;

            walker.ResetRungs();

            // Captured rather than read back off the walker afterwards: Teleport is followed by
            // Advance, which on a one-step route reaches Finish and raises Arrived, so by the time
            // anybody looks the walker has stopped and both endings are indistinguishable.
            walker.Failed = (w, step, cause) =>
            {
                runner.Failed = true;
                runner.FailCause = cause;

                // BEFORE THE TELEPORT, which is the whole reason this seam is raised where it is.
                runner.StoppedAt = runner.Probe.Location;
                runner.HaveStopped = true;
            };

            walker.Arrived = w => runner.Done = true;

            // See Runner.BlockedReason. Only the first, and only one that named a blocker: a jam
            // that produces four rungs is one jam, and the last rung's reason is the one recorded
            // after the ladder has shuffled the probe somewhere else.
            walker.RungFired = (w, rung, reason) =>
            {
                if (runner.BlockedReason == null
                    && reason != null
                    && reason.IndexOf("next step blocked by", StringComparison.Ordinal) >= 0)
                {
                    runner.BlockedReason = reason;
                }
            };

            walker.Follow(new NavRoute(new List<NavStep> { item.Step }, 0.0));
        }

        /// <summary>
        /// Has this probe's walk ended? Records the row and frees the probe when it has.
        ///
        /// Three endings, and the row says which: the walker raised Arrived without ever raising
        /// Failed (a pass), it raised Failed (the ladder ran out), or the deadline passed with the
        /// walker still going.
        /// </summary>
        private static bool TryComplete(Job job, Runner runner)
        {
            Item item = runner.Item;
            NavWalker walker = runner.Walker;

            bool expired = Core.TickCount - (runner.StartedTick + (long)WalkTimeout.TotalMilliseconds) >= 0;

            if (!runner.Done && !runner.Failed && !expired && walker.Active)
            {
                return false;
            }

            // HOW it ended, which is not the same question as WHY it failed. See Row.EndedBy.
            //
            // A walker that stopped without arriving and without failing has had its route dropped
            // from under it - a deleted mobile, a Stop. Not a pass: it certainly did not get there.
            string endedBy = runner.Failed
                ? "teleport"
                : (runner.Done ? null : (expired ? "timeout" : "abandoned"));

            // AND THE CAUSE IS ALWAYS THE LEDGER'S, however it ended.
            //
            // The Failed seam hands one over for a walker that climbed the whole ladder, and for
            // everything else it is computed here from exactly the same function. It has to be:
            // the ladder is five rungs twenty seconds apart, so a walk that is going to reach the
            // top rung needs a hundred seconds, and any deadline shorter than that would file
            // every hard failure under "the clock ran out" and never name one. Measured on the
            // self-test at the old forty-five second default - the water under the Trinsic pier,
            // which is goal-unstandable by construction, came back as `timeout`.
            string cause = null;

            if (endedBy != null)
            {
                if (runner.Failed && runner.FailCause != null)
                {
                    cause = runner.FailCause;
                }
                else
                {
                    bool unstandable;

                    cause = NavWalkFailures.CauseFor(
                        runner.Probe, item.Map, item.Goal, item.Range, out unstandable);
                }
            }

            var row = new NavWalkAuditRow
            {
                Kind = item.Kind,
                From = item.From,
                To = item.To,
                Destination = item.Destination,
                MapName = item.Map == null ? "" : item.Map.Name,
                Pass = endedBy == null,
                EndedBy = endedBy,
                Steps = runner.Probe.Steps,
                Tiles = item.Tiles(),
                PathTiles = item.PathTiles,
                Seconds = (Core.TickCount - runner.StartedTick) / 1000.0,
                StartX = item.Start.X,
                StartY = item.Start.Y,
                StartZ = item.Start.Z,
                GoalX = item.Goal.X,
                GoalY = item.Goal.Y,
                GoalZ = item.Goal.Z,
                StopX = runner.HaveStopped ? runner.StoppedAt.X : runner.Probe.X,
                StopY = runner.HaveStopped ? runner.StoppedAt.Y : runner.Probe.Y,
                StopZ = runner.HaveStopped ? runner.StoppedAt.Z : runner.Probe.Z,
                Range = item.Range,
                Cause = cause,
                Rungs = DescribeRungs(walker),
                RungTotal = walker.TotalRungsFired
            };

            // WHO WAS IN THE WAY, asked only of a failure and asked of the ENGINE.
            //
            // This is what keeps "bots in the way" from reading as "bad road". NextStepBlocker
            // names the occupant of the tile MovementPath's first direction points at - the tile
            // Mobile.Move hands to an occupant's OnMoveOver, and therefore the only one whose
            // occupant can refuse this step. Probes are transparent and cannot appear here.
            if (!row.Pass)
            {
                Mobile blocker = walker.NextStepBlocker(item.Step);

                if (blocker != null)
                {
                    row.OccupiedUnshovable = !NavWalkFailures.Shovable(blocker);
                    row.Occupied = String.Format(
                        "{0} ({1}) at {2},{3}",
                        blocker.Name ?? "?",
                        NavWalkFailures.Describe(blocker),
                        blocker.X,
                        blocker.Y);
                }
            }

            // A PASS CAN BE CONTESTED TOO, and without this nothing would ever say so.
            //
            // This is what keeps the highest-ratio list honest. That list is the evidence for
            // re-basing Britain, and its argument is "the authored straight line badly
            // under-represents the real walk" - which is a claim about the ROAD. A probe that took
            // two hundred steps because a Minter was standing in the bank doorway supports no such
            // claim, and on the first sweep those rows were the entire top of the list. A row
            // carrying a blocker is a row about the town being busy; one carrying none, on a graph
            // walked at mounted pace, is a row about the road.
            if (row.Pass && runner.BlockedReason != null)
            {
                row.Occupied = runner.BlockedReason;
                row.OccupiedUnshovable =
                    runner.BlockedReason.IndexOf("MAY NOT PUSH", StringComparison.Ordinal) >= 0;
            }

            job.Rows.Add(row);

            walker.Stop();
            walker.Failed = null;
            walker.Arrived = null;
            walker.RungFired = null;

            runner.Item = null;

            return true;
        }

        private static string DescribeRungs(NavWalker walker)
        {
            var parts = new List<string>();

            for (int i = (int)StuckRung.Repath; i < NavWalker.RungCount; i++)
            {
                int fired = walker.RungsFired((StuckRung)i);

                if (fired > 0)
                {
                    parts.Add(String.Format("{0} {1}", (StuckRung)i, fired));
                }
            }

            return String.Join(", ", parts.ToArray());
        }

        private static void Finish(Job job)
        {
            try
            {
                if (job.Poll != null)
                {
                    job.Poll.Stop();
                    job.Poll = null;
                }

                // The one property that must hold whatever else went wrong: nothing of this is
                // left in the world. A finally on the caller would not cover the timer path, so
                // the deletion is here and the whole method is wrapped.
                foreach (Runner runner in job.Runners)
                {
                    if (runner.Probe != null && !runner.Probe.Deleted)
                    {
                        runner.Probe.Delete();
                    }

                    if (runner.Walker != null)
                    {
                        runner.Walker.Stop();
                    }
                }

                WriteSnapshot(job, true);

                string summary = Summarise(job);

                if (job.SelfTest)
                {
                    // Loud, and its own line: the verdict is inverted for one of the two rows, so
                    // a reader skimming the summary would draw the wrong conclusion from it.
                    summary = SelfTestVerdict(job) + " " + summary;
                }

                Log.Info("Walk audit finished. {0}", summary);

                foreach (string line in Report(job))
                {
                    Log.Info(line);
                }

                if (job.Caller != null && !job.Caller.Deleted && job.Caller.NetState != null)
                {
                    job.Caller.SendMessage(0x35, "[WalkAudit] " + summary);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Walk audit could not finish cleanly.");
            }
            finally
            {
                _job = null;
            }
        }

        private static void Shuffle(List<Item> items)
        {
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = Utility.Random(i + 1);
                Item swap = items[i];
                items[i] = items[j];
                items[j] = swap;
            }
        }

        // ---- reporting ---------------------------------------------------------------------------

        /// <summary>The one-line answer, in the shape [NavAudit's summary uses.</summary>
        private static string Summarise(Job job)
        {
            int edges = 0, edgesFailed = 0, arrivals = 0, arrivalsFailed = 0, occupied = 0;

            foreach (NavWalkAuditRow row in job.Rows)
            {
                bool arrival = row.Kind == "arrival";

                if (arrival)
                {
                    arrivals++;
                }
                else
                {
                    edges++;
                }

                if (row.Pass)
                {
                    continue;
                }

                if (arrival)
                {
                    arrivalsFailed++;
                }
                else
                {
                    edgesFailed++;
                }

                if (row.Occupied != null)
                {
                    occupied++;
                }
            }

            // The worst UNCONTESTED ratio. See Report: a contested row is a fact about the town
            // being busy, and putting one in the headline points at the wrong thing.
            NavWalkAuditRow worst = null;

            foreach (NavWalkAuditRow row in job.Rows)
            {
                if (row.Pass
                    && row.Occupied == null
                    && row.RungTotal == 0
                    && (worst == null || row.Ratio > worst.Ratio))
                {
                    worst = row;
                }
            }

            return String.Format(
                "{0} edge walk(s): {1} failed. {2} arrival walk(s): {3} failed, {4} skipped "
                + "(pending-road). {5} failure(s) had somebody on the next-step tile. "
                + "Worst detour {6}. {7:F1}s over {8} probe(s)."
                + (job.Cliffs == null ? "" : " Approach tiles: " + job.Cliffs.Summary),
                edges,
                edgesFailed,
                arrivals,
                arrivalsFailed,
                job.Skipped,
                occupied,
                worst == null
                    ? "n/a"
                    : String.Format(
                        "{0:F2} ({1} tiles of road for {2}) on {3}",
                        worst.Ratio, worst.PathTiles, worst.Tiles, worst.Label),
                (Core.TickCount - job.StartedTick) / 1000.0,
                job.Runners.Count);
        }

        /// <summary>
        /// The three lists a reader needs, in the order they matter.
        ///
        /// The third is the one that is easy to leave out and is the reason a walk audit beats a
        /// geometry audit: a PASS that needed four rungs is not a clean road. It completed because
        /// the recovery ladder did its job, and it will fail the moment the ladder is busy or the
        /// tile is occupied - which is exactly the population this shard is heading towards. Those
        /// edges are invisible in a failure list and invisible in a ratio list, because a sidestep
        /// costs one step.
        /// </summary>
        private static List<string> Report(Job job)
        {
            var lines = new List<string>();

            var failures = new List<NavWalkAuditRow>();
            var passes = new List<NavWalkAuditRow>();
            var fragile = new List<NavWalkAuditRow>();

            foreach (NavWalkAuditRow row in job.Rows)
            {
                if (!row.Pass)
                {
                    failures.Add(row);
                }
                else
                {
                    passes.Add(row);

                    if (row.RungTotal > 0)
                    {
                        fragile.Add(row);
                    }
                }
            }

            // THE RATIO LIST IS SPLIT, AND THAT SPLIT IS WHAT MAKES IT EVIDENCE.
            //
            // The list argues "the authored straight line badly under-represents the real walk",
            // which is a claim about the ROAD. A probe that took two hundred steps because a
            // Minter was standing in the bank doorway supports no such claim - and on the first
            // full sweep those rows WERE the top of the list: brit-mkt-2 -> brit-mkt-3 at 82:1 on
            // a ten-tile hop, which [NavAudit's own occupancy pass had already reported as Emilio
            // standing on it. Mixed together, the loudest rows were the ones with nothing to fix.
            var clean = new List<NavWalkAuditRow>();
            var contested = new List<NavWalkAuditRow>();

            foreach (NavWalkAuditRow row in passes)
            {
                if (row.Occupied != null || row.RungTotal > 0)
                {
                    contested.Add(row);
                }
                else
                {
                    clean.Add(row);
                }
            }

            clean.Sort((a, b) => b.Ratio.CompareTo(a.Ratio));
            contested.Sort((a, b) => b.Ratio.CompareTo(a.Ratio));
            fragile.Sort((a, b) => b.RungTotal.CompareTo(a.RungTotal));

            foreach (NavWalkAuditRow row in failures)
            {
                lines.Add(String.Format(
                    "FAILED   {0} - {1} ({9}), stopped at {2},{3},{4} after {5} step(s) in {6:F1}s{7}{8}",
                    row.Label,
                    row.Cause,
                    row.StopX,
                    row.StopY,
                    row.StopZ,
                    row.Steps,
                    row.Seconds,
                    row.Occupied == null
                        ? ""
                        : String.Format(
                            "; next step blocked by {0}{1}",
                            row.Occupied,
                            row.OccupiedUnshovable ? " - WHICH A BOT MAY NOT PUSH" : ""),
                    row.Rungs.Length == 0 ? "" : "; rungs: " + row.Rungs,
                    row.EndedBy));
            }

            for (int i = 0; i < clean.Count && i < 20; i++)
            {
                NavWalkAuditRow row = clean[i];

                lines.Add(String.Format(
                    "DETOUR   {0} - the engine routes {1} tile(s) for a {2}-tile hop = {3:F2}"
                    + " (walked {4} step(s) in {5:F1}s)",
                    row.Label, row.PathTiles, row.Tiles, row.Ratio, row.Steps, row.Seconds));
            }

            for (int i = 0; i < contested.Count && i < 10; i++)
            {
                NavWalkAuditRow row = contested[i];

                lines.Add(String.Format(
                    "BUSY     {0} - {1} step(s) for {2} tile(s) = {3:F2} in {4:F1}s, but {5}",
                    row.Label,
                    row.Steps,
                    row.Tiles,
                    row.Ratio,
                    row.Seconds,
                    row.Occupied != null
                        ? "contested: " + row.Occupied
                        : "it climbed " + row.RungTotal + " rung(s): " + row.Rungs));
            }

            for (int i = 0; i < fragile.Count && i < 20; i++)
            {
                NavWalkAuditRow row = fragile[i];

                lines.Add(String.Format(
                    "FRAGILE  {0} - passed, but only after {1} rung(s): {2}",
                    row.Label, row.RungTotal, row.Rungs));
            }

            // LAST, because it is about a question none of the four lists above asked. Every row
            // in them starts on an authored waypoint; a CLIFF row is about the tiles a walker is
            // entitled to stop on instead. See NavNeighbourhood.
            foreach (string line in NavNeighbourhood.Describe(job.Cliffs, 20))
            {
                lines.Add(line);
            }

            return lines;
        }

        /// <summary>
        /// Data/Live/walk-audit.json.
        ///
        /// Written at the start with status "running" as well as at the end with "done", because a
        /// sweep takes minutes and the token cannot ack that long: the editor drops the token, gets
        /// "started", and reads the status out of this file. The header mirrors uo-offline's
        /// stuck_report.json (BotStuckTelemetry.cs:230-241) so a reader of one can read the other.
        /// </summary>
        private static void WriteSnapshot(Job job, bool done)
        {
            var builder = new StringBuilder(8192);

            int edges = 0, edgesFailed = 0, arrivals = 0, arrivalsFailed = 0;
            int occupied = 0, occupiedUnshovable = 0, fragile = 0, contested = 0;
            var causes = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (NavWalkAuditRow row in job.Rows)
            {
                if (row.Kind == "arrival")
                {
                    arrivals++;
                }
                else
                {
                    edges++;
                }

                if (row.Pass)
                {
                    if (row.RungTotal > 0)
                    {
                        fragile++;
                    }

                    // A pass whose ratio cannot be read as a fact about the road. See Report.
                    if (row.Occupied != null || row.RungTotal > 0)
                    {
                        contested++;
                    }

                    continue;
                }

                if (row.Kind == "arrival")
                {
                    arrivalsFailed++;
                }
                else
                {
                    edgesFailed++;
                }

                if (row.Occupied != null)
                {
                    occupied++;

                    if (row.OccupiedUnshovable)
                    {
                        occupiedUnshovable++;
                    }
                }

                string cause = row.Cause ?? "unknown";
                int already;

                causes[cause] = causes.TryGetValue(cause, out already) ? already + 1 : 1;
            }

            builder.Append("{\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"status\": ").Append(Json.Quote(done ? "done" : "running")).Append(",\n");
            builder.Append("  \"seconds\": ").Append(Fixed((Core.TickCount - job.StartedTick) / 1000.0)).Append(",\n");
            builder.Append("  \"probes\": ").Append(job.Runners.Count).Append(",\n");
            builder.Append("  \"selfTest\": ").Append(job.SelfTest ? "true" : "false").Append(",\n");
            builder.Append("  \"total\": ").Append(job.Total).Append(",\n");
            builder.Append("  \"walked\": ").Append(job.Rows.Count).Append(",\n");
            builder.Append("  \"skipped\": ").Append(job.Skipped).Append(",\n");
            builder.Append("  \"edges\": {\"walked\": ").Append(edges)
                .Append(", \"failed\": ").Append(edgesFailed).Append("},\n");
            builder.Append("  \"arrivals\": {\"walked\": ").Append(arrivals)
                .Append(", \"failed\": ").Append(arrivalsFailed).Append("},\n");
            builder.Append("  \"occupied\": ").Append(occupied).Append(",\n");
            builder.Append("  \"occupiedUnshovable\": ").Append(occupiedUnshovable).Append(",\n");
            builder.Append("  \"fragile\": ").Append(fragile).Append(",\n");
            builder.Append("  \"contested\": ").Append(contested).Append(",\n");

            builder.Append("  \"causes\": {");

            bool first = true;

            foreach (KeyValuePair<string, int> pair in causes)
            {
                if (!first)
                {
                    builder.Append(", ");
                }

                builder.Append(Json.Quote(pair.Key)).Append(": ").Append(pair.Value);
                first = false;
            }

            builder.Append("},\n");

            // The approach-tile scan, in the same shape nav-audit.json publishes it, so a reader
            // parses one format. `cliffsChecked` distinguishes "none" from "not run" - a self-test
            // never runs it, and the editor's Problems panel must not read a missing array as a
            // clean bill of health.
            builder.Append("  \"cliffsChecked\": ")
                .Append(job.Cliffs != null ? "true" : "false").Append(",\n");
            builder.Append("  \"cliffSeconds\": ")
                .Append(Fixed(job.Cliffs == null ? 0.0 : job.Cliffs.Seconds)).Append(",\n");
            builder.Append("  ");

            NavNeighbourhood.AppendJson(builder, job.Cliffs);

            builder.Append(",\n");

            builder.Append("  \"rows\": [\n");

            for (int i = 0; i < job.Rows.Count; i++)
            {
                NavWalkAuditRow row = job.Rows[i];

                builder.Append("    {\"kind\":").Append(Json.Quote(row.Kind));
                builder.Append(",\"from\":").Append(Json.Quote(row.From));

                if (row.To != null)
                {
                    builder.Append(",\"to\":").Append(Json.Quote(row.To));
                }

                if (row.Destination != null)
                {
                    builder.Append(",\"destination\":").Append(Json.Quote(row.Destination));
                }

                builder.Append(",\"map\":").Append(Json.Quote(row.MapName));
                builder.Append(",\"pass\":").Append(row.Pass ? "true" : "false");
                builder.Append(",\"steps\":").Append(row.Steps);
                builder.Append(",\"tiles\":").Append(row.Tiles);
                builder.Append(",\"pathTiles\":").Append(row.PathTiles);
                builder.Append(",\"ratio\":").Append(Fixed(row.Ratio));
                builder.Append(",\"stepRatio\":").Append(Fixed(row.StepRatio));
                builder.Append(",\"seconds\":").Append(Fixed(row.Seconds));
                builder.Append(",\"startX\":").Append(row.StartX);
                builder.Append(",\"startY\":").Append(row.StartY);
                builder.Append(",\"startZ\":").Append(row.StartZ);
                builder.Append(",\"x\":").Append(row.GoalX);
                builder.Append(",\"y\":").Append(row.GoalY);
                builder.Append(",\"z\":").Append(row.GoalZ);
                builder.Append(",\"stopX\":").Append(row.StopX);
                builder.Append(",\"stopY\":").Append(row.StopY);
                builder.Append(",\"stopZ\":").Append(row.StopZ);
                builder.Append(",\"range\":").Append(row.Range);
                builder.Append(",\"rungTotal\":").Append(row.RungTotal);
                builder.Append(",\"rungs\":").Append(Json.Quote(row.Rungs ?? ""));

                if (row.Cause != null)
                {
                    builder.Append(",\"cause\":").Append(Json.Quote(row.Cause));
                }

                if (row.EndedBy != null)
                {
                    builder.Append(",\"endedBy\":").Append(Json.Quote(row.EndedBy));
                }

                if (row.Occupied != null)
                {
                    builder.Append(",\"occupied\":").Append(Json.Quote(row.Occupied));
                    builder.Append(",\"occupiedUnshovable\":")
                        .Append(row.OccupiedUnshovable ? "true" : "false");
                }

                builder.Append("}");

                if (i < job.Rows.Count - 1)
                {
                    builder.Append(",");
                }

                builder.Append("\n");
            }

            builder.Append("  ]\n}\n");

            string error;

            if (!AtomicFile.Write(SnapshotPath, builder.ToString(), out error))
            {
                Log.Error("Could not write {0}: {1}", SnapshotPath, error);
            }
        }

        /// <summary>Invariant culture, so a comma decimal separator can never produce broken JSON.</summary>
        private static string Fixed(double value)
        {
            return value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>The summary line and the report, for the token path, without a Mobile.</summary>
        public static bool TryDescribeLast(out string summary, out IList<string> report)
        {
            Job job = _job;

            if (job == null)
            {
                summary = "no walk audit is running";
                report = new List<string>();
                return false;
            }

            summary = Summarise(job);
            report = Report(job);
            return true;
        }
    }
}
