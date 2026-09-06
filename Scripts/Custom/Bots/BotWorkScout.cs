// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotWorkScout.cs — authoring tool: find work sites and the roads to them, from
// inside the engine.
//
// WHY THIS EXISTS, and it is worth being blunt about it.
//
// The first cut of session 7e's navigation data was derived offline, by reading
// the client's own map1LegacyMUL.uop, statics1.mul and tiledata.mul in a script.
// It looked right. Every arrival point sat within two tiles of what the script
// believed was a mineable land tile, and every corridor hop was walkable
// according to a breadth-first search over the land and item Impassable flags.
//
// It was wrong. [NavAudit came back with fourteen blocked edges and reported the
// resolved Z of the corridor as 30, 50, -15 and 11 where the script had read a
// flat -5 for all of them, and BotWorkSites excluded both mine faces because the
// engine could not find a harvestable tile at any arrival point. The UOP chunk
// table is hash-keyed and the script's ordering assumption did not survive
// contact with it - close enough to look plausible on a handful of spot checks,
// far enough out to be useless.
//
// So the data comes from the engine now. Everything below asks exactly the
// questions the runtime asks, with the runtime's own methods:
//
//   harvestable?   HarvestDefinition.Validate, over map.Tiles, through
//                  BotHarvest.FindTarget's own scan - the same call the bot
//                  makes when it swings.
//   standable?     map.CanFit, at NavWalker.ResolveZ - the same Z the walker
//                  aims a hop at.
//   walkable hop?  MovementPath both ways, which is verbatim what
//                  NavAudit.CanWalk does. If the scout emits it, the audit
//                  passes it, because it is the same test.
//
// It writes Data/Live/work-scout.json for a human to merge into
// Data/Custom/navigation.json. It deliberately does NOT edit navigation.json:
// that file is authored, carries comments and ordering somebody chose, and a
// generator that rewrites it would quietly become the author.
//
// Run it with Custom.BotWorkScoutOnStart=True, or [BotWorkScout in-game.
// ServUO's console cannot invoke staff commands, which is why the flag exists -
// the same reason Custom.NavAuditOnStart does.

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

        /// <summary>The authored hop cap. A sampled leg never exceeds it.</summary>
        private static int HopCap
        {
            get { return NavigationSystem.HopMaxTiles; }
        }

        /// <summary>How far around a seed to sweep for harvestable tiles.</summary>
        private const int SeedRadius = 30;

        /// <summary>Keep arrival points this far apart, so four bots are not on one tile.</summary>
        private const int ArrivalSpacing = 5;

        private const int ArrivalsPerSite = 4;

        /// <summary>Tiles the corridor search may wander from the straight line before giving up.</summary>
        private const int CorridorMargin = 90;

        public static void Initialize()
        {
            CommandSystem.Register("BotWorkScout", AccessLevel.Administrator, OnCommand);

            if (Config.Get("Custom.BotWorkScoutOnStart", false))
            {
                EventSink.ServerStarted += () => Run(null);
            }
        }

        [Usage("BotWorkScout")]
        [Description("Sweeps for mine and lumber sites near Britain and the roads to them, and writes Data/Live/work-scout.json.")]
        private static void OnCommand(CommandEventArgs e)
        {
            Run(e.Mobile);
        }

        private sealed class Seed
        {
            public string Id;
            public string Name;
            public string Type;
            public string Tag;
            public Point3D At;
            public BotClass Worker;
            public string Prefix;
        }

        // The seeds are approximate centres, not authored answers: the scout sweeps SeedRadius
        // around each and reports what is actually there. They came from the offline pass, which
        // was reliable about WHERE the rock and the trees are (a 700x800 sweep found the same two
        // ranges the engine does) and unreliable about the exact tiles and their heights.
        private static readonly Seed[] Seeds =
        {
            new Seed
            {
                Id = "brit-mine-north", Name = "Britain West Mine, north face",
                Type = "mine", Tag = "mine-north",
                At = new Point3D(1281, 1594, 0), Worker = BotClass.Miner, Prefix = "brit-minenorth",
            },
            new Seed
            {
                Id = "brit-mine-south", Name = "Britain West Mine, south face",
                Type = "mine", Tag = "mine-south",
                At = new Point3D(1281, 1655, 0), Worker = BotClass.Miner, Prefix = "brit-minesouth",
            },
            new Seed
            {
                Id = "brit-lumber-nw", Name = "The North-west Wood",
                Type = "lumber", Tag = "lumber-nw",
                At = new Point3D(1319, 1542, 0), Worker = BotClass.Lumberjack, Prefix = "brit-woodpath",
            },
        };

        public static void Run(Mobile from)
        {
            Map map = Map.Trammel;
            NavWaypoint start = Nav.Waypoint("brit-gate-w");

            if (start == null)
            {
                Emit(from, "No 'brit-gate-w' waypoint; nothing to scout from.", true);
                return;
            }

            var report = new List<string>();
            var json = new StringBuilder(4096);

            json.Append("{\n");
            json.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            json.Append("  \"sites\": [\n");

            for (int i = 0; i < Seeds.Length; i++)
            {
                ScoutSite(map, start, Seeds[i], report, json);

                if (i < Seeds.Length - 1)
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

            Emit(from, "[BotWorkScout] wrote " + SnapshotPath, false);

            foreach (string line in report)
            {
                Emit(from, "  " + line, false);
            }
        }

        private static void ScoutSite(Map map, NavWaypoint start, Seed seed, List<string> report, StringBuilder json)
        {
            HarvestDefinition definition = BotHarvest.DefinitionFor(seed.Worker);

            List<Point3D> harvestable = Harvestable(map, seed.At, definition);

            if (harvestable.Count == 0)
            {
                report.Add(String.Format("{0}: NOTHING HARVESTABLE within {1} tiles of {2}",
                    seed.Id, SeedRadius, seed.At));

                json.Append("    {\"id\":").Append(Json.Quote(seed.Id)).Append(",\"harvestable\":0}");

                return;
            }

            List<Point3D> arrivals = Arrivals(map, harvestable, definition, start.Location);

            if (arrivals.Count == 0)
            {
                report.Add(String.Format("{0}: {1} harvestable tile(s) but nowhere to stand",
                    seed.Id, harvestable.Count));

                json.Append("    {\"id\":").Append(Json.Quote(seed.Id))
                    .Append(",\"harvestable\":").Append(harvestable.Count).Append(",\"arrivals\":0}");

                return;
            }

            // Route to the arrival point nearest the town, then keep the rest as arrivals.
            string attach;
            List<Point3D> legs =
                Corridor(map, Surface(map, start.Location), arrivals[0], report, seed.Id, out attach);

            // The approach is the site end of the corridor, which is where a bot plugs back into
            // the graph - so it is what the face has to stay in reach of.
            Point3D approach = legs != null && legs.Count > 0 ? legs[0] : arrivals[0];

            Rectangle2D face = Face(arrivals, harvestable, approach);

            report.Add(String.Format(
                "{0}: {1} harvestable, {2} arrival(s), corridor {3} waypoint(s) attaching at '{4}', face {5}x{6} at {7},{8}",
                seed.Id, harvestable.Count, arrivals.Count,
                legs == null ? 0 : legs.Count,
                attach == null ? "NOTHING" : attach,
                face.Width, face.Height, face.X, face.Y));

            WriteSite(json, seed, arrivals, legs, face, harvestable.Count, attach);
        }

        /// <summary>Every tile near the seed that the harvest definition would accept.</summary>
        private static List<Point3D> Harvestable(Map map, Point3D at, HarvestDefinition definition)
        {
            var found = new List<Point3D>();

            if (definition == null)
            {
                return found;
            }

            for (int x = at.X - SeedRadius; x <= at.X + SeedRadius; x++)
            {
                for (int y = at.Y - SeedRadius; y <= at.Y + SeedRadius; y++)
                {
                    StaticTile[] tiles = map.Tiles.GetStaticTiles(x, y, false);
                    bool hit = false;

                    for (int i = 0; i < tiles.Length && !hit; i++)
                    {
                        if (definition.Validate((tiles[i].ID & 0x3FFF) | 0x4000))
                        {
                            found.Add(new Point3D(x, y, tiles[i].Z));
                            hit = true;
                        }
                    }

                    if (hit)
                    {
                        continue;
                    }

                    LandTile land = map.Tiles.GetLandTile(x, y);

                    if (definition.Validate(land.ID))
                    {
                        found.Add(new Point3D(x, y, land.Z));
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Standable tiles within harvest range of something harvestable, spread out and sorted
        /// so the one nearest town comes first — that is the one the corridor aims at.
        /// </summary>
        private static List<Point3D> Arrivals(
            Map map, List<Point3D> harvestable, HarvestDefinition definition, Point3D town)
        {
            var seen = new HashSet<int>();
            var candidates = new List<Point3D>();
            int range = definition.MaxRange;

            foreach (Point3D tile in harvestable)
            {
                for (int dx = -range; dx <= range; dx++)
                {
                    for (int dy = -range; dy <= range; dy++)
                    {
                        int x = tile.X + dx;
                        int y = tile.Y + dy;
                        int key = (x << 16) | (y & 0xFFFF);

                        if (!seen.Add(key))
                        {
                            continue;
                        }

                        int z = NavWalker.ResolveZ(map, new Point3D(x, y, tile.Z));

                        // The same fit the walker needs to stand there: a 16-high humanoid, no
                        // mobile check (nothing is standing in the wilderness at boot).
                        if (!map.CanFit(x, y, z, 16, false, false, true))
                        {
                            continue;
                        }

                        candidates.Add(new Point3D(x, y, z));
                    }
                }
            }

            candidates.Sort((a, b) => NavGraph.Chebyshev(a, town).CompareTo(NavGraph.Chebyshev(b, town)));

            var picked = new List<Point3D>();

            foreach (Point3D candidate in candidates)
            {
                bool clear = true;

                foreach (Point3D held in picked)
                {
                    if (NavGraph.Chebyshev(candidate, held) < ArrivalSpacing)
                    {
                        clear = false;
                        break;
                    }
                }

                if (!clear)
                {
                    continue;
                }

                picked.Add(candidate);

                if (picked.Count == ArrivalsPerSite)
                {
                    break;
                }
            }

            return picked;
        }

        /// <summary>
        /// A chain of hops from the site back toward town, every one of which MovementPath can
        /// walk both ways — which is exactly NavAudit's test, so anything emitted here passes it.
        ///
        /// SAMPLED FROM THE SITE INWARD, and stopping the moment it can reach the graph that
        /// already exists. Sampling the other way round is the obvious thing to do and it is
        /// wrong: it re-walks Britain from the gate and lays a parallel set of waypoints down
        /// streets that already have them. Growing inward instead means the corridor is only ever
        /// the part that is genuinely new, and it attaches to whichever town waypoint is really
        /// closest to the site rather than to whichever one somebody chose as a starting point.
        ///
        /// The search underneath is a breadth-first flood over tiles the engine says a mobile can
        /// stand on, bounded to a corridor around the straight line so it cannot wander off across
        /// the facet, and refusing any step that climbs more than the engine's own step height.
        /// </summary>
        private static List<Point3D> Corridor(
            Map map, Point3D from, Point3D to, List<string> report, string siteId, out string attach)
        {
            attach = null;

            List<Point3D> path = Flood(map, from, to);

            if (path == null)
            {
                report.Add(siteId + ": NO WALKABLE CORRIDOR from the town");
                return null;
            }

            path.Reverse();

            // The site end is itself a waypoint. Without it the nearest waypoint to a bot
            // working the face is the first hop UP the corridor, and a face deeper than the hop
            // cap then contains tiles from which Nav.TryRouteFrom cannot route at all - the bot
            // finishes its shift, asks for a way home every tick, and is told there is no
            // waypoint within twelve tiles. That is exactly how the first work probe failed.
            var legs = new List<Point3D> { path[0] };
            Point3D anchor = path[0];
            int i = 0;

            while (i < path.Count - 1)
            {
                // Can this leg reach the existing graph? If so the corridor is finished, and
                // everything between here and town already exists.
                NavWaypoint join = NearestReachable(map, anchor);

                if (join != null)
                {
                    attach = join.Id;
                    break;
                }

                int best = -1;

                for (int j = Math.Min(i + HopCap, path.Count - 1); j > i; j--)
                {
                    if (NavGraph.Chebyshev(anchor, path[j]) > HopCap)
                    {
                        continue;
                    }

                    if (!CanWalk(map, anchor, path[j]))
                    {
                        continue;
                    }

                    best = j;
                    break;
                }

                if (best < 0)
                {
                    report.Add(String.Format(
                        "{0}: corridor stalled at {1} - no reachable hop within {2} tiles",
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

            if (attach == null)
            {
                report.Add(siteId + ": corridor never reached the existing graph");
            }

            return legs;
        }

        /// <summary>
        /// An already-authored waypoint this point can hop to, or null.
        ///
        /// Nearest first, so a corridor attaches to the closest piece of road rather than to
        /// whichever one the store happens to list first.
        /// </summary>
        private static NavWaypoint NearestReachable(Map map, Point3D from)
        {
            var candidates = new List<NavWaypoint>();

            foreach (NavWaypoint waypoint in NavigationSystem.Store.Waypoints)
            {
                if (waypoint.Map != map)
                {
                    continue;
                }

                if (NavGraph.Chebyshev(waypoint.Location, from) <= HopCap)
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

        private static List<Point3D> Flood(Map map, Point3D from, Point3D to)
        {
            var queue = new Queue<Point3D>();
            var came = new Dictionary<int, Point3D>();
            var seen = new HashSet<int>();

            int Key(Point3D p)
            {
                return (p.X << 16) | (p.Y & 0xFFFF);
            }

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

                        int key = (x << 16) | (y & 0xFFFF);

                        if (!seen.Add(key))
                        {
                            continue;
                        }

                        int z = NavWalker.ResolveZ(map, new Point3D(x, y, current.Z));

                        if (!map.CanFit(x, y, z, 16, false, false, true))
                        {
                            continue;
                        }

                        // Refuse a step the engine itself would refuse: a climb of more than the
                        // step height is a cliff, and a flood that ignores it produces a corridor
                        // that reads walkable tile by tile and is not.
                        if (Math.Abs(z - current.Z) > 11)
                        {
                            continue;
                        }

                        var next = new Point3D(x, y, z);

                        came[key] = current;

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

        /// <summary>
        /// The zone rectangle: the arrivals plus everything harvestable beside them, CLAMPED to
        /// what the approach waypoint can route from.
        ///
        /// The clamp is the load-bearing half. A bot works by shuffling around inside this
        /// rectangle, and Nav.TryRouteFrom refuses to route from anywhere with no waypoint within
        /// NavigationSystem.HopMaxTiles (Nav.cs:213). So a face that extends further than the hop
        /// cap from its approach contains tiles a bot can walk to, mine happily, and then never
        /// leave - it finishes its shift and asks for a route home every tick, for ever.
        ///
        /// Losing a few tiles of rock at the edge is the right trade: they are unreachable by
        /// definition, and a smaller face that a bot can always walk out of beats a larger one
        /// with a trap around the rim.
        /// </summary>
        private static Rectangle2D Face(List<Point3D> arrivals, List<Point3D> harvestable, Point3D approach)
        {
            int minX = Int32.MaxValue, maxX = Int32.MinValue;
            int minY = Int32.MaxValue, maxY = Int32.MinValue;

            foreach (Point3D point in arrivals)
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }

            foreach (Point3D point in harvestable)
            {
                bool near = false;

                foreach (Point3D arrival in arrivals)
                {
                    if (NavGraph.Chebyshev(point, arrival) <= 4)
                    {
                        near = true;
                        break;
                    }
                }

                if (!near)
                {
                    continue;
                }

                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }

            // A little slack so a bot shuffling along the face is not immediately outside it.
            minX -= 2;
            minY -= 2;
            maxX += 2;
            maxY += 2;

            // ...and then the clamp. One tile inside the cap, so a bot standing on the boundary
            // is still comfortably in range rather than exactly at it.
            int reach = HopCap - 1;

            minX = Math.Max(minX, approach.X - reach);
            maxX = Math.Min(maxX, approach.X + reach);
            minY = Math.Max(minY, approach.Y - reach);
            maxY = Math.Min(maxY, approach.Y + reach);

            return new Rectangle2D(minX, minY, (maxX - minX) + 1, (maxY - minY) + 1);
        }

        private static void WriteSite(
            StringBuilder json, Seed seed, List<Point3D> arrivals, List<Point3D> legs,
            Rectangle2D face, int harvestable, string attach)
        {
            json.Append("    {\"id\":").Append(Json.Quote(seed.Id));
            json.Append(",\"name\":").Append(Json.Quote(seed.Name));
            json.Append(",\"type\":").Append(Json.Quote(seed.Type));
            json.Append(",\"tag\":").Append(Json.Quote(seed.Tag));
            json.Append(",\"prefix\":").Append(Json.Quote(seed.Prefix));
            json.Append(",\"harvestable\":").Append(harvestable);
            json.Append(",\"attach\":").Append(attach == null ? "null" : Json.Quote(attach));
            json.Append(",\"zone\":{\"x\":").Append(face.X).Append(",\"y\":").Append(face.Y);
            json.Append(",\"width\":").Append(face.Width).Append(",\"height\":").Append(face.Height).Append("}");

            json.Append(",\"arrivals\":[");

            for (int i = 0; i < arrivals.Count; i++)
            {
                if (i > 0)
                {
                    json.Append(",");
                }

                json.Append("{\"x\":").Append(arrivals[i].X)
                    .Append(",\"y\":").Append(arrivals[i].Y)
                    .Append(",\"z\":").Append(arrivals[i].Z).Append("}");
            }

            json.Append("],\"waypoints\":[");

            if (legs != null)
            {
                for (int i = 0; i < legs.Count; i++)
                {
                    if (i > 0)
                    {
                        json.Append(",");
                    }

                    json.Append("{\"x\":").Append(legs[i].X)
                        .Append(",\"y\":").Append(legs[i].Y)
                        .Append(",\"z\":").Append(legs[i].Z).Append("}");
                }
            }

            json.Append("]}");
        }

        private static bool CanWalk(Map map, Point3D start, Point3D goal)
        {
            if (NavGraph.Chebyshev(start, goal) <= 1)
            {
                // MovementPath returns no path for an adjacent goal (MovementPath.cs:34), so this
                // would be a guaranteed false negative. NavAudit skips the same case.
                return true;
            }

            return new MovementPath(start, goal, map).Success
                && new MovementPath(goal, start, map).Success;
        }

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
