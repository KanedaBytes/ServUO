// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotWorkScout.cs — authoring tool: build the road to a hand-picked work site.
//
// TWO GENERATIONS OF THIS FILE HAVE BEEN WRONG, in instructive ways.
//
// The first derived everything offline from the client's MUL and UOP files. It
// read a flat z -5 along a corridor the engine resolves at 30, 50 and -15, and
// [NavAudit rejected fourteen of its edges.
//
// The second asked the engine, which fixed the geometry and hid a worse fault:
// it also let the engine CHOOSE the sites, by sweeping for anything
// HarvestDefinition.Validate accepted. That is a far weaker test than it sounds.
// Stock ServUO's m_MountainAndCaveTiles contains land ids - 236-247 among them -
// that this client's tiledata names 'forest', so the sweep found scattered
// transition tiles in the fields west of Castle Britannia and called them a
// mine. Every arrival passed Validate; every arrival had one to four harvestable
// tiles in reach; the corridor crossed the castle moat; and the work probe
// passed anyway, because the miner was hauling the ore EquipmentTable spawns it
// with. Real rock is land 556-559, named 'rock', in cells that saturate at
// 100/100.
//
// So this generation chooses nothing. THE SITES AND THEIR ARRIVAL POINTS ARE
// HAND-PICKED AND VERIFIED IN-GAME, listed below as coordinates. The tool's job
// is now only the part a human should not do by hand: find a walkable road from
// each site back to the existing graph, and prove every hop with the same
// MovementPath test [NavAudit uses.
//
// Use [BotSiteAudit and [BotSitePick to choose a site; use this to connect it.

using System;
using System.Collections.Generic;
using System.Text;

using Server.Commands;
using Server.Engines.Harvest;

namespace Server.Custom
{
    public static class BotWorkScout
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        public const string SnapshotPath = "Data/Live/work-scout.json";

        private static int HopCap
        {
            get { return NavigationSystem.HopMaxTiles; }
        }

        /// <summary>Tiles the corridor flood may wander from the straight line before giving up.</summary>
        private const int CorridorMargin = 260;

        public static void Initialize()
        {
            CommandSystem.Register("BotWorkScout", AccessLevel.Administrator, OnCommand);

            if (Config.Get("Custom.BotWorkScoutOnStart", false))
            {
                EventSink.ServerStarted += () => Run(null);
            }
        }

        [Usage("BotWorkScout")]
        [Description("Builds and verifies the road from each authored work site back to the town graph; writes Data/Live/work-scout.json.")]
        private static void OnCommand(CommandEventArgs e)
        {
            Run(e.Mobile);
        }

        private sealed class Site
        {
            public string Id;
            public string Name;
            public string Type;
            public string Tag;
            public string Prefix;
            public BotClass Worker;
            public Point3D[] Arrivals;
        }

        // HAND-PICKED, verified with [BotSitePick against the shard's own TileMatrix, and confirmed
        // in-game. The reach figure beside each is how many harvestable tiles sit inside the 5x5
        // box BotHarvest.FindTarget sweeps from that tile - the number the previous generation of
        // this file never looked at, and the one Nav.Data now guards.
        private static readonly Site[] Sites =
        {
            new Site
            {
                Id = "brit-mine-north", Name = "The Northern Outcrop",
                Type = "mine", Tag = "mine-north", Prefix = "brit-minenorth",
                Worker = BotClass.Miner,
                Arrivals = new[]
                {
                    new Point3D(1451, 1517, 43),  // reach 14
                    new Point3D(1452, 1529, 35),  // reach 12
                    new Point3D(1448, 1522, 45),  // reach 12
                    new Point3D(1450, 1512, 40),  // reach 12
                    new Point3D(1447, 1527, 32),  // reach 11
                },
            },
            new Site
            {
                Id = "brit-mine-west", Name = "The West Cliff",
                Type = "mine", Tag = "mine-west", Prefix = "brit-minewest",
                Worker = BotClass.Miner,
                Arrivals = new[]
                {
                    new Point3D(1192, 1750, 2),   // reach 15
                    new Point3D(1196, 1756, 4),   // reach 13
                    new Point3D(1197, 1763, 2),   // reach 13
                    new Point3D(1197, 1774, 2),   // reach 13
                },
            },
            new Site
            {
                Id = "brit-lumber-south", Name = "The Southern Wood",
                Type = "lumber", Tag = "lumber-south", Prefix = "brit-woodpath",
                Worker = BotClass.Lumberjack,
                Arrivals = new[]
                {
                    new Point3D(1422, 1835, 0),   // reach 4
                    new Point3D(1414, 1844, 0),   // reach 4
                    new Point3D(1406, 1831, 0),   // reach 4
                    new Point3D(1402, 1852, 0),   // reach 4
                },
            },
        };

        public static void Run(Mobile from)
        {
            Map map = Map.Trammel;

            var report = new List<string>();
            var json = new StringBuilder(8192);

            json.Append("{\n");
            json.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            json.Append("  \"sites\": [\n");

            for (int i = 0; i < Sites.Length; i++)
            {
                ScoutSite(map, Sites[i], report, json);

                if (i < Sites.Length - 1)
                {
                    json.Append(",\n");
                }
            }

            json.Append("\n  ]\n}\n");

            string error;

            if (!AtomicFile.Write(SnapshotPath, json.ToString(), out error))
            {
                Log.Error("Could not write {0}: {1}", SnapshotPath, error);
            }

            Emit(from, "[BotWorkScout] wrote " + SnapshotPath);

            foreach (string line in report)
            {
                Emit(from, "  " + line);
            }
        }

        private static void ScoutSite(Map map, Site site, List<string> report, StringBuilder json)
        {
            HarvestDefinition definition = BotHarvest.DefinitionFor(site.Worker);

            // 1. The road in, from an arrival back to whatever already exists.
            //
            // Every arrival is tried, not just the first: a face can have one corner walled off by
            // a cliff or a river while the rest of it is perfectly reachable, and giving up on the
            // site because arrival[0] happened to be that corner would be throwing away the site
            // for the sake of the order the list is written in.
            string attach = null;
            List<Point3D> legs = null;

            foreach (Point3D entry in site.Arrivals)
            {
                legs = Corridor(map, Surface(map, entry), report, site.Id, out attach);

                if (legs != null && attach != null)
                {
                    break;
                }
            }

            if (legs == null || attach == null)
            {
                report.Add(site.Id + ": NO CORRIDOR - not written");
                json.Append("    {\"id\":").Append(Json.Quote(site.Id)).Append(",\"corridor\":false}");
                return;
            }

            // 2. A waypoint at every arrival that is not already in reach of one.
            //
            // Nav.TryRouteFrom refuses to route from anywhere with no waypoint inside the hop cap,
            // and a bot works by shuffling around the whole face - so every arrival needs one in
            // reach or the bot that wanders to it can never leave. A face wider than the hop cap
            // therefore needs several, which is why this is a loop and not a single approach point.
            var anchors = new List<Point3D>(legs);

            if (anchors.Count == 0)
            {
                anchors.Add(Surface(map, site.Arrivals[0]));
            }

            var kept = new List<Point3D>();
            var faceLegs = new List<Point3D>();

            foreach (Point3D arrival in site.Arrivals)
            {
                Point3D at = Surface(map, arrival);

                if (Nearest(anchors, at) <= HopCap - 1)
                {
                    kept.Add(at);
                    continue;
                }

                Point3D host = Closest(anchors, at);

                if (NavGraph.Chebyshev(host, at) > HopCap || !CanWalk(map, host, at))
                {
                    report.Add(String.Format(
                        "{0}: arrival {1} DROPPED - no walkable hop from the face chain", site.Id, at));
                    continue;
                }

                anchors.Add(at);
                faceLegs.Add(at);
                kept.Add(at);
            }

            if (kept.Count == 0)
            {
                report.Add(site.Id + ": every arrival unreachable - not written");
                json.Append("    {\"id\":").Append(Json.Quote(site.Id)).Append(",\"corridor\":false}");
                return;
            }

            Rectangle2D face = Face(kept, anchors, definition);

            int reach = 0;

            foreach (Point3D arrival in kept)
            {
                reach = Math.Max(reach, BotWorkSites.ReachFrom(map, arrival, definition));
            }

            report.Add(String.Format(
                "{0}: corridor {1} + {2} face waypoint(s) attaching at '{3}', {4}/{5} arrival(s) kept, "
                + "best reach {6}, face {7}x{8} at {9},{10}",
                site.Id, legs.Count, faceLegs.Count, attach, kept.Count, site.Arrivals.Length,
                reach, face.Width, face.Height, face.X, face.Y));

            WriteSite(json, site, kept, legs, faceLegs, face, attach, reach);
        }

        private static int Nearest(List<Point3D> points, Point3D to)
        {
            int best = Int32.MaxValue;

            foreach (Point3D p in points)
            {
                best = Math.Min(best, NavGraph.Chebyshev(p, to));
            }

            return best;
        }

        private static Point3D Closest(List<Point3D> points, Point3D to)
        {
            Point3D best = points[0];
            int bestDistance = Int32.MaxValue;

            foreach (Point3D p in points)
            {
                int d = NavGraph.Chebyshev(p, to);

                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = p;
                }
            }

            return best;
        }

        /// <summary>
        /// Hops from the site back toward town, every one walkable both ways, stopping the moment
        /// the existing graph is in reach.
        ///
        /// Sampled from the SITE INWARD on purpose: the other way round re-walks Britain and lays
        /// a second set of waypoints down streets that already have them.
        /// </summary>
        private static List<Point3D> Corridor(
            Map map, Point3D from, List<string> report, string siteId, out string attach)
        {
            attach = null;

            NavWaypoint seed = NearestReachable(map, from);

            if (seed != null)
            {
                // Already touching the graph: the site needs no corridor at all.
                attach = seed.Id;
                return new List<Point3D>();
            }

            // Aim at SEVERAL candidate waypoints and keep the shortest road.
            //
            // Aiming only at the Chebyshev-nearest is the obvious thing and it is wrong: the
            // closest waypoint as the crow flies can be behind a mountain or a town wall, and the
            // flood then swings hundreds of tiles around the obstacle to reach that ONE tile when
            // a slightly-further waypoint was directly approachable. That is how the wood a
            // hundred tiles south of Britain first came back with a thirty-six hop corridor
            // entering from the north.
            List<NavWaypoint> targets = NearestSeveral(map, from, 8);

            if (targets.Count == 0)
            {
                report.Add(siteId + ": no waypoints on this facet at all");
                return null;
            }

            List<Point3D> path = null;
            string via = null;

            foreach (NavWaypoint candidate in targets)
            {
                List<Point3D> attempt = Flood(map, from, Surface(map, candidate.Location));

                if (attempt != null && (path == null || attempt.Count < path.Count))
                {
                    path = attempt;
                    via = candidate.Id;
                }
            }

            if (path == null)
            {
                report.Add(siteId + ": NO WALKABLE ROUTE toward any of the "
                    + targets.Count + " nearest waypoints");
                return null;
            }

            Log.Debug("{0}: shortest road is {1} tiles, aiming at '{2}'", siteId, path.Count, via);

            var legs = new List<Point3D>();
            Point3D anchor = path[0];
            int i = 0;

            while (i < path.Count - 1)
            {
                NavWaypoint join = NearestReachable(map, anchor);

                if (join != null)
                {
                    attach = join.Id;
                    break;
                }

                int best = -1;

                for (int j = Math.Min(i + HopCap, path.Count - 1); j > i; j--)
                {
                    if (NavGraph.Chebyshev(anchor, path[j]) > HopCap || !CanWalk(map, anchor, path[j]))
                    {
                        continue;
                    }

                    best = j;
                    break;
                }

                if (best < 0)
                {
                    report.Add(String.Format(
                        "{0}: corridor stalled at {1} - no reachable hop within {2}",
                        siteId, anchor, HopCap));

                    return legs;
                }

                anchor = path[best];
                legs.Add(anchor);
                i = best;
            }

            if (attach == null)
            {
                NavWaypoint join = NearestReachable(map, anchor);
                attach = join == null ? null : join.Id;
            }

            return legs;
        }

        private static NavWaypoint NearestReachable(Map map, Point3D from)
        {
            var candidates = new List<NavWaypoint>();

            foreach (NavWaypoint waypoint in NavigationSystem.Store.Waypoints)
            {
                if (waypoint.Map == map && NavGraph.Chebyshev(waypoint.Location, from) <= HopCap)
                {
                    candidates.Add(waypoint);
                }
            }

            candidates.Sort((a, b) =>
                NavGraph.Chebyshev(a.Location, from).CompareTo(NavGraph.Chebyshev(b.Location, from)));

            foreach (NavWaypoint waypoint in candidates)
            {
                if (CanWalk(map, from, Surface(map, waypoint.Location)))
                {
                    return waypoint;
                }
            }

            return null;
        }

        private static List<NavWaypoint> NearestSeveral(Map map, Point3D from, int count)
        {
            var all = new List<NavWaypoint>();

            foreach (NavWaypoint waypoint in NavigationSystem.Store.Waypoints)
            {
                if (waypoint.Map == map)
                {
                    all.Add(waypoint);
                }
            }

            all.Sort((a, b) =>
                NavGraph.Chebyshev(a.Location, from).CompareTo(NavGraph.Chebyshev(b.Location, from)));

            if (all.Count > count)
            {
                all.RemoveRange(count, all.Count - count);
            }

            return all;
        }

        private static List<Point3D> Flood(Map map, Point3D from, Point3D to)
        {
            var queue = new Queue<Point3D>();
            var came = new Dictionary<int, Point3D>();
            var seen = new HashSet<int>();

            queue.Enqueue(from);
            seen.Add(Key(from));

            int minX = Math.Min(from.X, to.X) - CorridorMargin;
            int maxX = Math.Max(from.X, to.X) + CorridorMargin;
            int minY = Math.Min(from.Y, to.Y) - CorridorMargin;
            int maxY = Math.Max(from.Y, to.Y) + CorridorMargin;

            Point3D hit = Point3D.Zero;
            bool found = false;

            while (queue.Count > 0 && !found)
            {
                Point3D current = queue.Dequeue();

                for (int dx = -1; dx <= 1 && !found; dx++)
                {
                    for (int dy = -1; dy <= 1 && !found; dy++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        int x = current.X + dx;
                        int y = current.Y + dy;

                        if (x < minX || x > maxX || y < minY || y > maxY)
                        {
                            continue;
                        }

                        if (!seen.Add((x << 16) | (y & 0xFFFF)))
                        {
                            continue;
                        }

                        int z = NavWalker.ResolveZ(map, new Point3D(x, y, current.Z));

                        if (!map.CanFit(x, y, z, 16, false, false, true))
                        {
                            continue;
                        }

                        // A climb steeper than the engine's own step height is a cliff. Refusing it
                        // here is what makes the tool find a switchback up a real slope rather than
                        // drawing a straight line through one.
                        if (Math.Abs(z - current.Z) > 11)
                        {
                            continue;
                        }

                        var next = new Point3D(x, y, z);

                        came[(x << 16) | (y & 0xFFFF)] = current;

                        if (x == to.X && y == to.Y)
                        {
                            hit = next;
                            found = true;
                            break;
                        }

                        queue.Enqueue(next);
                    }
                }
            }

            if (!found)
            {
                return null;
            }

            var path = new List<Point3D>();
            Point3D step = hit;

            while (step != from)
            {
                path.Add(step);

                Point3D previous;

                if (!came.TryGetValue(Key(step), out previous))
                {
                    break;
                }

                step = previous;
            }

            path.Add(from);
            path.Reverse();

            return path;
        }

        private static int Key(Point3D p)
        {
            return (p.X << 16) | (p.Y & 0xFFFF);
        }

        /// <summary>
        /// The workable rectangle: the ground beside the arrivals, clamped to what the site's own
        /// waypoints can route from.
        ///
        /// The clamp is load-bearing. A bot works by shuffling inside this rectangle, and a tile
        /// further than the hop cap from every waypoint is one it can walk to, mine happily, and
        /// then never leave.
        /// </summary>
        private static Rectangle2D Face(
            List<Point3D> arrivals, List<Point3D> anchors, HarvestDefinition definition)
        {
            int minX = Int32.MaxValue, maxX = Int32.MinValue;
            int minY = Int32.MaxValue, maxY = Int32.MinValue;
            int range = (definition == null ? 2 : definition.MaxRange) + 2;

            foreach (Point3D arrival in arrivals)
            {
                for (int dx = -range; dx <= range; dx++)
                {
                    for (int dy = -range; dy <= range; dy++)
                    {
                        int x = arrival.X + dx;
                        int y = arrival.Y + dy;

                        if (Nearest(anchors, new Point3D(x, y, 0)) > HopCap - 1)
                        {
                            continue;
                        }

                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }

            return new Rectangle2D(minX, minY, (maxX - minX) + 1, (maxY - minY) + 1);
        }

        private static void WriteSite(
            StringBuilder json, Site site, List<Point3D> arrivals, List<Point3D> legs,
            List<Point3D> faceLegs, Rectangle2D face, string attach, int reach)
        {
            json.Append("    {\"id\":").Append(Json.Quote(site.Id));
            json.Append(",\"name\":").Append(Json.Quote(site.Name));
            json.Append(",\"type\":").Append(Json.Quote(site.Type));
            json.Append(",\"tag\":").Append(Json.Quote(site.Tag));
            json.Append(",\"prefix\":").Append(Json.Quote(site.Prefix));
            json.Append(",\"attach\":").Append(Json.Quote(attach));
            json.Append(",\"reach\":").Append(reach);
            json.Append(",\"zone\":{\"x\":").Append(face.X).Append(",\"y\":").Append(face.Y);
            json.Append(",\"width\":").Append(face.Width).Append(",\"height\":").Append(face.Height).Append("}");

            Append(json, ",\"arrivals\":", arrivals);
            Append(json, ",\"corridor\":", legs);
            Append(json, ",\"face\":", faceLegs);

            json.Append("}");
        }

        private static void Append(StringBuilder json, string label, List<Point3D> points)
        {
            json.Append(label).Append("[");

            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0)
                {
                    json.Append(",");
                }

                json.Append("{\"x\":").Append(points[i].X)
                    .Append(",\"y\":").Append(points[i].Y)
                    .Append(",\"z\":").Append(points[i].Z).Append("}");
            }

            json.Append("]");
        }

        private static bool CanWalk(Map map, Point3D start, Point3D goal)
        {
            if (NavGraph.Chebyshev(start, goal) <= 1)
            {
                return true;
            }

            return new MovementPath(start, goal, map).Success
                && new MovementPath(goal, start, map).Success;
        }

        private static Point3D Surface(Map map, Point3D point)
        {
            return new Point3D(point.X, point.Y, NavWalker.ResolveZ(map, point));
        }

        private static void Emit(Mobile from, string line)
        {
            Log.Info(line);

            if (from != null)
            {
                from.SendMessage(0x40, line);
            }
        }
    }
}
