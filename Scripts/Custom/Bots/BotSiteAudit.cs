// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotSiteAudit.cs — what is ACTUALLY under every work-site coordinate.
//
// A diagnostic, written because the 7e site data was wrong in a way that every
// check it had passed. [NavAudit said the corridor was walkable (it is),
// BotWorkSites said the faces were harvestable (it did not agree with the map),
// and Bots.Shift passed (it was measuring the wrong thing). Meanwhile the editor
// draws brit-minenorth-* across Castle Britannia's moat and both faces on green
// land west of the castle.
//
// So this prints, for every arrival point and every corridor waypoint, the
// ground truth from the shard's own TileMatrix and nothing else:
//
//   land tile id (dec + hex) and its TileData name
//   whether the mining definition accepts it   - Mining.OreAndStone.Validate
//   whether the lumber definition accepts it   - via the statics, as the bot sees them
//   every static on the tile, and whether any validates
//   walkable                                    - map.CanFit at NavWalker.ResolveZ
//   water                                       - TileData land flag Wet
//   impassable                                  - TileData land flag Impassable
//
// It also writes Data/Live/site-audit.json so the same coordinates can be held
// against the editor's exported radar tiles, which are rendered through this
// same TileMatrix and therefore MUST agree. Where they do not, one of the two
// is reading a different client install, and that is the bug.

using System;
using System.Collections.Generic;
using System.Text;

using Server.Commands;
using Server.Engines.Harvest;

namespace Server.Custom
{
    public static class BotSiteAudit
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        public const string SnapshotPath = "Data/Live/site-audit.json";

        public static void Initialize()
        {
            CommandSystem.Register("BotSiteAudit", AccessLevel.Administrator, OnCommand);
            CommandSystem.Register("BotOreSweep", AccessLevel.Administrator, OnSweep);
            CommandSystem.Register("BotSitePick", AccessLevel.Administrator, OnPick);
            CommandSystem.Register("BotForgeAudit", AccessLevel.Administrator, OnForge);

            if (Config.Get("Custom.BotSiteAuditOnStart", false))
            {
                EventSink.ServerStarted += () => { Run(null); Forge(null); };
            }
        }

        [Usage("BotSiteAudit")]
        [Description("Prints the real land tile, statics and walkability under every work-site arrival and corridor waypoint.")]
        private static void OnCommand(CommandEventArgs e)
        {
            Capture(e.Mobile, "[BotSiteAudit", () => Run(e.Mobile));
        }

        [Usage("BotOreSweep [radius]")]
        [Description("Sweeps a wide box around Britain for DENSE harvestable ground and names the land tiles, so a real rock face can be told from scattered forest-transition tiles.")]
        private static void OnSweep(CommandEventArgs e)
        {
            Capture(e.Mobile, "[BotOreSweep", () => Sweep(e.Mobile, e.Length > 0 ? e.GetInt32(0) : 400));
        }

        [Usage("BotSitePick <x> <y> [mine|chop]")]
        [Description("Lists the best standable arrival tiles around a point, ranked by how much is harvestable within reach.")]
        private static void OnPick(CommandEventArgs e)
        {
            if (e.Length < 2)
            {
                e.Mobile.SendMessage(0x25, "Usage: [BotSitePick <x> <y> [mine|chop]");
                return;
            }

            Capture(e.Mobile, "[BotSitePick", () =>
                Pick(e.Mobile, e.GetInt32(0), e.GetInt32(1),
                    e.Length > 2 && Insensitive.Equals(e.GetString(2), "chop")));
        }

        /// <summary>
        /// The best places to STAND, ranked by what a bot could actually reach from them.
        ///
        /// This is the check the first cut of the sites never made. It ranks by the count of
        /// harvestable tiles inside BotHarvest.FindTarget's own 5x5 sweep, so a tile that scores
        /// well is one a bot can work without moving - and a face made of these is a face that
        /// keeps producing as the resource banks under it deplete.
        /// </summary>
        public static void Pick(Mobile from, int cx, int cy, bool chop)
        {
            Map map = Map.Trammel;
            HarvestDefinition mine = BotHarvest.DefinitionFor(BotClass.Miner);
            HarvestDefinition wood = BotHarvest.DefinitionFor(BotClass.Lumberjack);
            HarvestDefinition want = chop ? wood : mine;

            const int scan = 20;
            var scored = new List<KeyValuePair<Point3D, int>>();

            for (int x = cx - scan; x <= cx + scan; x++)
            {
                for (int y = cy - scan; y <= cy + scan; y++)
                {
                    int z = NavWalker.ResolveZ(map, new Point3D(x, y, 0));

                    if (!map.CanFit(x, y, z, 16, false, false, true))
                    {
                        continue;
                    }

                    int reach = 0;

                    for (int dx = -want.MaxRange; dx <= want.MaxRange; dx++)
                    {
                        for (int dy = -want.MaxRange; dy <= want.MaxRange; dy++)
                        {
                            LandTile nl = map.Tiles.GetLandTile(x + dx, y + dy);
                            bool hit = !chop && want.Validate(nl.ID);

                            StaticTile[] ns = map.Tiles.GetStaticTiles(x + dx, y + dy, true);

                            for (int i = 0; i < ns.Length && !hit; i++)
                            {
                                hit = want.Validate((ns[i].ID & 0x3FFF) | 0x4000);
                            }

                            if (hit)
                            {
                                reach++;
                            }
                        }
                    }

                    if (reach > 0)
                    {
                        scored.Add(new KeyValuePair<Point3D, int>(new Point3D(x, y, z), reach));
                    }
                }
            }

            scored.Sort((a, b) => b.Value.CompareTo(a.Value));

            Emit(from, String.Format(
                "[BotSitePick] {0} around {1},{2}: {3} standable tile(s) with anything in reach. Best, spread 5 apart:",
                chop ? "CHOP" : "MINE", cx, cy, scored.Count));

            var taken = new List<Point3D>();

            foreach (var pair in scored)
            {
                bool clear = true;

                foreach (Point3D held in taken)
                {
                    if (NavGraph.Chebyshev(pair.Key, held) < 5)
                    {
                        clear = false;
                        break;
                    }
                }

                if (!clear)
                {
                    continue;
                }

                taken.Add(pair.Key);

                LandTile land = map.Tiles.GetLandTile(pair.Key.X, pair.Key.Y);

                Emit(from, String.Format(
                    "   {0},{1},{2}  reach {3}/25  standing on land {4} '{5}'",
                    pair.Key.X, pair.Key.Y, pair.Key.Z, pair.Value, land.ID, SafeLandName(land.ID)));

                if (taken.Count >= 6)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Where is there actually enough rock or wood to be worth walking to?
        ///
        /// This exists because "the harvest definition accepts this tile" turned out to be a much
        /// weaker statement than it sounds. Stock ServUO's m_MountainAndCaveTiles contains land ids
        /// - 236-247 among them - that this client's tiledata names 'forest', so Validate happily
        /// accepts scattered transition tiles in ordinary fields. The first cut of the 7e sites was
        /// built on exactly that: four arrivals with one to four mineable tiles in reach, on green
        /// land west of Castle Britannia, every one of which passed Validate.
        ///
        /// A real face is DENSE. So this counts per 10x10 cell and reports the count alongside the
        /// land names, because the name is what tells you whether you are looking at a mountain or
        /// at the edge of a wood.
        /// </summary>
        public static void Sweep(Mobile from, int radius)
        {
            Map map = Map.Trammel;
            HarvestDefinition mine = BotHarvest.DefinitionFor(BotClass.Miner);
            HarvestDefinition wood = BotHarvest.DefinitionFor(BotClass.Lumberjack);

            // Britain bank, the centre everything in this shard is measured from.
            var origin = new Point3D(1434, 1690, 0);

            var mineCells = new Dictionary<int, int>();
            var chopCells = new Dictionary<int, int>();
            var mineNames = new Dictionary<int, Dictionary<string, int>>();

            for (int x = origin.X - radius; x <= origin.X + radius; x += 1)
            {
                for (int y = origin.Y - radius; y <= origin.Y + radius; y += 1)
                {
                    LandTile land = map.Tiles.GetLandTile(x, y);
                    bool m = mine != null && mine.Validate(land.ID);
                    bool c = false;

                    StaticTile[] statics = map.Tiles.GetStaticTiles(x, y, true);

                    for (int i = 0; i < statics.Length; i++)
                    {
                        int encoded = (statics[i].ID & 0x3FFF) | 0x4000;

                        m = m || (mine != null && mine.Validate(encoded));
                        c = c || (wood != null && wood.Validate(encoded));
                    }

                    int cell = ((x / 10) << 16) | ((y / 10) & 0xFFFF);

                    if (m)
                    {
                        int had;
                        mineCells.TryGetValue(cell, out had);
                        mineCells[cell] = had + 1;

                        Dictionary<string, int> names;

                        if (!mineNames.TryGetValue(cell, out names))
                        {
                            names = new Dictionary<string, int>();
                            mineNames[cell] = names;
                        }

                        string n = SafeLandName(land.ID) + "/" + land.ID;
                        int seen;
                        names.TryGetValue(n, out seen);
                        names[n] = seen + 1;
                    }

                    if (c)
                    {
                        int had;
                        chopCells.TryGetValue(cell, out had);
                        chopCells[cell] = had + 1;
                    }
                }
            }

            Emit(from, String.Format(
                "[BotOreSweep] {0} tiles swept around {1}. Densest 10x10 cells:", (radius * 2 + 1) * (radius * 2 + 1), origin));

            // Sorted by DISTANCE, not by count: every real face saturates at 100, so ranking by
            // count just lists the biggest mountain on the facet. What matters is the nearest
            // patch that is dense enough to be worth the walk.
            Report(from, "MINE", mineCells, mineNames, origin, 40);
            Report(from, "CHOP", chopCells, null, origin, 8);
        }

        private static void Report(
            Mobile from, string label, Dictionary<int, int> cells,
            Dictionary<int, Dictionary<string, int>> names, Point3D origin, int floor)
        {
            var order = new List<KeyValuePair<int, int>>();

            foreach (var pair in cells)
            {
                if (pair.Value >= floor)
                {
                    order.Add(pair);
                }
            }

            order.Sort((a, b) =>
            {
                int ax = (a.Key >> 16) * 10, ay = (short)(a.Key & 0xFFFF) * 10;
                int bx = (b.Key >> 16) * 10, by = (short)(b.Key & 0xFFFF) * 10;
                int da = Math.Max(Math.Abs(ax - origin.X), Math.Abs(ay - origin.Y));
                int db = Math.Max(Math.Abs(bx - origin.X), Math.Abs(by - origin.Y));
                return da.CompareTo(db);
            });

            int shown = 0;

            foreach (var pair in order)
            {
                if (shown >= 10)
                {
                    break;
                }

                int cx = (pair.Key >> 16) * 10;
                int cy = (short)(pair.Key & 0xFFFF) * 10;

                string detail = "";

                if (names != null)
                {
                    Dictionary<string, int> n;

                    if (names.TryGetValue(pair.Key, out n))
                    {
                        var parts = new List<string>();

                        foreach (var kv in n)
                        {
                            parts.Add(kv.Key + " x" + kv.Value);
                        }

                        parts.Sort(StringComparer.Ordinal);
                        detail = " | " + String.Join(", ", parts.ToArray());
                    }
                }

                Emit(from, String.Format(
                    "  {0} cell {1},{2} count {3} dist {4}{5}",
                    label,
                    cx,
                    cy,
                    pair.Value,
                    Math.Max(Math.Abs(cx - origin.X), Math.Abs(cy - origin.Y)),
                    detail));

                shown++;
            }

            if (shown == 0)
            {
                Emit(from, String.Format(
                    "  {0}: no cell with {1}+ tiles anywhere in the box.", label, floor));
            }
        }

        [Usage("BotForgeAudit")]
        [Description("Finds every anvil and forge near the smithy and reports which tiles satisfy DefBlacksmithy from both.")]
        private static void OnForge(CommandEventArgs e)
        {
            Capture(e.Mobile, "[BotForgeAudit", () => Forge(e.Mobile));
        }

        /// <summary>
        /// Where the anvil and forge actually are, and which tiles can work at both.
        ///
        /// They are ITEMS here, not map statics - Data/Decoration/Britannia/britain.cfg places an
        /// AnvilEastAddon and a SmallForgeAddon - so a static scan finds nothing and the audit
        /// above reports "statics 0" at every forge arrival. DefBlacksmithy.CheckAnvilAndForge
        /// looks at both, plus line of sight and a +/-16 Z window, so this reports what it would
        /// see rather than what the map file holds.
        /// </summary>
        public static void Forge(Mobile from)
        {
            Map map = Map.Trammel;
            var centre = new Point3D(1424, 1557, 30);

            var anvils = new List<IEntity>();
            var forges = new List<IEntity>();

            IPooledEnumerable eable = map.GetItemsInRange(centre, 12);

            try
            {
                foreach (Item item in eable)
                {
                    if (IsAnvilId(item.ItemID))
                    {
                        anvils.Add(item);
                    }
                    else if (IsForgeId(item.ItemID))
                    {
                        forges.Add(item);
                    }
                }
            }
            finally
            {
                eable.Free();
            }

            Emit(from, String.Format(
                "[BotForgeAudit] within 12 of {0}: {1} anvil(s), {2} forge(s)",
                centre, anvils.Count, forges.Count));

            foreach (IEntity a in anvils)
            {
                Emit(from, String.Format("   ANVIL 0x{0:X4} at {1}", ((Item)a).ItemID, a.Location));
            }

            foreach (IEntity f in forges)
            {
                Emit(from, String.Format("   FORGE 0x{0:X4} at {1}", ((Item)f).ItemID, f.Location));
            }

            // Now the tiles: which standable spots see both, at DefBlacksmithy's own range of 2.
            Emit(from, "[BotForgeAudit] standable tiles within 2 of BOTH fixtures (LOS is checked for real by CanCraft):");

            int found = 0;

            for (int x = centre.X - 8; x <= centre.X + 8; x++)
            {
                for (int y = centre.Y - 8; y <= centre.Y + 8; y++)
                {
                    int z = NavWalker.ResolveZ(map, new Point3D(x, y, centre.Z));
                    bool fit = map.CanFit(x, y, z, 16, false, false, true);
                    var at = new Point3D(x, y, z);

                    int da = Reach(at, anvils);
                    int df = Reach(at, forges);

                    // Only the geometric candidates are worth explaining.
                    if (da > 2 || df > 2)
                    {
                        continue;
                    }

                    bool usable = fit && da <= 2 && df <= 2;

                    Emit(from, String.Format(
                        "   {0},{1},{2}  fit {3}  anvil d{4}  forge d{5}{6}",
                        x, y, z,
                        fit ? "y" : "N",
                        da, df,
                        usable ? "   <= USABLE" : ""));

                    if (usable)
                    {
                        found++;
                    }
                }
            }

            if (found == 0)
            {
                Emit(from, "   NONE - no standable tile near the smithy is within 2 of both fixtures.");
            }
        }

        private static int Reach(Point3D at, List<IEntity> fixtures)
        {
            int best = Int32.MaxValue;

            foreach (IEntity fixture in fixtures)
            {
                best = Math.Min(best, Math.Max(Math.Abs(fixture.X - at.X), Math.Abs(fixture.Y - at.Y)));
            }

            return best;
        }

        /// <summary>
        /// Is a fixture close enough, and at a workable height?
        ///
        /// DISTANCE AND Z ONLY - deliberately no line-of-sight test. CheckAnvilAndForge uses
        /// Mobile.InLOS, and there is no mobile here; Map.LineOfSight's Point3D overload aims at
        /// the fixture's own tile and answers false even from the tile the anvil stands on, which
        /// is how an earlier pass of this diagnostic managed to report that no tile in Britain can
        /// reach the Britain anvil.
        ///
        /// So this is the PESSIMISTIC half - a fixture that is simply not there, which is what a
        /// wrong coordinate looks like - and it is exact. The optimistic half, a forge behind a
        /// wall, is answered for real by DefBlacksmithy.CanCraft the moment a Smith tries, and
        /// CrafterBehavior surfaces it through Blocked and Bots.Work.
        /// </summary>
        private static bool IsAnvilId(int id)
        {
            return id == 0x0FAF || id == 0x0FB0 || id == 4015 || id == 4016
                || id == 0x2DD5 || id == 0x2DD6;
        }

        private static bool IsForgeId(int id)
        {
            return id == 0x0FB1 || id == 4017 || (id >= 6522 && id <= 6569)
                || (id >= 0x197A && id <= 0x19A9) || id == 0x2DD8 || id == 0xA531 || id == 0xA535;
        }

        private sealed class Row
        {
            public string Kind;
            public string Id;
            public int X;
            public int Y;
            public int AuthoredZ;
            public int LandId;
            public string LandName;
            public int LandZ;
            public int ResolvedZ;
            public bool Mineable;
            public bool Choppable;
            public int StaticCount;
            public string Statics;
            public bool CanFit;
            public bool Wet;
            public bool Impassable;
            public int NearMine;
            public int NearChop;
            public string NearFirst;
        }

        public static void Run(Mobile from)
        {
            Map map = Map.Trammel;

            HarvestDefinition mine = BotHarvest.DefinitionFor(BotClass.Miner);
            HarvestDefinition wood = BotHarvest.DefinitionFor(BotClass.Lumberjack);

            var rows = new List<Row>();

            // Every arrival of every work destination, plus the forge.
            foreach (NavDestination destination in Nav.Destinations(map, null, null))
            {
                if (!BotWorkSites.IsWorkType(destination.Type))
                {
                    continue;
                }

                rows.Add(Probe(map, "centre", destination.Id, destination.Location, mine, wood));

                if (destination.ArrivalList == null)
                {
                    continue;
                }

                for (int i = 0; i < destination.ArrivalList.Count; i++)
                {
                    NavArrival arrival = destination.ArrivalList[i];

                    rows.Add(Probe(
                        map,
                        "arrival",
                        String.Format("{0}#{1}", destination.Id, i),
                        arrival.Location,
                        mine,
                        wood));
                }
            }

            // Every corridor waypoint this session authored.
            foreach (NavWaypoint waypoint in NavigationSystem.Store.Waypoints)
            {
                if (waypoint.Map != map || !IsCorridor(waypoint.Id))
                {
                    continue;
                }

                rows.Add(Probe(map, "waypoint", waypoint.Id, waypoint.Location, mine, wood));
            }

            Emit(from, String.Format("[BotSiteAudit] {0} point(s) on {1}", rows.Count, map));

            foreach (Row row in rows)
            {
                Emit(from, Describe(row));
            }

            Summarise(from, rows);
            Write(rows);
        }

        private static bool IsCorridor(string id)
        {
            return id.StartsWith("brit-minenorth-", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("brit-minesouth-", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("brit-woodpath-", StringComparison.OrdinalIgnoreCase);
        }

        private static Row Probe(
            Map map, string kind, string id, Point3D at, HarvestDefinition mine, HarvestDefinition wood)
        {
            LandTile land = map.Tiles.GetLandTile(at.X, at.Y);
            StaticTile[] statics = map.Tiles.GetStaticTiles(at.X, at.Y, true);

            var row = new Row
            {
                Kind = kind,
                Id = id,
                X = at.X,
                Y = at.Y,
                AuthoredZ = at.Z,
                LandId = land.ID,
                LandName = SafeLandName(land.ID),
                LandZ = land.Z,
                ResolvedZ = NavWalker.ResolveZ(map, at),
                Mineable = mine != null && mine.Validate(land.ID),
                StaticCount = statics.Length,
            };

            TileFlag flags = TileData.LandTable[land.ID & 0x3FFF].Flags;

            row.Wet = (flags & TileFlag.Wet) != 0;
            row.Impassable = (flags & TileFlag.Impassable) != 0;
            row.CanFit = map.CanFit(at.X, at.Y, row.ResolvedZ, 16, false, false, true);

            var names = new StringBuilder();

            for (int i = 0; i < statics.Length; i++)
            {
                int encoded = (statics[i].ID & 0x3FFF) | 0x4000;

                if (mine != null && mine.Validate(encoded))
                {
                    row.Mineable = true;
                }

                if (wood != null && wood.Validate(encoded))
                {
                    row.Choppable = true;
                }

                if (i < 6)
                {
                    if (names.Length > 0)
                    {
                        names.Append(' ');
                    }

                    names.AppendFormat("0x{0:X4}@{1}", statics[i].ID, statics[i].Z);
                }
            }

            row.Statics = names.ToString();

            // THE CHECK THAT MATTERS. A bot harvests anything within MaxRange 2 of where it
            // stands - BotHarvest.FindTarget sweeps exactly this box - so the tile underfoot being
            // grass proves nothing on its own. An arrival is only usable if SOMETHING in here
            // validates.
            int range = mine == null ? 2 : mine.MaxRange;

            for (int dx = -range; dx <= range; dx++)
            {
                for (int dy = -range; dy <= range; dy++)
                {
                    int nx = at.X + dx;
                    int ny = at.Y + dy;

                    LandTile nl = map.Tiles.GetLandTile(nx, ny);
                    bool m = mine != null && mine.Validate(nl.ID);
                    bool c = false;

                    StaticTile[] ns = map.Tiles.GetStaticTiles(nx, ny, true);

                    for (int i = 0; i < ns.Length; i++)
                    {
                        int encoded = (ns[i].ID & 0x3FFF) | 0x4000;

                        m = m || (mine != null && mine.Validate(encoded));
                        c = c || (wood != null && wood.Validate(encoded));
                    }

                    if (m)
                    {
                        row.NearMine++;
                    }

                    if (c)
                    {
                        row.NearChop++;
                    }

                    if ((m || c) && row.NearFirst == null)
                    {
                        row.NearFirst = String.Format(
                            "{0},{1} land {2} '{3}'", nx, ny, nl.ID, SafeLandName(nl.ID));
                    }
                }
            }

            return row;
        }

        private static string SafeLandName(int id)
        {
            try
            {
                string name = TileData.LandTable[id & 0x3FFF].Name;

                return String.IsNullOrWhiteSpace(name) ? "(unnamed)" : name.Trim();
            }
            catch
            {
                return "(out of range)";
            }
        }

        private static string Describe(Row r)
        {
            return String.Format(
                "  {0,-8} {1,-22} {2},{3} z{4}/{5} land {6} (0x{6:X}) '{7}' landZ {8} | "
                + "mine {9} chop {10} | within2 mine {16} chop {17} {18} | statics {11} [{12}] "
                + "| fit {13} wet {14} imp {15}",
                r.Kind,
                r.Id,
                r.X,
                r.Y,
                r.AuthoredZ,
                r.ResolvedZ,
                r.LandId,
                r.LandName,
                r.LandZ,
                r.Mineable ? "YES" : "no ",
                r.Choppable ? "YES" : "no ",
                r.StaticCount,
                r.Statics,
                r.CanFit ? "y" : "N",
                r.Wet ? "WET" : "-",
                r.Impassable ? "IMP" : "-",
                r.NearMine,
                r.NearChop,
                r.NearFirst == null ? "" : "first " + r.NearFirst);
        }

        /// <summary>
        /// The verdict, which is the point: an arrival that cannot reach anything harvestable is a
        /// site that does not work, whatever the file says.
        /// </summary>
        private static void Summarise(Mobile from, List<Row> rows)
        {
            int arrivals = 0;
            int dry = 0;
            int wet = 0;
            int unfit = 0;

            foreach (Row row in rows)
            {
                if (row.Kind == "arrival")
                {
                    arrivals++;

                    if (row.NearMine == 0 && row.NearChop == 0)
                    {
                        dry++;
                    }
                }

                if (row.Wet)
                {
                    wet++;
                }

                if (!row.CanFit)
                {
                    unfit++;
                }
            }

            Emit(from, String.Format(
                "[BotSiteAudit] {0} arrival(s): {1} with NOTHING HARVESTABLE WITHIN REACH. "
                + "{2} point(s) over water, {3} where a bot cannot stand.",
                arrivals,
                dry,
                wet,
                unfit));

            Emit(from, "[BotSiteAudit] 'mine/chop' is the tile itself; 'within2' is the 5x5 box "
                + "BotHarvest.FindTarget actually sweeps. An arrival with within2 mine 0 chop 0 is "
                + "a bot that will stand there and swing at nothing.");
        }

        private static void Write(List<Row> rows)
        {
            var json = new StringBuilder(8192);

            json.Append("{\n  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            json.Append("  \"points\": [\n");

            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];

                json.Append("    {\"kind\":").Append(Json.Quote(r.Kind));
                json.Append(",\"id\":").Append(Json.Quote(r.Id));
                json.Append(",\"x\":").Append(r.X).Append(",\"y\":").Append(r.Y);
                json.Append(",\"authoredZ\":").Append(r.AuthoredZ);
                json.Append(",\"resolvedZ\":").Append(r.ResolvedZ);
                json.Append(",\"landId\":").Append(r.LandId);
                json.Append(",\"landName\":").Append(Json.Quote(r.LandName));
                json.Append(",\"landZ\":").Append(r.LandZ);
                json.Append(",\"mineable\":").Append(r.Mineable ? "true" : "false");
                json.Append(",\"choppable\":").Append(r.Choppable ? "true" : "false");
                json.Append(",\"statics\":").Append(r.StaticCount);
                json.Append(",\"canFit\":").Append(r.CanFit ? "true" : "false");
                json.Append(",\"wet\":").Append(r.Wet ? "true" : "false");
                json.Append(",\"impassable\":").Append(r.Impassable ? "true" : "false");
                json.Append(",\"nearMine\":").Append(r.NearMine);
                json.Append(",\"nearChop\":").Append(r.NearChop);
                json.Append("}");

                if (i < rows.Count - 1)
                {
                    json.Append(",");
                }

                json.Append("\n");
            }

            json.Append("  ]\n}\n");

            string error;

            if (!AtomicFile.Write(SnapshotPath, json.ToString(), out error))
            {
                Log.Error("Could not write {0}: {1}", SnapshotPath, error);
            }
        }

        /// <summary>
        /// Lines collected for the report gump, or null when nobody is capturing.
        ///
        /// A plain static because every one of these commands runs on the game thread and none of
        /// them yields: Capture sets it, the work runs to completion, Capture clears it. Buffering
        /// here rather than threading a list through forty call sites, which is the same fix done
        /// forty times and forty chances to miss one.
        /// </summary>
        private static List<string> _capture;

        private static void Emit(Mobile from, string line)
        {
            Log.Info(line);

            if (_capture != null)
            {
                _capture.Add(line);
                return;
            }

            if (from != null)
            {
                from.SendMessage(0x40, line);
            }
        }

        /// <summary>
        /// Run a command with its output collected into one scrollable report.
        ///
        /// These audits print a line per arrival point and per waypoint - a hundred and more, of
        /// which the interesting ones are somewhere in the middle. In the journal that is a wall
        /// that has already scrolled past by the time it finishes printing.
        ///
        /// A headless run captures nothing and goes to the console, which is where the only
        /// reader is.
        /// </summary>
        private static void Capture(Mobile from, string title, Action work)
        {
            if (from == null)
            {
                work();
                return;
            }

            var lines = new List<string>();

            _capture = lines;

            try
            {
                work();
            }
            finally
            {
                _capture = null;
            }

            CommandReport.Send(from, title, lines);
        }
    }
}
