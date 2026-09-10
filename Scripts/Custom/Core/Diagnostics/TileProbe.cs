using System;
using System.Collections.Generic;
using System.Text;

using Server.Commands;
using Server.Mobiles;
using Server.Movement;

namespace Server.Custom
{
    /// <summary>
    /// Everything the engine knows about one tile, and whether it will let a mobile step onto it.
    ///
    /// WHY THIS EXISTS. "A player can walk onto lamp posts" is a claim about `CheckMovement`, and
    /// every instrument this shard had answered a different question. `[NavAudit` pathfinds an
    /// edge, which is `MovementPath` over hundreds of tiles and says nothing about which one was
    /// wrong. `nav-hop` asks the same thing for one hop. `[BotSiteAudit` reports what is under a
    /// work-site arrival. None of them asks the engine, about one tile, from one direction, and
    /// prints the answer.
    ///
    /// THE THREE ITEM LISTS ARE THE POINT, not a detail. `[WorldItems` walks `World.Items`
    /// (WorldItemSnapshot.cs:183) and `FastMovementImpl` reads `map.GetSector(x, y).Items`
    /// (FastMovement.cs:407-425). An item in the first and not the second is drawn by the client,
    /// reported by every snapshot we own, matched against the decoration file it came from - and
    /// invisible to movement. Nothing we had could have seen that, so this prints all three lists
    /// side by side and says when they disagree.
    ///
    /// It also prints the three process-global switches. `MovementImpl.IgnoreMovableImpassables`
    /// and `AlwaysIgnoreDoors` are plain statics set without `try`/`finally` (BaseAI.cs:2358,
    /// FastAStarAlgorithm.cs:93-94, SlowAStarAlgorithm.cs:156-157, PlayerMobile.cs:2081) and
    /// cleared only on explicit return paths, so an exception thrown inside `Move` leaves either
    /// one set for every mobile for the rest of the process. Reading them is one line and it is
    /// the cheapest of all the hypotheses to rule out.
    ///
    /// Read-only. The one mobile it creates is the same ephemeral probe NavCorridor uses, deleted
    /// in a `finally`, with `Timer.DelayCall(Delete)` on deserialize in case a crash lands one in
    /// a save.
    /// </summary>
    public static class TileProbe
    {
        private static readonly CustomLogger Log = CustomLogger.For("Tile");

        /// <summary>Where the token answer goes, for the editor and for a headless run.</summary>
        public const string SnapshotPath = "Data/Live/tile-probe.json";

        public static void Initialize()
        {
            CommandSystem.Register("TileProbe", AccessLevel.Administrator, TileProbe_OnCommand);
        }

        [Usage("TileProbe [<x> <y> [z]]")]
        [Description(
            "Reports what the engine sees at a tile - land, statics, items in all three lists, "
            + "the movement switches, and whether a step onto it is refused from each neighbour. "
            + "With no arguments, probes the tile you are standing on.")]
        private static void TileProbe_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            int x = from.X;
            int y = from.Y;
            int? z = from.Z;

            if (e.Length >= 2)
            {
                x = e.GetInt32(0);
                y = e.GetInt32(1);
                z = e.Length >= 3 ? (int?)e.GetInt32(2) : null;
            }

            IList<string> lines = Describe(from.Map, x, y, z);

            CommandReport.Send(from, String.Format("Tile {0},{1}", x, y), lines);
        }

        /// <summary>
        /// The report, as lines. Also what the `tile-probe` token writes and what a headless run
        /// reads off the console.
        /// </summary>
        public static IList<string> Describe(Map map, int x, int y, int? hintZ)
        {
            var lines = new List<string>();

            if (map == null || map == Map.Internal)
            {
                lines.Add("No facet.");
                return lines;
            }

            AppendSwitches(lines);
            AppendLand(lines, map, x, y);
            AppendStatics(lines, map, x, y);

            int standZ = AppendStanding(lines, map, x, y, hintZ);

            AppendItems(lines, map, x, y, standZ);
            AppendSteps(lines, map, x, y, standZ);

            return lines;
        }

        /// <summary>
        /// Every world item in an ItemID range, and whether the engine will let a mobile step onto
        /// the tile it stands on - reported only for the ones it WILL.
        ///
        /// FOUR SAMPLES ARE NOT AN ANSWER TO "CAN A PLAYER WALK ONTO LAMP POSTS". A lamp post is
        /// Impassable and 15 tall, so it blocks a step from a surface at its own Z - but the rule
        /// is `item.Z + CalcHeight &lt;= ourZ`, and an item sunk more than its own height below the
        /// surface somebody is walking on stops blocking, correctly. Decoration is placed at an
        /// authored Z from a .cfg file; whether that Z is under the walking surface is a fact about
        /// 519 separate tiles, and the only honest way to answer it is to ask about all of them.
        ///
        /// Reports the passable ones only, plus a count, because the useful output of a sweep is
        /// the exceptions and a total that says how hard it looked.
        /// </summary>
        public static IList<string> Sweep(Map map, int fromId, int toId)
        {
            var lines = new List<string>();

            if (map == null || map == Map.Internal)
            {
                lines.Add("No facet.");
                return lines;
            }

            AppendSwitches(lines);

            var found = new List<Item>();

            // World.Items on an explicit diagnostic (CLAUDE.md section 15): there is no index of
            // world items by ItemID, and building one for a command that runs by hand would be a
            // hook in an upstream file for the sake of a report.
            foreach (Item item in World.Items.Values)
            {
                if (item != null && !item.Deleted && item.Map == map && item.Parent == null
                    && item.ItemID >= fromId && item.ItemID <= toId)
                {
                    found.Add(item);
                }
            }

            var probe = new StepProbe();
            int passable = 0;

            try
            {
                foreach (Item item in found)
                {
                    string how = FirstAllowedStep(map, probe, item.X, item.Y, item.Z);

                    if (how == null)
                    {
                        continue;
                    }

                    passable++;

                    ItemData data = item.ItemData;

                    lines.Add(String.Format(
                        "PASSABLE 0x{0:X4} '{1}' at {2},{3} z {4} (land {5}, h {6}) - {7}",
                        item.ItemID,
                        data.Name,
                        item.X,
                        item.Y,
                        item.Z,
                        map.GetAverageZ(item.X, item.Y),
                        data.CalcHeight,
                        how));
                }
            }
            finally
            {
                probe.Delete();
            }

            lines.Insert(1, String.Format(
                "SWEEP 0x{0:X4}-0x{1:X4} on {2}: {3} item(s), {4} passable",
                fromId,
                toId,
                map.Name,
                found.Count,
                passable));

            return lines;
        }

        /// <summary>
        /// The first neighbour the engine will let a creature step onto this tile from, or null.
        ///
        /// The step is asked from the tile the walker would actually be on, so the Z it starts at
        /// is resolved the walker's way rather than copied from the item - an item sunk below the
        /// street is exactly the case being looked for, and starting the probe at the item's own Z
        /// would ask about standing in the hole with it.
        /// </summary>
        private static string FirstAllowedStep(Map map, BaseCreature probe, int x, int y, int itemZ)
        {
            for (int i = 0; i < 8; i++)
            {
                var d = (Direction)i;

                int fx = x, fy = y;

                Offset((Direction)(((int)d + 4) & 0x7), ref fx, ref fy);

                int fromZ = NavWalker.ResolveZ(map, new Point3D(fx, fy, itemZ));
                var from = new Point3D(fx, fy, fromZ);

                if (!map.CanFit(fx, fy, fromZ, 16, false, false, true))
                {
                    continue;
                }

                probe.MoveToWorld(from, map);

                int newZ;

                if (Movement.Movement.CheckMovement(probe, map, from, d, out newZ))
                {
                    return String.Format("step {0} from {1},{2},{3} lands at z {4}", d, fx, fy, fromZ, newZ);
                }
            }

            return null;
        }

        // ---- the switches ----

        private static void AppendSwitches(IList<string> lines)
        {
            IMovementImpl impl = Movement.Movement.Impl;

            lines.Add(String.Format(
                "Movement.Impl is '{0}'; FastMovementImpl.Enabled={1}",
                impl == null ? "(null)" : impl.GetType().Name,
                FastMovementImpl.Enabled));

            // A leak here is the whole answer to a passability question, so it is stated as a
            // verdict rather than left as two booleans to interpret.
            bool leak = MovementImpl.IgnoreMovableImpassables || MovementImpl.AlwaysIgnoreDoors;

            lines.Add(String.Format(
                "MovementImpl.IgnoreMovableImpassables={0}, AlwaysIgnoreDoors={1}{2}",
                MovementImpl.IgnoreMovableImpassables,
                MovementImpl.AlwaysIgnoreDoors,
                leak
                    ? "  <-- SET ON AN IDLE SHARD. These are set without try/finally and cleared "
                        + "only on explicit return paths, so this is a leak."
                    : ""));
        }

        // ---- land ----

        private static void AppendLand(IList<string> lines, Map map, int x, int y)
        {
            LandTile tile = map.Tiles.GetLandTile(x, y);
            LandData data = TileData.LandTable[tile.ID & TileData.MaxLandValue];

            int low = 0, average = 0, top = 0;

            map.GetAverageZ(x, y, ref low, ref average, ref top);

            lines.Add(String.Format(
                "LAND 0x{0:X4} '{1}' z {2}{3} flags [{4}]; GetAverageZ low {5} avg {6} top {7}",
                tile.ID,
                data.Name,
                tile.Z,
                tile.Ignored ? " (ignored)" : "",
                Flags(data.Flags),
                low,
                average,
                top));
        }

        // ---- statics ----

        private static void AppendStatics(IList<string> lines, Map map, int x, int y)
        {
            StaticTile[] tiles = map.Tiles.GetStaticTiles(x, y, true);

            if (tiles == null || tiles.Length == 0)
            {
                lines.Add("STATICS none");
                return;
            }

            for (int i = 0; i < tiles.Length; i++)
            {
                ItemData data = TileData.ItemTable[tiles[i].ID & TileData.MaxItemValue];

                lines.Add(String.Format(
                    "STATIC 0x{0:X4} '{1}' z {2} h {3} calc {4} flags [{5}]",
                    tiles[i].ID,
                    data.Name,
                    tiles[i].Z,
                    data.Height,
                    data.CalcHeight,
                    Flags(data.Flags)));
            }
        }

        // ---- where a mobile would stand ----

        private static int AppendStanding(IList<string> lines, Map map, int x, int y, int? hintZ)
        {
            int hint = hintZ ?? 0;

            int resolved;
            bool found = NavWalker.TryResolveZ(map, new Point3D(x, y, hint), out resolved);

            int fallback = NavWalker.ResolveZ(map, new Point3D(x, y, hint));

            lines.Add(String.Format(
                "STAND hint z {0}: TryResolveZ {1} ({2}), ResolveZ {3}; CanFit {4}, CanSpawnMobile {5}",
                hint,
                found ? "yes" : "NO",
                resolved,
                fallback,
                map.CanFit(x, y, fallback, 16, false, false, true),
                map.CanSpawnMobile(x, y, fallback)));

            return fallback;
        }

        // ---- items, in all three lists ----

        private static void AppendItems(IList<string> lines, Map map, int x, int y, int z)
        {
            var world = new Dictionary<Serial, Item>();
            var sector = new HashSet<Serial>();
            var ranged = new HashSet<Serial>();

            // WALKING World.Items, on an explicit diagnostic only (CLAUDE.md section 15). That is
            // the point of this pass: World.Items is the list every snapshot we own is built from,
            // and the sector list is the one movement reads. Comparing them is the measurement,
            // so it cannot be done from the cheaper side alone.
            foreach (Item item in World.Items.Values)
            {
                if (item != null && !item.Deleted && item.Map == map && item.Parent == null
                    && item.X == x && item.Y == y)
                {
                    world[item.Serial] = item;
                }
            }

            Sector s = map.GetSector(x, y);

            if (s != null && s.Items != null)
            {
                foreach (Item item in s.Items)
                {
                    if (item != null && !item.Deleted && item.AtWorldPoint(x, y))
                    {
                        sector.Add(item.Serial);

                        if (!world.ContainsKey(item.Serial))
                        {
                            world[item.Serial] = item;
                        }
                    }
                }
            }

            IPooledEnumerable<Item> eable = map.GetItemsInRange(new Point3D(x, y, z), 0);

            try
            {
                foreach (Item item in eable)
                {
                    if (item != null && !item.Deleted && item.AtWorldPoint(x, y))
                    {
                        ranged.Add(item.Serial);

                        if (!world.ContainsKey(item.Serial))
                        {
                            world[item.Serial] = item;
                        }
                    }
                }
            }
            finally
            {
                eable.Free();
            }

            if (world.Count == 0)
            {
                lines.Add("ITEMS none in any list");
                return;
            }

            foreach (KeyValuePair<Serial, Item> pair in world)
            {
                Item item = pair.Value;
                ItemData data = item.ItemData;

                bool inSector = sector.Contains(pair.Key);

                lines.Add(String.Format(
                    "ITEM 0x{0:X} 0x{1:X4} '{2}' z {3} h {4} calc {5} movable {6} visible {7} "
                    + "flags [{8}] lists [{9}]{10}",
                    pair.Key.Value,
                    item.ItemID,
                    data.Name,
                    item.Z,
                    data.Height,
                    data.CalcHeight,
                    item.Movable,
                    item.Visible,
                    Flags(data.Flags),
                    String.Format(
                        "{0}{1}{2}",
                        world.ContainsKey(pair.Key) ? "World " : "",
                        inSector ? "Sector " : "",
                        ranged.Contains(pair.Key) ? "Range" : ""),
                    inSector
                        ? ""
                        : "  <-- NOT IN THE SECTOR LIST. FastMovementImpl reads that list, so "
                            + "this item does not block a step."));
            }
        }

        // ---- the engine's own answer ----

        private static void AppendSteps(IList<string> lines, Map map, int x, int y, int z)
        {
            var target = new Point3D(x, y, z);
            var probe = new StepProbe();

            try
            {
                for (int i = 0; i < 8; i++)
                {
                    var d = (Direction)i;

                    // The tile a step in direction `d` LANDS on is the target, so the mobile has
                    // to stand on the opposite one. Walking the offsets backwards is how the
                    // question stays "can something get onto this tile" rather than "can
                    // something leave it".
                    int fx = x, fy = y;

                    Offset((Direction)(((int)d + 4) & 0x7), ref fx, ref fy);

                    int fromZ = NavWalker.ResolveZ(map, new Point3D(fx, fy, z));
                    var from = new Point3D(fx, fy, fromZ);

                    probe.MoveToWorld(from, map);

                    int mobileZ, pointZ;

                    bool asMobile = Movement.Movement.CheckMovement(probe, map, from, d, out mobileZ);
                    bool asPoint = Movement.Movement.CheckMovement(from, map, from, d, out pointZ);

                    lines.Add(String.Format(
                        "STEP {0} from {1},{2},{3}: creature {4} (z {5}), point {6} (z {7})",
                        d,
                        fx,
                        fy,
                        fromZ,
                        asMobile ? "ALLOWED" : "refused",
                        mobileZ,
                        asPoint ? "ALLOWED" : "refused",
                        pointZ));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Step probe failed at {0},{1}.", x, y);
                lines.Add("STEP probe threw: " + ex.Message);
            }
            finally
            {
                probe.Delete();
            }

            // Said last, because it is the sentence somebody reads: the target tile's own
            // occupancy answer, which is what a walker asks before it aims at a tile.
            lines.Add(String.Format(
                "GOAL {0},{1},{2}: CanFit {3}",
                target.X,
                target.Y,
                target.Z,
                map.CanFit(target.X, target.Y, target.Z, 16, false, false, true)));
        }

        /// <summary>The engine's own offsets, copied rather than reached for - Movement's are private.</summary>
        private static void Offset(Direction d, ref int x, ref int y)
        {
            switch (d & Direction.Mask)
            {
                case Direction.North: --y; break;
                case Direction.Right: ++x; --y; break;
                case Direction.East: ++x; break;
                case Direction.Down: ++x; ++y; break;
                case Direction.South: ++y; break;
                case Direction.Left: --x; ++y; break;
                case Direction.West: --x; break;
                case Direction.Up: --x; --y; break;
            }
        }

        /// <summary>
        /// The flags that decide passability, named, and nothing else.
        ///
        /// `Flags.ToString()` prints all sixteen a tile happens to carry, and the four that matter
        /// are then something you find by reading. Impassable, Surface, Bridge and Wall are the
        /// ones every rule in FastMovementImpl is written in terms of.
        /// </summary>
        private static string Flags(TileFlag flags)
        {
            var builder = new StringBuilder(32);

            Append(builder, flags, TileFlag.Impassable, "Impassable");
            Append(builder, flags, TileFlag.Surface, "Surface");
            Append(builder, flags, TileFlag.Bridge, "Bridge");
            Append(builder, flags, TileFlag.Wall, "Wall");
            Append(builder, flags, TileFlag.Wet, "Wet");
            Append(builder, flags, TileFlag.Door, "Door");

            return builder.Length == 0 ? "none" : builder.ToString();
        }

        private static void Append(StringBuilder builder, TileFlag flags, TileFlag one, string name)
        {
            if ((flags & one) == 0)
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(name);
        }

        /// <summary>
        /// The mobile the step questions are asked with: an uncontrolled BaseCreature, which is
        /// what a bot is, rather than the Point3D overload - `checkMobs` and `AlwaysIgnoreDoors`
        /// are both read off the mobile, so the two overloads can and do disagree.
        /// </summary>
        private class StepProbe : BaseCreature
        {
            public StepProbe()
                : base(AIType.AI_Melee, FightMode.None, 10, 1, 0.2, 0.4)
            {
                Body = 0x190;
                Name = "a tile probe";

                Blessed = true;
                Hidden = true;
                CantWalk = false;

                Karma = 0;
                Fame = 0;
            }

            public StepProbe(Serial serial)
                : base(serial)
            {
            }

            public override bool CanOpenDoors { get { return false; } }

            public override void Serialize(GenericWriter writer)
            {
                base.Serialize(writer);
                writer.Write(0);
            }

            public override void Deserialize(GenericReader reader)
            {
                base.Deserialize(reader);
                reader.ReadInt();

                // Created and deleted inside one request, so it should never reach a save at all
                // - but a crash between the two would leave a hidden mobile in the world for ever.
                Timer.DelayCall(Delete);
            }
        }
    }
}
