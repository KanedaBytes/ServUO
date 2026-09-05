using System;
using System.Collections.Generic;

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
    /// </summary>
    public static class NavAudit
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        public static void Run(Mobile from)
        {
            var report = new List<string>();
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
                    report.Add(String.Format(
                        "FAR      '{0}' -> '{1}' is {2} tiles (cap {3})", a.Id, b.Id, distance, cap));
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
                    report.Add(String.Format(
                        "BLOCKED  '{0}' {1}{2} -> '{3}' {4}{5}",
                        a.Id,
                        start,
                        start.Z != a.Z ? String.Format(" (authored z {0})", a.Z) : "",
                        b.Id,
                        goal,
                        goal.Z != b.Z ? String.Format(" (authored z {0})", b.Z) : ""));
                }
            }

            string summary = String.Format(
                "[NavAudit] {0} walk edge(s) checked: {1} blocked, {2} over cap, {3} adjacent (skipped).",
                checkedEdges,
                blocked,
                far,
                adjacent);

            Emit(from, summary, blocked > 0 || far > 0);

            foreach (string line in report)
            {
                Emit(from, "  " + line, true);
            }

            if (blocked > 0)
            {
                Emit(
                    from,
                    "  Note: a waypoint at a closed door is a false positive. Verify before editing.",
                    true);
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
