using System;
using System.Collections.Generic;
using System.Text;

namespace Server.Custom
{
    /// <summary>What kind of finding this is. Only Blocked means the edge cannot be walked.</summary>
    public enum NavAuditKind
    {
        /// <summary>The engine could not path it at all. The edge is wrong.</summary>
        Blocked = 0,

        /// <summary>Longer than the hop cap. The edge is walkable but the walker cannot plan it.</summary>
        Far = 1,

        /// <summary>
        /// The geometry is fine and something is standing on it.
        ///
        /// A WARNING, never a block. The obstruction is a fact about right now, not about the
        /// data - a vendor wanders off, a crowd disperses - so failing the audit on it would make
        /// a clean run depend on the weather. But a waypoint with a shopkeeper permanently parked
        /// on it is worth knowing about, because every walker will meet it and the geometry pass
        /// is blind to it.
        /// </summary>
        Occupied = 2,
    }

    /// <summary>
    /// One thing the audit found, structured rather than as prose.
    ///
    /// The editor draws these as a layer over the edges, which a formatted line cannot be turned
    /// into without parsing it back - and a format parsed by the same codebase that wrote it is a
    /// format defined twice.
    /// </summary>
    public sealed class NavAuditProblem
    {
        public NavAuditProblem(string from, string to, string map, NavAuditKind kind, int distance)
            : this(from, to, map, kind, distance, null)
        {
        }

        public NavAuditProblem(
            string from, string to, string map, NavAuditKind kind, int distance, string detail)
        {
            From = from;
            To = to;
            Map = map;
            Kind = kind;
            Distance = distance;
            Detail = detail;
        }

        public string From { get; private set; }

        public string To { get; private set; }

        public string Map { get; private set; }

        public NavAuditKind Kind { get; private set; }

        /// <summary>
        /// True only for a genuinely unwalkable edge.
        ///
        /// Kept as its own property because the editor draws a red edge from it, and an
        /// occupancy warning must not turn an edge red - the data is fine.
        /// </summary>
        public bool Blocked
        {
            get { return Kind == NavAuditKind.Blocked; }
        }

        public int Distance { get; private set; }

        /// <summary>What is standing there, for an Occupied finding. Null otherwise.</summary>
        public string Detail { get; private set; }
    }

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
            int occupied = 0;

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
                    found.Add(new NavAuditProblem(a.Id, b.Id, a.Map.Name, NavAuditKind.Far, distance));
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
                    found.Add(new NavAuditProblem(a.Id, b.Id, a.Map.Name, NavAuditKind.Blocked, distance));
                    lines.Add(String.Format(
                        "BLOCKED  '{0}' {1}{2} -> '{3}' {4}{5}",
                        a.Id,
                        start,
                        start.Z != a.Z ? String.Format(" (authored z {0})", a.Z) : "",
                        b.Id,
                        goal,
                        goal.Z != b.Z ? String.Format(" (authored z {0})", b.Z) : ""));

                    continue;
                }

                // Second pass: the geometry is walkable, so ask the other question the geometry
                // pass cannot - is anything standing on it? See the class comment.
                string standing = Occupancy(a.Map, start, goal);

                if (standing != null)
                {
                    occupied++;
                    found.Add(new NavAuditProblem(
                        a.Id, b.Id, a.Map.Name, NavAuditKind.Occupied, distance, standing));
                    lines.Add(String.Format(
                        "OCCUPIED '{0}' -> '{1}': geometry is fine, {2}",
                        a.Id,
                        b.Id,
                        standing));
                }
            }

            summary = String.Format(
                "[NavAudit] {0} walk edge(s) checked: {1} blocked, {2} over cap, {3} occupied (warning only), "
                + "{4} adjacent (skipped).",
                checkedEdges,
                blocked,
                far,
                occupied,
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
                builder.Append(",\"kind\":").Append(Json.Quote(problem.Kind.ToString().ToLowerInvariant()));
                builder.Append(",\"distance\":").Append(problem.Distance);

                if (problem.Detail != null)
                {
                    builder.Append(",\"detail\":").Append(Json.Quote(problem.Detail));
                }

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
        /// <summary>
        /// Walk the path the engine just found and report the first thing standing on it, or null.
        ///
        /// This is the audit's answer to its own blind spot. The geometry pass probes with a
        /// Point3D, which sets `checkMobs` false (Movement.cs:411) and walks through anybody; a
        /// real walker is an uncontrolled BaseCreature and is stopped by them. Rather than spawn a
        /// probe creature to find that out - which would place a mobile in the world for the sake
        /// of a report - this re-walks the returned path and looks at who is on it.
        ///
        /// Deliberately matched to the engine's own rule so it does not invent obstructions:
        ///
        ///   - CanMoveOver (Movement.cs:361) lets a walker pass a dead mobile, a dead bonded pet,
        ///     and a hidden staff member. So do we.
        ///   - The engine exempts the GOAL tile (the `xForward != m_Goal.X` clause), because
        ///     something standing on the destination should not make it unreachable. So do we.
        ///   - The Z window is +/-15, as in Check.
        ///
        /// The first obstruction is enough: the report is there to send a person to look, not to
        /// enumerate a crowd.
        /// </summary>
        private static string Occupancy(Map map, Point3D start, Point3D goal)
        {
            var path = new MovementPath(start, goal, map);

            if (!path.Success)
            {
                return null;
            }

            int x = start.X;
            int y = start.Y;

            Direction[] steps = path.Directions;

            for (int i = 0; i < steps.Length; i++)
            {
                Offset(steps[i], ref x, ref y);

                // The goal tile is exempt, exactly as the engine exempts it.
                if (x == goal.X && y == goal.Y)
                {
                    continue;
                }

                int z = NavWalker.ResolveZ(map, new Point3D(x, y, goal.Z));

                IPooledEnumerable<Mobile> mobiles = map.GetMobilesInRange(new Point3D(x, y, z), 0);

                try
                {
                    foreach (Mobile mob in mobiles)
                    {
                        if (mob == null || mob.Deleted || mob.X != x || mob.Y != y)
                        {
                            continue;
                        }

                        if (!mob.Alive || mob.IsDeadBondedPet || (mob.Hidden && mob.IsStaff()))
                        {
                            continue;
                        }

                        if (mob.Z + 15 <= z || z + 15 <= mob.Z)
                        {
                            continue;
                        }

                        return String.Format(
                            "{0} ({1}) is standing at ({2}, {3})",
                            String.IsNullOrEmpty(mob.Name) ? "something" : mob.Name,
                            mob.GetType().Name,
                            x,
                            y);
                    }
                }
                finally
                {
                    mobiles.Free();
                }
            }

            return null;
        }

        /// <summary>
        /// One tile in a direction. Local rather than borrowed from MovementImpl, which exposes
        /// this only on the implementation instance.
        /// </summary>
        private static void Offset(Direction d, ref int x, ref int y)
        {
            switch (d & Direction.Mask)
            {
                case Direction.North: y--; break;
                case Direction.Right: x++; y--; break;
                case Direction.East: x++; break;
                case Direction.Down: x++; y++; break;
                case Direction.South: y++; break;
                case Direction.Left: x--; y++; break;
                case Direction.West: x--; break;
                case Direction.Up: x--; y--; break;
            }
        }

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
