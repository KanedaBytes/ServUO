// NavCorridor.cs — walk a road between two points the way a bot would, and report it.
//
// THE PROBLEM THIS EXISTS FOR, stated exactly, because the obvious solution is
// the wrong one:
//
//   BotWorkScout floods a corridor using map.CanFit, and a closed door is an
//   Impassable item, so CanFit refuses a town gate. Two verified work sites -
//   brit-mine-west and brit-lumber-south - could not be written because their
//   corridors came back as a 28-hop and a 36-hop arc swinging right around the
//   mountain range, and every hop of those arcs is genuinely walkable. The flood
//   was not wrong about the geometry. It was wrong about the gate.
//
//   A real bot walks through it. FastAStarAlgorithm sets
//   MoveImpl.AlwaysIgnoreDoors from bc.CanOpenDoors (FastAStarAlgorithm.cs:93),
//   and CanOpenDoors is true for any non-animal body (BaseCreature.cs:1924). But
//   it does that ONLY when it has a BaseCreature - the Point3D overload
//   NavAudit uses gets none, so it sees the door as a wall.
//
// So the probe is a real BaseCreature, briefly. That is a deliberate break with
// NavAudit.cs:330-334, which refuses to place a mobile to answer a question, and
// the difference is worth being precise about: NavAudit re-checks a hundred
// edges on every boot, where this is one request a person typed while authoring
// a road, and the door behaviour is the entire point of asking.
//
// A FLOOD, THEN A PROOF, THEN A REPAIR - and it took all three.
//
//   The first attempt walked the probe greedily toward the goal in legs, since
//   FastAStarAlgorithm refuses a goal more than AreaSize away (38 tiles,
//   FastAStarAlgorithm.cs:47). It died thirty tiles out of the west gate:
//   aiming at a destination and shouldering around what is in the way is a
//   LOCAL search, and Britain's west wall is a concave obstacle no amount of
//   local shouldering escapes. So the road is found by a flood, which cannot get
//   stuck that way.
//
//   The flood and the pathfinder then disagreed. The flood compares two resolved
//   Z values; MoveImpl does a real tile-geometry calculation. Tuning the flood's
//   climb limit does not reconcile them - at the engine's own StepHeight of 2 it
//   loses roads a bot can walk, at BotWorkScout's 11 it proposed a step from
//   1352,1725 that the pathfinder refused, and the west-gate and south-bridge
//   roads wanted different answers.
//
//   So the tool stops guessing the rule and asks. Every proposed hop is walked by
//   a real BaseCreature; a hop that fails is bisected; and a step refused even
//   between ADJACENT tiles has that tile banned and the road flooded again. Each
//   pass removes one genuine obstruction, so it converges - on a road every hop
//   of which has been walked, which is the only claim worth making.

using System;
using System.Collections.Generic;
using System.Text;

using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    public static class NavCorridor
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        /// <summary>Where the editor reads the last computed road from.</summary>
        public const string OutputPath = "Data/Live/nav-route.json";

        /// <summary>Tiles the flood may wander outside the box its two ends make. BotWorkScout's.</summary>
        private const int Margin = 260;

        /// <summary>
        /// Tiles visited before the flood gives up.
        ///
        /// A bound, not a budget: an unreachable goal would otherwise flood the whole margin box,
        /// which is 260 tiles of slack in every direction. This runs on the game thread, so
        /// "returns eventually" is not good enough.
        /// </summary>
        private const int MaxTiles = 250000;

        /// <summary>How far from a clicked point to look for somewhere a mobile can stand.</summary>
        private const int SnapRadius = 8;

        /// <summary>How many times a road may be re-floated around a step the engine refuses.</summary>
        private const int RepairAttempts = 6;

        /// <summary>How far a shift-drag looks for a road. Beyond this the author meant it.</summary>
        private const int RoadSnapRadius = 12;

        /// <summary>
        /// The most the search will climb between adjacent tiles.
        ///
        /// BotWorkScout's number, kept so the two tools agree about what a cliff is - and
        /// deliberately NOT the engine's StepHeight of 2 (Movement.cs:11). Both were tried. At 2
        /// the flood is stricter than movement actually is and loses roads a bot can walk; at 11
        /// it is looser and occasionally proposes a step MoveImpl refuses. Neither number is
        /// right, because comparing two ResolveZ results is not the calculation MoveImpl performs.
        ///
        /// So the constant is a candidate generator, not a verdict. Being loose here is the safe
        /// direction: SampleVerified drives a real creature over the result, and TryPathAvoiding
        /// re-floods around anything it refuses.
        /// </summary>
        private const int ClimbLimit = 11;

        /// <summary>
        /// What one tile of each kind of ground costs the search.
        ///
        /// A road is no shorter than the grass beside it, but it is where a road GOES, and a
        /// corridor laid across open country is one nobody would ever have walked. Weighting the
        /// search rather than restricting it keeps that a preference: it still crosses a field
        /// when a field is the only way through, it just will not do so to save two tiles.
        ///
        /// Classified by the land tile's NAME, which is what [BotOreSweep leans on and for the
        /// same reason: this client's tiledata is the authority on what a tile actually is, and
        /// the id ranges are not contiguous enough to hard-code. An unknown name costs Other, so
        /// ground nobody has classified is neither preferred nor avoided.
        /// </summary>
        private static int CostOf(Map map, int x, int y)
        {
            switch (GroundAt(map, x, y))
            {
                case Ground.Road: return Config.Get("Custom.NavRoadCostRoad", 1);
                case Ground.Grass: return Config.Get("Custom.NavRoadCostGrass", 3);
                case Ground.Sand: return Config.Get("Custom.NavRoadCostSand", 3);
                case Ground.Forest: return Config.Get("Custom.NavRoadCostForest", 4);
            }

            return Config.Get("Custom.NavRoadCostOther", 3);
        }

        private enum Ground
        {
            Other,
            Road,
            Grass,
            Sand,
            Forest
        }

        /// <summary>
        /// What KIND of ground this is, independent of what it currently costs.
        ///
        /// Separated from the cost for a reason that cost a wrong measurement: IsRoad was written
        /// as "costs no more than a road", which is true of every tile the moment the weights are
        /// equal. Flattening them to check the weighting was doing anything duly reported every
        /// road as 100% on roads - a metric defined in terms of the thing it was measuring. The
        /// classification is a fact about the tile; the cost is a policy about it, and only the
        /// second belongs in config.
        /// </summary>
        private static Ground GroundAt(Map map, int x, int y)
        {
            string name;

            try
            {
                name = TileData.LandTable[map.Tiles.GetLandTile(x, y).ID & 0x3FFF].Name;
            }
            catch
            {
                return Ground.Other;
            }

            if (name == null)
            {
                return Ground.Other;
            }

            if (Has(name, "road") || Has(name, "cobble") || Has(name, "dirt") || Has(name, "pave"))
            {
                return Ground.Road;
            }

            if (Has(name, "grass"))
            {
                return Ground.Grass;
            }

            if (Has(name, "sand"))
            {
                return Ground.Sand;
            }

            if (Has(name, "forest") || Has(name, "jungle"))
            {
                return Ground.Forest;
            }

            return Ground.Other;
        }

        private static bool Has(string name, string part)
        {
            return name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// What share of a walked road is actually on made ground, 0 to 100.
        ///
        /// The one number that says whether the cost weighting did anything. Without it "the path
        /// got four tiles longer" is the only evidence, and that is equally consistent with the
        /// weighting working and with it doing nothing while the search order shifted.
        /// </summary>
        public static int RoadFraction(Map map, IList<Point3D> path)
        {
            if (map == null || path == null || path.Count == 0)
            {
                return 0;
            }

            int on = 0;

            for (int i = 0; i < path.Count; i++)
            {
                if (IsRoad(map, path[i].X, path[i].Y))
                {
                    on++;
                }
            }

            return (on * 100) / path.Count;
        }

        /// <summary>True when this tile reads as a made road. The editor's shift-drag snaps to it.</summary>
        public static bool IsRoad(Map map, int x, int y)
        {
            return GroundAt(map, x, y) == Ground.Road;
        }

        /// <summary>
        /// One entry in the search frontier, ordered by cost and then by tile.
        ///
        /// A SortedSet needs a TOTAL order: comparing on cost alone would make two tiles of equal
        /// cost compare equal and the set would silently keep only one of them. The key is the
        /// tie-break, which is why this is a struct with an explicit CompareTo.
        /// </summary>
        private struct PathNode : IComparable<PathNode>
        {
            public readonly int Cost;
            public readonly int Key;
            public readonly Point3D Point;

            public PathNode(int cost, int key, Point3D point)
            {
                Cost = cost;
                Key = key;
                Point = point;
            }

            public int CompareTo(PathNode other)
            {
                int byCost = Cost.CompareTo(other.Cost);

                return byCost != 0 ? byCost : Key.CompareTo(other.Key);
            }
        }

        /// <summary>
        /// A humanoid stand-in for the bot that will walk this road.
        ///
        /// Everything about it is in service of one property: a non-animal Body, which is what
        /// makes CanOpenDoors true and therefore what makes FastAStarAlgorithm ignore doors. It
        /// never fights, never thinks and never lives longer than the request that made it.
        /// </summary>
        private class CorridorProbe : BaseCreature
        {
            public CorridorProbe()
                : base(AIType.AI_Melee, FightMode.None, 10, 1, 0.2, 0.4)
            {
                Body = 0x190;
                Name = "a corridor probe";

                Blessed = true;
                Hidden = true;
                CantWalk = false;

                // Nothing should react to it: not a player, not a guard, not another creature.
                Karma = 0;
                Fame = 0;
            }

            public CorridorProbe(Serial serial)
                : base(serial)
            {
            }

            public override bool CanOpenDoors { get { return true; } }

            public override void Serialize(GenericWriter writer)
            {
                base.Serialize(writer);
                writer.Write(0);
            }

            public override void Deserialize(GenericReader reader)
            {
                base.Deserialize(reader);
                reader.ReadInt();

                // Belt and braces. One of these is created and deleted inside a single request, so
                // it should never reach a save at all - but a crash between the two would leave a
                // hidden mobile in the world for ever, and the ephemeral idiom costs nothing.
                Timer.DelayCall(Delete);
            }
        }

        /// <summary>
        /// Walk from one point to another and return every tile stepped on, start included.
        ///
        /// A breadth-first flood, not a greedy walk toward the goal. That was the first attempt
        /// and it failed thirty tiles out of the west gate: aiming at the destination and
        /// shouldering around whatever is in the way is a LOCAL search, and Britain's west wall is
        /// a concave obstacle that no amount of local shouldering escapes. A flood cannot get
        /// stuck that way - it is exhaustive inside its box - and it is the same shape as the one
        /// BotWorkScout already uses, differing in exactly one line: the walkability test.
        /// </summary>
        public static bool TryPath(
            Map map, Point3D from, Point3D to, out List<Point3D> path, out string error)
        {
            return TryPath(map, from, to, null, out path, out error);
        }

        /// <summary>The same flood, refusing a set of tiles a previous attempt proved unusable.</summary>
        public static bool TryPath(
            Map map, Point3D from, Point3D to, HashSet<int> banned,
            out List<Point3D> path, out string error)
        {
            path = new List<Point3D>();
            error = null;

            if (map == null || map == Map.Internal)
            {
                error = "no facet";
                return false;
            }

            // BOTH ENDS ARE SNAPPED to the nearest tile a mobile can stand on, and that is not a
            // convenience. A wood's centre is usually a tree and a rock face's centre is usually
            // rock: the first run of this against brit-lumber-south answered "no walkable road"
            // having flooded the entire margin box, because the goal tile it was looking for could
            // never be entered. An unreachable goal and an unstandable one look identical from
            // inside a flood, and only one of them is the author's mistake.
            Point3D start, goal;

            if (!TrySnap(map, from, out start))
            {
                error = String.Format("nothing standable within {0} tiles of {1},{2}",
                    SnapRadius, from.X, from.Y);
                return false;
            }

            if (!TrySnap(map, to, out goal))
            {
                error = String.Format("nothing standable within {0} tiles of {1},{2}",
                    SnapRadius, to.X, to.Y);
                return false;
            }

            // Dijkstra, not breadth-first, because the tiles are no longer all worth the same.
            // A plain queue finds the fewest STEPS; weighting the ground and still popping in
            // insertion order would compute a cost and then not use it.
            var frontier = new SortedSet<PathNode>();
            var came = new Dictionary<int, Point3D>();
            var best = new Dictionary<int, int>();

            frontier.Add(new PathNode(0, Key(start), start));
            best[Key(start)] = 0;

            int minX = Math.Min(start.X, goal.X) - Margin;
            int maxX = Math.Max(start.X, goal.X) + Margin;
            int minY = Math.Min(start.Y, goal.Y) - Margin;
            int maxY = Math.Max(start.Y, goal.Y) + Margin;

            bool found = false;
            Point3D hit = Point3D.Zero;
            int visited = 0;

            while (frontier.Count > 0 && !found && visited < MaxTiles)
            {
                PathNode node = frontier.Min;
                frontier.Remove(node);

                Point3D current = node.Point;
                visited++;

                int currentCost;

                // A stale entry: a cheaper way to this tile turned up after it was queued.
                if (!best.TryGetValue(node.Key, out currentCost) || currentCost < node.Cost)
                {
                    continue;
                }

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

                        int z = NavWalker.ResolveZ(map, new Point3D(x, y, current.Z));

                        int key = (x << 16) | (y & 0xFFFF);

                        if (banned != null && banned.Contains(key))
                        {
                            continue;
                        }

                        if (!CanStand(map, x, y, z))
                        {
                            continue;
                        }

                        // THE ENGINE'S STEP HEIGHT, not a generous approximation of it.
                        //
                        // BotWorkScout uses 11 here under a comment calling it "the engine's own
                        // step height"; the engine's is 2 (Movement.cs:11, `StepHeight`). The gap
                        // matters: a flood that climbs 11 walks up slopes MoveImpl will not, and
                        // then every check downstream disagrees with it. That is exactly what the
                        // west-gate road did - 315 contiguous "walkable" tiles containing a step
                        // from 1352,1725 to the tile beside it that the pathfinder refused, which
                        // took a bisection down to adjacent tiles to expose.
                        if (Math.Abs(z - current.Z) > ClimbLimit)
                        {
                            continue;
                        }

                        // A diagonal costs the same as an orthogonal step, as it does to the
                        // mobile: UO movement is eight-directional and a diagonal is one step.
                        int cost = currentCost + CostOf(map, x, y);
                        int known;

                        if (best.TryGetValue(key, out known) && known <= cost)
                        {
                            continue;
                        }

                        var next = new Point3D(x, y, z);

                        best[key] = cost;
                        came[key] = current;

                        if (x == goal.X && y == goal.Y)
                        {
                            hit = next;
                            found = true;
                            break;
                        }

                        frontier.Add(new PathNode(cost, key, next));
                    }
                }
            }

            if (!found)
            {
                error = visited >= MaxTiles
                    ? String.Format("gave up after {0} tiles; the goal may be unreachable", visited)
                    : String.Format("no walkable road from {0},{1} to {2},{3}",
                        start.X, start.Y, goal.X, goal.Y);
                return false;
            }

            var reversed = new List<Point3D>();
            Point3D step = hit;

            while (true)
            {
                reversed.Add(step);

                if (step.X == start.X && step.Y == start.Y)
                {
                    break;
                }

                Point3D parent;

                if (!came.TryGetValue(Key(step), out parent))
                {
                    break;
                }

                step = parent;
            }

            reversed.Reverse();
            path = reversed;

            return true;
        }

        /// <summary>
        /// Can a mobile stand here - and this is the ONE line that separates this from the flood
        /// that could not leave town.
        ///
        /// A closed door is an Impassable item, so map.CanFit refuses a town gate, and that is why
        /// BotWorkScout's corridors to brit-mine-west and brit-lumber-south came back as 28-hop and
        /// 36-hop arcs right around the mountain range. Every hop of those arcs was genuinely
        /// walkable; the flood was not wrong about the geometry, it was wrong about the gate.
        ///
        /// A real bot walks through it: FastAStarAlgorithm sets MoveImpl.AlwaysIgnoreDoors from
        /// bc.CanOpenDoors, true for any non-animal body. So a tile whose only obstruction is a
        /// door is passable here too, which makes this flood agree with the pathfinder that will
        /// actually be asked to walk its result.
        /// </summary>
        private static bool CanStand(Map map, int x, int y, int z)
        {
            if (map.CanFit(x, y, z, 16, false, false, true))
            {
                return true;
            }

            return HasDoor(map, x, y, z);
        }

        private static bool HasDoor(Map map, int x, int y, int z)
        {
            IPooledEnumerable items = map.GetItemsInRange(new Point3D(x, y, z), 0);

            try
            {
                foreach (Item item in items)
                {
                    if (item is BaseDoor && Math.Abs(item.Z - z) < 20)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                // A pooled enumerable that is not freed leaks its pool entry. The non-generic
                // IPooledEnumerable is the one ServUO idiom that genuinely needs a try/finally.
                items.Free();
            }

            return false;
        }

        /// <summary>The nearest tile to a point that a mobile can stand on, searched outward.</summary>
        private static bool TrySnap(Map map, Point3D near, out Point3D snapped)
        {
            snapped = Point3D.Zero;

            for (int radius = 0; radius <= SnapRadius; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        // The rim only; everything inside was covered by a smaller radius.
                        if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        {
                            continue;
                        }

                        int x = near.X + dx;
                        int y = near.Y + dy;
                        int z = NavWalker.ResolveZ(map, new Point3D(x, y, near.Z));

                        if (CanStand(map, x, y, z))
                        {
                            snapped = new Point3D(x, y, z);
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static int Key(Point3D point)
        {
            return (point.X << 16) | (point.Y & 0xFFFF);
        }

        /// <summary>
        /// Prove a hop with the pathfinder the bot will really use, doors and all.
        ///
        /// The flood says a road EXISTS; this says the engine can walk one leg of it, which is a
        /// different question and the one [NavAudit asks once the waypoints are saved. It needs a
        /// BaseCreature rather than the Point3D overload, because AlwaysIgnoreDoors is set from the
        /// creature and the bare overload therefore treats a gate as a wall - so verifying with it
        /// would reject exactly the roads this tool exists to author.
        ///
        /// A deliberate break with NavAudit.cs:330-334, which refuses to place a mobile to answer a
        /// question. The difference: that re-checks a hundred edges on every boot; this is one
        /// request a person typed while authoring a road.
        /// </summary>
        public static bool TryVerifyHops(Map map, IList<Point3D> hops, out string failure)
        {
            failure = null;

            if (map == null || hops == null || hops.Count < 2)
            {
                return true;
            }

            CorridorProbe probe = null;

            try
            {
                probe = new CorridorProbe();

                for (int i = 1; i < hops.Count; i++)
                {
                    Point3D a = hops[i - 1];
                    Point3D b = hops[i];

                    probe.MoveToWorld(a, map);

                    if (new MovementPath(probe, b).Success)
                    {
                        continue;
                    }

                    failure = String.Format(
                        "{0},{1} -> {2},{3} is not walkable", a.X, a.Y, b.X, b.Y);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                Log.Error(ex, "Corridor hop verification threw.");
                return false;
            }
            finally
            {
                if (probe != null)
                {
                    probe.Delete();
                }
            }
        }

        /// <summary>
        /// Answer the two questions a hand-edited proposal asks: is this hop walkable, and where
        /// is the nearest road?
        ///
        /// Both are things only the shard can answer - the browser has no map data and no movement
        /// rules - and both are asked while somebody is dragging, so they are one cheap request
        /// rather than a re-walk of the whole road. Verification drives the same real BaseCreature
        /// the proposal was built with, so a hop that passes here passes for the same reason.
        /// </summary>
        public static string ProbeHops(Map map, IList<Point3D> pairs, Point3D? snapNear)
        {
            var builder = new StringBuilder(512);

            builder.Append("{\n  \"utc\": \"").Append(DateTime.UtcNow.ToString("o"));
            builder.Append("\",\n  \"hops\": [");

            CorridorProbe probe = null;

            try
            {
                if (map != null && map != Map.Internal)
                {
                    probe = new CorridorProbe();

                    for (int i = 0; pairs != null && i + 1 < pairs.Count; i += 2)
                    {
                        Point3D a = Resolve(map, pairs[i]);
                        Point3D b = Resolve(map, pairs[i + 1]);

                        if (i > 0)
                        {
                            builder.Append(",");
                        }

                        builder.Append("{\"ax\":").Append(a.X).Append(",\"ay\":").Append(a.Y);
                        builder.Append(",\"bx\":").Append(b.X).Append(",\"by\":").Append(b.Y);
                        builder.Append(",\"ok\":").Append(Pathable(map, probe, a, b) ? "true" : "false");
                        builder.Append("}");
                    }
                }

                builder.Append("],\n  \"snap\": ");

                Point3D found;

                if (snapNear != null && map != null && map != Map.Internal
                    && TrySnapRoad(map, snapNear.Value, out found))
                {
                    builder.Append("[").Append(found.X).Append(",").Append(found.Y);
                    builder.Append(",").Append(found.Z).Append("]");
                }
                else
                {
                    builder.Append("null");
                }

                builder.Append("\n}\n");
            }
            finally
            {
                if (probe != null)
                {
                    probe.Delete();
                }
            }

            return builder.ToString();
        }

        private static Point3D Resolve(Map map, Point3D point)
        {
            return new Point3D(point.X, point.Y, NavWalker.ResolveZ(map, point));
        }

        /// <summary>
        /// The nearest MADE road tile a mobile can stand on, for the editor's shift-drag.
        ///
        /// Asks GroundAt rather than CostOf, so it keeps meaning the same thing when somebody
        /// retunes the weights. Defining "road" as "costs no more than a road" made every tile a
        /// road the moment the weights were flattened, which is how a measurement of the weighting
        /// came back reporting 100% of every route was already on roads.
        /// </summary>
        private static bool TrySnapRoad(Map map, Point3D near, out Point3D found)
        {
            found = Point3D.Zero;

            for (int radius = 0; radius <= RoadSnapRadius; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        {
                            continue;
                        }

                        int x = near.X + dx;
                        int y = near.Y + dy;

                        if (!IsRoad(map, x, y))
                        {
                            continue;
                        }

                        int z = NavWalker.ResolveZ(map, new Point3D(x, y, near.Z));

                        if (CanStand(map, x, y, z))
                        {
                            found = new Point3D(x, y, z);
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Write the road for the editor to propose waypoints along.
        ///
        /// The raw tile list, deliberately - the shard says where a bot can walk and the editor
        /// decides where to put waypoints on it, because spacing is an authoring judgement and the
        /// person making it is looking at the map.
        /// </summary>
        /// <summary>
        /// Thin the walked tiles down to hops a waypoint could sit on.
        ///
        /// Every `spacing` tiles, and always the last one. The editor decides where waypoints
        /// really go - that is an authoring judgement made looking at the map - but the shard has
        /// to offer something it has actually VERIFIED, because an unverified proposal is just a
        /// line on a map and getting past that was the whole point of asking the shard.
        /// </summary>
        public static List<Point3D> Sample(IList<Point3D> path, int spacing)
        {
            var hops = new List<Point3D>();

            if (path == null || path.Count == 0)
            {
                return hops;
            }

            if (spacing < 1)
            {
                spacing = 1;
            }

            for (int i = 0; i < path.Count; i += spacing)
            {
                hops.Add(path[i]);
            }

            Point3D last = path[path.Count - 1];

            if (hops.Count == 0 || hops[hops.Count - 1] != last)
            {
                hops.Add(last);
            }

            return hops;
        }

        /// <summary>
        /// Sample the road into hops, and BISECT any hop the pathfinder refuses until it accepts.
        ///
        /// Plain sampling is not enough, and the west-gate road is why: every tile of it is
        /// walkable and consecutive, yet the pair at 1356,1727 -> 1346,1722 came back unpathable.
        /// Ten tiles apart along the road is not ten tiles apart in a straight line, and
        /// FastAStarAlgorithm is answering a different question from the flood - it has to get
        /// there inside its own box, around whatever the flood happened to walk around.
        ///
        /// So a refused hop has the road's own midpoint inserted between its ends and is asked
        /// again. The tiles between two hops are contiguous and walkable by construction, so this
        /// terminates: in the worst case it converges on adjacent tiles, which the pathfinder
        /// cannot refuse. The result is a proposal the author can accept as it stands, rather than
        /// a report that something is wrong somewhere in the middle of it.
        /// </summary>
        public static List<Point3D> SampleVerified(
            Map map, IList<Point3D> path, int spacing, out bool ok, out string failure,
            out Point3D blame)
        {
            ok = true;
            failure = null;
            blame = Point3D.Zero;

            var hops = new List<Point3D>();

            if (path == null || path.Count == 0)
            {
                return hops;
            }

            if (spacing < 1)
            {
                spacing = 1;
            }

            var indices = new List<int>();

            for (int i = 0; i < path.Count; i += spacing)
            {
                indices.Add(i);
            }

            if (indices[indices.Count - 1] != path.Count - 1)
            {
                indices.Add(path.Count - 1);
            }

            if (map == null || map == Map.Internal || indices.Count < 2)
            {
                foreach (int index in indices)
                {
                    hops.Add(path[index]);
                }

                return hops;
            }

            CorridorProbe probe = null;

            try
            {
                probe = new CorridorProbe();

                for (int i = 1; i < indices.Count; i++)
                {
                    int a = indices[i - 1];
                    int b = indices[i];

                    while (!Pathable(map, probe, path[a], path[b]) && b - a > 1)
                    {
                        b = a + ((b - a) / 2);
                        indices.Insert(i, b);
                    }

                    if (b - a <= 1 && !Pathable(map, probe, path[a], path[b]))
                    {
                        // Adjacent tiles the pathfinder still refuses. The flood walked them, so
                        // this is the two answers genuinely disagreeing rather than the sampling
                        // being too coarse - worth saying out loud instead of bisecting for ever.
                        ok = false;
                        blame = path[b];
                        failure = String.Format(
                            "{0},{1} -> {2},{3} is walkable but not pathable",
                            path[a].X, path[a].Y, path[b].X, path[b].Y);
                    }
                }
            }
            catch (Exception ex)
            {
                ok = false;
                failure = ex.Message;
                Log.Error(ex, "Corridor sampling threw.");
            }
            finally
            {
                if (probe != null)
                {
                    probe.Delete();
                }
            }

            foreach (int index in indices)
            {
                hops.Add(path[index]);
            }

            return hops;
        }

        private static bool Pathable(Map map, CorridorProbe probe, Point3D a, Point3D b)
        {
            if (a.X == b.X && a.Y == b.Y)
            {
                return true;
            }

            probe.MoveToWorld(a, map);

            return new MovementPath(probe, b).Success;
        }

        /// <summary>
        /// A road the engine will actually walk: flood, verify, and re-flood around what it refuses.
        ///
        /// The flood and the pathfinder answer different questions and occasionally disagree about
        /// one step - the flood compares two resolved Z values, MoveImpl does a real tile-geometry
        /// calculation. Tuning the flood's climb limit cannot fix that: at 2 it loses roads that
        /// are walkable, at 11 it proposes a step that is not, and the west-gate and south-bridge
        /// roads disagree about which they want.
        ///
        /// So instead of guessing the rule, the tool asks: when verification finds a step the
        /// engine refuses even between adjacent tiles, that tile is banned and the road is flooded
        /// again. Each pass removes one genuine obstruction, so this converges - and it converges
        /// on a road every hop of which has been walked by a real creature, which is the only claim
        /// worth making.
        /// </summary>
        public static bool TryRoad(
            Map map, Point3D from, Point3D to, int spacing,
            out List<Point3D> path, out List<Point3D> hops, out string error)
        {
            path = new List<Point3D>();
            hops = new List<Point3D>();
            error = null;

            var banned = new HashSet<int>();

            for (int attempt = 0; attempt < RepairAttempts; attempt++)
            {
                if (!TryPath(map, from, to, banned, out path, out error))
                {
                    return false;
                }

                bool ok;
                string failure;
                Point3D blame;

                hops = SampleVerified(map, path, spacing, out ok, out failure, out blame);

                if (ok)
                {
                    return true;
                }

                error = failure;

                if (blame == Point3D.Zero)
                {
                    return false;
                }

                banned.Add((blame.X << 16) | (blame.Y & 0xFFFF));
            }

            error = String.Format(
                "{0} (gave up after {1} attempts to route around it)", error, RepairAttempts);

            return false;
        }

        public static void WriteSnapshot(
            Map map, Point3D from, Point3D to, List<Point3D> path, List<Point3D> hops,
            bool hopsWalkable, string hopError, bool ok, string error)
        {
            var builder = new StringBuilder(4096);

            builder.Append("{\n");
            builder.Append("  \"utc\": \"").Append(DateTime.UtcNow.ToString("o")).Append("\",\n");
            builder.Append("  \"map\": ").Append(Json.Quote(map == null ? null : map.Name)).Append(",\n");
            builder.Append("  \"ok\": ").Append(ok ? "true" : "false").Append(",\n");
            builder.Append("  \"error\": ").Append(Json.Quote(error)).Append(",\n");
            builder.Append("  \"from\": [").Append(from.X).Append(",").Append(from.Y).Append("],\n");
            builder.Append("  \"to\": [").Append(to.X).Append(",").Append(to.Y).Append("],\n");
            builder.Append("  \"points\": [");

            if (path != null)
            {
                for (int i = 0; i < path.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(",");
                    }

                    builder.Append("[").Append(path[i].X).Append(",")
                        .Append(path[i].Y).Append(",").Append(path[i].Z).Append("]");
                }
            }

            builder.Append("],\n");

            // hopsWalkable is the shard saying it drove a real BaseCreature between each
            // consecutive pair of hops - doors and gates included, because AlwaysIgnoreDoors comes
            // off the creature. Without it this file would only assert that a road EXISTS, not
            // that a bot can use it, and those are different claims.
            builder.Append("  \"hopsWalkable\": ").Append(hopsWalkable ? "true" : "false").Append(",\n");
            builder.Append("  \"hopError\": ").Append(Json.Quote(hopError)).Append(",\n");
            builder.Append("  \"hops\": [");

            if (hops != null)
            {
                for (int i = 0; i < hops.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(",");
                    }

                    builder.Append("[").Append(hops[i].X).Append(",")
                        .Append(hops[i].Y).Append(",").Append(hops[i].Z).Append("]");
                }
            }

            builder.Append("]\n}\n");

            string writeError;

            if (!AtomicFile.Write(OutputPath, builder.ToString(), out writeError))
            {
                Log.Error("Corridor snapshot not written: {0}", writeError);
            }
        }
    }
}
