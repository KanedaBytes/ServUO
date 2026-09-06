using System;
using System.Collections.Generic;
using System.Text;

namespace Server.Custom
{
    /// <summary>
    /// Walkability audit: pathfinds every walk edge with the real engine against real map data
    /// and reports the ones that cannot be walked.
    ///
    /// This exists because the alternative is discovering bad coordinates one stuck NPC at a
    /// time. uo-offline-server put it best in their own audit command: every multi-session
    /// navigation bug they had was silently bad DATA, and all of it was mechanically detectable.
    ///
    /// Not a health check: pathing several hundred edges is far too costly to run on every
    /// [CoreSmoke. It is a command, plus Custom.NavAuditOnStart for a headless run - ServUO's
    /// console cannot invoke staff commands, so that flag is the only way to see this over SSH.
    ///
    /// CAVEAT, carried over from their experience: a waypoint at a closed door is a false
    /// positive. Read the report before believing it.
    ///
    /// CAVEAT, the other way, and the dangerous one because it is silent: **a clean audit does
    /// not mean a walker can walk it.** This probes with a Point3D; NavWalker probes with the
    /// mobile itself, and the engine treats the two differently in ways that go in BOTH
    /// directions:
    ///
    ///   - MOBILES. `Movement.cs:411` sets `checkMobs` only when the probe is an uncontrolled
    ///     BaseCreature, so a Point3D walks through anything standing in the way and a walker
    ///     does not. Every tile on the route except the goal is blocked by whoever is on it.
    ///     Verified: `brit-prov-1` (1469,1668) has a stock vendor parked on it permanently, and
    ///     the audit reports the edge clean.
    ///   - DOORS. Conversely `FastAStarAlgorithm.cs:92` only ignores doors for a BaseCreature,
    ///     so the audit is STRICTER here - which is the closed-door false positive above.
    ///   - START TILE. The audit paths waypoint-tile to waypoint-tile. A walker starts the next
    ///     hop from wherever it stopped, which is anywhere within `ArrivalRangeFor` (2 tiles by
    ///     default) of the waypoint - a 5x5 box the audit never validated.
    ///
    /// So the audit checks the GEOMETRY of an edge, once, on the canonical tiles. It cannot
    /// check occupancy, and it does not check the approaches. Treat a pass as "the road exists",
    /// not "everyone will get through".
    /// </summary>
    /// <summary>
    /// One thing the audit found, structured rather than as prose.
    ///
    /// The editor draws these as a layer over the edges, which a formatted line cannot be turned
    /// into without parsing it back - and a format parsed by the same codebase that wrote it is a
    /// format defined twice.
    /// </summary>
    public sealed class NavAuditProblem
    {
        public NavAuditProblem(string from, string to, string map, bool blocked, int distance)
        {
            From = from;
            To = to;
            Map = map;
            Blocked = blocked;
            Distance = distance;
        }

        public string From { get; private set; }

        public string To { get; private set; }

        public string Map { get; private set; }

        /// <summary>True when the engine could not path it; false when it is merely over the cap.</summary>
        public bool Blocked { get; private set; }

        public int Distance { get; private set; }
    }

    public static class NavAudit
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>Where the structured findings go, for the editor to draw as a layer.</summary>
        public const string SnapshotPath = "Data/Live/nav-audit.json";

        /// <summary>
        /// Runs the audit and reports it to whoever asked.
        ///
        /// A thin wrapper over TryRun, which is where the work is. Split so the editor bridge can
        /// have the same report the command prints rather than a second, subtly different one -
        /// and so the blocked pairs can be written somewhere a map layer can read them.
        /// </summary>
        public static void Run(Mobile from)
        {
            string summary;
            IList<string> report;
            IList<NavAuditProblem> problems;

            TryRun(out summary, out report, out problems);

            Emit(from, summary, problems.Count > 0);

            foreach (string line in report)
            {
                Emit(from, "  " + line, true);
            }

            foreach (NavAuditProblem problem in problems)
            {
                if (problem.Blocked)
                {
                    Emit(
                        from,
                        "  Note: a waypoint at a closed door is a false positive. Verify before editing.",
                        true);

                    break;
                }
            }
        }

        /// <summary>
        /// The audit itself, callable without a Mobile.
        ///
        /// `problems` is the same findings as `report`, structured. Prose cannot be turned into a
        /// map layer, and parsing it back would mean inventing the format twice.
        /// </summary>
        public static bool TryRun(
            out string summary, out IList<string> report, out IList<NavAuditProblem> problems)
        {
            var lines = new List<string>();
            var found = new List<NavAuditProblem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int checkedEdges = 0;
            int blocked = 0;
            int far = 0;
            int adjacent = 0;

            int cap = NavigationSystem.HopMaxTiles;

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

                // Edges are symmetrised in the graph, so audit each unordered pair once.
                string key = String.CompareOrdinal(a.Id, b.Id) < 0
                    ? a.Id + ">" + b.Id
                    : b.Id + ">" + a.Id;

                if (!seen.Add(key))
                {
                    continue;
                }

                checkedEdges++;

                int distance = NavGraph.Chebyshev(a.Location, b.Location);

                if (distance > cap)
                {
                    far++;
                    lines.Add(String.Format(
                        "FAR      '{0}' -> '{1}' is {2} tiles (cap {3})", a.Id, b.Id, distance, cap));
                    found.Add(new NavAuditProblem(a.Id, b.Id, a.Map.Name, false, distance));
                    continue;
                }

                if (distance <= 1)
                {
                    // MovementPath returns no path at all for an adjacent goal
                    // (MovementPath.cs:34), so this would be a guaranteed false positive.
                    adjacent++;
                    continue;
                }

                Point3D start = Surface(a.Map, a.Location);
                Point3D goal = Surface(a.Map, b.Location);

                if (!CanWalk(a.Map, start, goal))
                {
                    blocked++;
                    found.Add(new NavAuditProblem(a.Id, b.Id, a.Map.Name, true, distance));
                    lines.Add(String.Format(
                        "BLOCKED  '{0}' {1}{2} -> '{3}' {4}{5}",
                        a.Id,
                        start,
                        start.Z != a.Z ? String.Format(" (authored z {0})", a.Z) : "",
                        b.Id,
                        goal,
                        goal.Z != b.Z ? String.Format(" (authored z {0})", b.Z) : ""));
                }
            }

            summary = String.Format(
                "[NavAudit] {0} walk edge(s) checked: {1} blocked, {2} over cap, {3} adjacent (skipped).",
                checkedEdges,
                blocked,
                far,
                adjacent);

            report = lines;
            problems = found;

            return blocked == 0;
        }

        /// <summary>
        /// Writes the findings where a map layer can read them.
        ///
        /// The ack caps its arrays at forty and carries prose; this carries every finding as a
        /// record. A layer needs the edge, not a sentence about the edge.
        /// </summary>
        public static void WriteSnapshot(IList<NavAuditProblem> problems)
        {
            var builder = new StringBuilder(512);

            builder.Append("{\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"problems\": [\n");

            for (int i = 0; i < problems.Count; i++)
            {
                NavAuditProblem problem = problems[i];

                builder.Append("    {\"from\":").Append(Json.Quote(problem.From));
                builder.Append(",\"to\":").Append(Json.Quote(problem.To));
                builder.Append(",\"map\":").Append(Json.Quote(problem.Map));
                builder.Append(",\"blocked\":").Append(problem.Blocked ? "true" : "false");
                builder.Append(",\"distance\":").Append(problem.Distance);
                builder.Append("}");

                if (i < problems.Count - 1)
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

        /// <summary>
        /// Both directions, because a one-way blockage (a ledge you can drop off but not climb)
        /// is exactly the kind of thing that strands a walker halfway through a patrol.
        /// </summary>
        private static bool CanWalk(Map map, Point3D start, Point3D goal)
        {
            return new MovementPath(start, goal, map).Success
                && new MovementPath(goal, start, map).Success;
        }

        /// <summary>
        /// Resolved exactly as NavWalker resolves a hop goal - they must agree, or the audit
        /// would pass a hop the walker then aims at the wrong floor.
        /// </summary>
        private static Point3D Surface(Map map, Point3D point)
        {
            return new Point3D(point.X, point.Y, NavWalker.ResolveZ(map, point));
        }

        private static void Emit(Mobile from, string line, bool problem)
        {
            if (problem)
            {
                Log.Warn(line);
            }
            else
            {
                Log.Info(line);
            }

            if (from != null)
            {
                from.SendMessage(problem ? 0x25 : 0x40, line);
            }
        }
    }
}
