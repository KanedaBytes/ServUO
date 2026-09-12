// NavHopProbe.cs - the synthetic hop set, and the proof that NavStockCopy is the same algorithm.
//
// WHAT IT ANSWERS
// ---------------
// Three questions the walk audit structurally cannot, because a sweep walks the edges that exist:
//
//   1. HOW FAR CAN THE STOCK PATHFINDER ACTUALLY SEE. The Navigation README records that a hop is
//      "unreliable past ~9 tiles uphill" and that Custom.NavHopMaxTiles is 12 because of the
//      38-tile box. Neither claim has ever been measured across a controlled set of lengths. This
//      builds one - real waypoint pairs at 9, 12, 16, 20 and 24 tiles, classified flat, climbing
//      and door-crossing - and reports success rate, route length and NODE EXPANSIONS at each.
//
//   2. WHAT THE ONE TOKEN AT FastAStarAlgorithm.cs:118 IS WORTH. Every hop is planned three times:
//      by stock, by the copy with the dead-end `break`, and by the copy with `continue`. The three
//      answers sit in one row so the difference is a subtraction rather than an argument.
//
//   3. WHETHER THE COPY IS THE SAME ALGORITHM AT ALL. `validate` walks a corpus and asserts that
//      stock and the copy-with-break return BYTE-IDENTICAL direction arrays. Every expansion figure
//      this instrument produces is worthless without that, so it is a first-class mode rather than
//      a test buried somewhere.
//
// WHY IT CALLS THE ALGORITHMS DIRECTLY RATHER THAN THROUGH MovementPath
// ---------------------------------------------------------------------
// MovementPath returns before choosing an algorithm on three paths (map null or Internal at :31,
// InRange(start, goal, 1) at :34, CheckCondition at :48), and it consults
// MovementPath.OverrideAlgorithm at :39 - so asking it would mean measuring stock only while the
// instrument itself is uninstalled, and measuring "no route" for a one-tile hop that the
// pathfinder was never asked about. Calling FastAStarAlgorithm.Instance and NavStockCopy directly
// gives the algorithm's own answer whatever mode the shard is in, which is the whole point.
//
// The MoveImpl door dance is INSIDE both of them (FastAStarAlgorithm.cs:97-107 and the copy's
// mirror of it), so MODIFICATIONS entry 6 applies to the probe exactly as it does to a live bot.
//
// THE PROBE IS A REAL WALKER OF A REAL CLASS
// -------------------------------------------
// It takes the classes out of NavWalkAudit's registry rather than declaring its own, so `bot`
// means the same PlayerBot-derived probe the walk audit uses and `creature` means the same
// BaseCreature - and Core still names no bot class. The mobile matters: it is the IPoint3D the
// algorithm casts, so it decides both the BaseCreature branch and BotPathPolicy's answer.
//
// It moves nothing. The probe is placed on the start tile, the searches are planned, and it is
// deleted in a finally. No ledger, no strikes, no steps.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Server.Commands;
using Server.Items;
using Server.PathAlgorithms.FastAStar;

namespace Server.Custom
{
    /// <summary>One planned hop, told three ways.</summary>
    public sealed class NavHopProbeRow
    {
        public string ProbeClass;
        public string Terrain;

        public Point3D Start;
        public Point3D Goal;
        public string FromId;
        public string ToId;

        /// <summary>Chebyshev separation - the same measure NavHopMaxTiles is expressed in.</summary>
        public int Tiles;

        public int Climb;

        public bool StockOk;
        public int StockTiles;
        public double StockMs;

        public bool BreakOk;
        public int BreakTiles;
        public int BreakExpansions;
        public int BreakProbes;
        public double BreakMs;
        public string BreakOutcome;

        public bool ContinueOk;
        public int ContinueTiles;
        public int ContinueExpansions;
        public int ContinueProbes;
        public double ContinueMs;
        public string ContinueOutcome;

        /// <summary>True when stock and the copy-with-break disagree at all. Must never be true.</summary>
        public bool Divergent;

        public string Divergence;
    }

    public static class NavHopProbe
    {
        private static readonly CustomLogger Log = CustomLogger.For("Nav");

        public const string SnapshotPath = "Data/Live/nav-hop-probe.json";

        /// <summary>The lengths the README's claims are about, plus the two the cap question needs.</summary>
        private static readonly int[] DefaultLengths = { 9, 12, 16, 20, 24 };

        /// <summary>Climb at or above this many Z counts the hop as climbing rather than flat.</summary>
        private const int ClimbThreshold = 5;

        private static string _lastSummary = "never run";
        private static readonly List<string> _lastReport = new List<string>();

        public static void Initialize()
        {
            CommandSystem.Register("NavHopProbe", AccessLevel.Administrator, OnCommand);
        }

        [Usage("NavHopProbe gen [count] [budget <n>] [class] | NavHopProbe validate [count] [class] | NavHopProbe <x,y,z> <x,y,z> ... [class]")]
        [Description(
            "Plan hops three ways - stock, the stock copy, and the copy with FastAStarAlgorithm's "
            + "dead-end break changed to continue - and report success, route length and node "
            + "expansions for each. 'gen' builds a controlled set from real waypoint pairs at 9, "
            + "12, 16, 20 and 24 tiles across flat, climbing and door-crossing terrain. 'validate' "
            + "asserts the copy returns byte-identical routes to stock.")]
        private static void OnCommand(CommandEventArgs e)
        {
            string summary;
            IList<string> report;

            if (!TryRun(Join(e.Arguments), out summary, out report))
            {
                e.Mobile.SendMessage(0x22, summary);
                return;
            }

            e.Mobile.SendMessage(0x40, summary);

            foreach (string line in report)
            {
                e.Mobile.SendMessage(line);
            }
        }

        private static string Join(string[] args)
        {
            return args == null || args.Length == 0 ? "" : String.Join(" ", args);
        }

        /// <summary>
        /// The whole instrument, in one synchronous pass. Synchronous because planning a route
        /// moves nothing and takes microseconds - unlike [WalkAudit, which has to wait for walkers -
        /// so there is no job, no timer and no partial snapshot to reason about.
        /// </summary>
        public static bool TryRun(string body, out string summary, out IList<string> report)
        {
            var lines = new List<string>();
            report = lines;
            summary = null;

            if (World.Loading || World.Saving)
            {
                summary = "the world is loading or saving";
                return false;
            }

            string mode = "gen";
            string probeKey = null;
            int count = 0;
            int budget = 0;
            bool wantBudget = false;
            var explicitPoints = new List<Point3D>();
            Map facet = Map.Trammel;

            foreach (string word in (body ?? "").Split(
                new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.StartsWith("#"))
                {
                    continue;
                }

                if (word.IndexOf(',') >= 0)
                {
                    Point3D point;

                    if (!TryParsePoint(word, out point))
                    {
                        summary = "could not read the point '" + word + "'";
                        return false;
                    }

                    explicitPoints.Add(point);
                    continue;
                }

                int parsed;

                if (Int32.TryParse(word, out parsed))
                {
                    // The number after `budget` is the budget; any other number is the count.
                    if (wantBudget)
                    {
                        budget = parsed;
                        wantBudget = false;
                    }
                    else
                    {
                        count = parsed;
                    }

                    continue;
                }

                if (Insensitive.Equals(word, "budget"))
                {
                    wantBudget = true;
                    continue;
                }

                if (Insensitive.Equals(word, "gen") || Insensitive.Equals(word, "validate"))
                {
                    mode = word.ToLowerInvariant();
                    continue;
                }

                Map named;

                if (JsonConfig.TryParseMap(word, out named))
                {
                    facet = named;
                    continue;
                }

                probeKey = word;
            }

            if (explicitPoints.Count > 0)
            {
                mode = "pairs";

                if ((explicitPoints.Count % 2) != 0)
                {
                    summary = "explicit points come in pairs; got " + explicitPoints.Count;
                    return false;
                }
            }

            var classes = new List<NavWalkAudit.ProbeClass>();

            foreach (NavWalkAudit.ProbeClass cls in NavWalkAudit.ProbeClasses)
            {
                if (probeKey == null || Insensitive.Equals(cls.Key, probeKey))
                {
                    classes.Add(cls);
                }
            }

            if (classes.Count == 0)
            {
                summary = "no such probe class '" + probeKey + "'; known: "
                    + NavWalkAudit.ProbeKeyList();
                return false;
            }

            List<Hop> work;

            switch (mode)
            {
                case "pairs":
                    work = new List<Hop>();

                    for (int i = 0; i + 1 < explicitPoints.Count; i += 2)
                    {
                        work.Add(new Hop
                        {
                            Start = explicitPoints[i],
                            Goal = explicitPoints[i + 1],
                            Map = facet,
                            FromId = "(point)",
                            ToId = "(point)"
                        });
                    }

                    break;

                case "validate":
                    work = BuildValidationSet(facet, count <= 0 ? 500 : count);
                    break;

                default:
                    work = BuildGeneratedSet(facet, count <= 0 ? 20 : count);
                    break;
            }

            if (work.Count == 0)
            {
                summary = "no hops to plan - is the graph loaded for " + facet + "?";
                return false;
            }

            // Both copies run at the same budget, always: the whole point of the pair is that ONE
            // token differs between them, so a run where they also differed in budget would answer
            // nothing. FastAStarAlgorithm's own 300 is the default.
            _breakCopy.MaxExpansions = budget > 0 ? budget : 300;
            _continueCopy.MaxExpansions = _breakCopy.MaxExpansions;

            var rows = new List<NavHopProbeRow>(work.Count * classes.Count);
            long startedTick = Core.TickCount;

            foreach (NavWalkAudit.ProbeClass cls in classes)
            {
                NavWalkAudit.IWalkAuditProbe probe = null;

                try
                {
                    probe = cls.Create();

                    foreach (Hop hop in work)
                    {
                        rows.Add(Plan(cls, probe, hop));
                    }
                }
                finally
                {
                    if (probe != null && probe.Mobile != null && !probe.Mobile.Deleted)
                    {
                        probe.Mobile.Delete();
                    }
                }
            }

            double seconds = (Core.TickCount - startedTick) / 1000.0;

            Report(mode, facet, rows, seconds, out summary, lines);

            _lastSummary = summary;
            _lastReport.Clear();
            _lastReport.AddRange(lines);

            WriteSnapshot(mode, facet, rows, seconds, summary);

            return true;
        }

        // ---- planning ----------------------------------------------------------------------------

        private sealed class Hop
        {
            public Point3D Start;
            public Point3D Goal;
            public Map Map;
            public string FromId;
            public string ToId;
            public string Terrain;
        }

        private static NavHopProbeRow Plan(
            NavWalkAudit.ProbeClass cls, NavWalkAudit.IWalkAuditProbe probe, Hop hop)
        {
            var row = new NavHopProbeRow
            {
                ProbeClass = cls.Key,
                Start = hop.Start,
                Goal = hop.Goal,
                FromId = hop.FromId,
                ToId = hop.ToId,
                Tiles = Chebyshev(hop.Start, hop.Goal),
                Climb = hop.Goal.Z - hop.Start.Z,
                Terrain = hop.Terrain ?? Classify(hop.Map, hop.Start, hop.Goal)
            };

            Mobile mobile = probe.Mobile;

            // The subject has to STAND at the start tile: Movement.CheckMovement asks the mobile
            // about the tile it is leaving, and a probe left in the void answers differently from
            // one on the road. MoveToWorld rather than a walk - this measures planning, not moving.
            mobile.MoveToWorld(hop.Start, hop.Map);

            // Stock, called directly. See the header for why not through MovementPath.
            var stockWatch = System.Diagnostics.Stopwatch.StartNew();
            Direction[] stock = null;

            if (FastAStarAlgorithm.Instance.CheckCondition(mobile, hop.Map, hop.Start, hop.Goal))
            {
                stock = FastAStarAlgorithm.Instance.Find(mobile, hop.Map, hop.Start, hop.Goal);
            }

            row.StockMs = stockWatch.Elapsed.TotalMilliseconds;
            row.StockOk = stock != null && stock.Length > 0;
            row.StockTiles = stock == null ? 0 : stock.Length;

            NavSearchResult broke = _breakCopy.Find(mobile, hop.Map, hop.Start, hop.Goal);

            row.BreakOk = broke.Success;
            row.BreakTiles = broke.Length;
            row.BreakExpansions = broke.Expansions;
            row.BreakProbes = broke.SuccessorProbes;
            row.BreakMs = broke.Milliseconds;
            row.BreakOutcome = broke.Outcome.ToString();

            NavSearchResult went = _continueCopy.Find(mobile, hop.Map, hop.Start, hop.Goal);

            row.ContinueOk = went.Success;
            row.ContinueTiles = went.Length;
            row.ContinueExpansions = went.Expansions;
            row.ContinueProbes = went.SuccessorProbes;
            row.ContinueMs = went.Milliseconds;
            row.ContinueOutcome = went.Outcome.ToString();

            // Only meaningful at 300: above it the copy is deliberately a different search from
            // stock, and reporting that as a divergence would cry wolf on every raised-budget run.
            row.Divergence = _breakCopy.MaxExpansions == 300 ? Compare(stock, broke.Directions) : null;
            row.Divergent = row.Divergence != null;

            return row;
        }

        /// <summary>
        /// One instance each, held for the life of the process. The scratch arrays are ~320 KB
        /// apiece (ModernUO's own reason for holding rather than allocating,
        /// BitmapAStarAlgorithm.cs:55-58), and two instances are exactly why NavStockCopy's state
        /// is per-instance rather than static the way upstream's is.
        /// </summary>
        private static readonly NavStockCopy _breakCopy = NavPathfinder.NewCopy(false);

        private static readonly NavStockCopy _continueCopy = NavPathfinder.NewCopy(true);

        /// <summary>Null when the two agree byte for byte. A sentence naming the first difference otherwise.</summary>
        private static string Compare(Direction[] stock, Direction[] copy)
        {
            bool stockNull = stock == null;
            bool copyNull = copy == null;

            if (stockNull != copyNull)
            {
                return stockNull ? "stock returned null, the copy did not" : "the copy returned null, stock did not";
            }

            if (stockNull)
            {
                return null;
            }

            if (stock.Length != copy.Length)
            {
                return String.Format(
                    CultureInfo.InvariantCulture,
                    "stock returned {0} step(s), the copy {1}", stock.Length, copy.Length);
            }

            for (int i = 0; i < stock.Length; i++)
            {
                if (stock[i] != copy[i])
                {
                    return String.Format(
                        CultureInfo.InvariantCulture,
                        "step {0} differs: stock {1}, copy {2}", i, stock[i], copy[i]);
                }
            }

            return null;
        }

        // ---- work lists --------------------------------------------------------------------------

        /// <summary>
        /// A controlled set: for each length in DefaultLengths and each terrain class, up to
        /// `perCell` real waypoint pairs at exactly that Chebyshev separation.
        ///
        /// REAL PAIRS RATHER THAN SYNTHESISED COORDINATES, because a random point on the map is
        /// usually somewhere no walker would ever stand, and a success rate over tiles nobody
        /// walks answers nothing. Every start and goal here is an authored waypoint, which is
        /// where a bot actually stands.
        /// </summary>
        private static List<Hop> BuildGeneratedSet(Map facet, int perCell)
        {
            var hops = new List<Hop>();
            NavigationStore store = NavigationSystem.Store;

            if (store == null || store.Waypoints == null)
            {
                return hops;
            }

            var points = new List<NavWaypoint>();

            foreach (NavWaypoint waypoint in store.Waypoints)
            {
                if (waypoint.Map == facet)
                {
                    points.Add(waypoint);
                }
            }

            // Deterministic order, so two runs of the same command build the same corpus and the
            // second is a regression test on the first rather than a fresh sample.
            points.Sort((a, b) => String.CompareOrdinal(a.Id, b.Id));

            foreach (int length in DefaultLengths)
            {
                var taken = new Dictionary<string, int>(StringComparer.Ordinal);

                for (int i = 0; i < points.Count; i++)
                {
                    for (int j = 0; j < points.Count; j++)
                    {
                        if (i == j)
                        {
                            continue;
                        }

                        if (Chebyshev(points[i].Location, points[j].Location) != length)
                        {
                            continue;
                        }

                        string terrain = Classify(facet, points[i].Location, points[j].Location);

                        int already;
                        taken.TryGetValue(terrain, out already);

                        if (already >= perCell)
                        {
                            continue;
                        }

                        taken[terrain] = already + 1;

                        hops.Add(new Hop
                        {
                            Start = points[i].Location,
                            Goal = points[j].Location,
                            Map = facet,
                            FromId = points[i].Id,
                            ToId = points[j].Id,
                            Terrain = terrain
                        });
                    }
                }
            }

            return hops;
        }

        /// <summary>
        /// The corpus the copy is validated against: every authored edge, both ways, capped.
        /// Edges rather than generated pairs, because these are the hops the fleet actually plans
        /// and a copy that agreed everywhere except on real roads would be no use at all.
        /// </summary>
        private static List<Hop> BuildValidationSet(Map facet, int cap)
        {
            var hops = new List<Hop>();
            NavigationStore store = NavigationSystem.Store;

            if (store == null || store.Edges == null || store.Waypoints == null)
            {
                return hops;
            }

            var index = new Dictionary<string, NavWaypoint>(StringComparer.OrdinalIgnoreCase);

            foreach (NavWaypoint waypoint in store.Waypoints)
            {
                if (waypoint.Id != null)
                {
                    index[waypoint.Id] = waypoint;
                }
            }

            foreach (NavEdge edge in store.Edges)
            {
                NavWaypoint from, to;

                if (!index.TryGetValue(edge.From ?? "", out from)
                    || !index.TryGetValue(edge.To ?? "", out to))
                {
                    continue;
                }

                if (from.Map != facet || to.Map != facet)
                {
                    continue;
                }

                hops.Add(new Hop
                {
                    Start = from.Location, Goal = to.Location, Map = facet,
                    FromId = from.Id, ToId = to.Id
                });

                hops.Add(new Hop
                {
                    Start = to.Location, Goal = from.Location, Map = facet,
                    FromId = to.Id, ToId = from.Id
                });

                if (hops.Count >= cap)
                {
                    break;
                }
            }

            return hops;
        }

        // ---- classification ------------------------------------------------------------------------

        private static int Chebyshev(Point3D a, Point3D b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);

            return dx > dy ? dx : dy;
        }

        /// <summary>
        /// door &gt; climbing &gt; flat, in that order, because a door decides the answer whatever the
        /// slope: MODIFICATIONS entry 6 is about doors and a hop that crosses one is testing that
        /// gate whether or not it also climbs.
        /// </summary>
        private static string Classify(Map map, Point3D start, Point3D goal)
        {
            if (CrossesDoor(map, start, goal))
            {
                return "door";
            }

            return Math.Abs(goal.Z - start.Z) >= ClimbThreshold ? "climb" : "flat";
        }

        private static bool CrossesDoor(Map map, Point3D start, Point3D goal)
        {
            if (map == null || map == Map.Internal)
            {
                return false;
            }

            int x = Math.Min(start.X, goal.X);
            int y = Math.Min(start.Y, goal.Y);
            int w = Math.Abs(goal.X - start.X) + 1;
            int h = Math.Abs(goal.Y - start.Y) + 1;

            // The whole bounding box rather than the line: a route round a building leaves the
            // line at once, and the question is whether a door is in play for this hop at all.
            IPooledEnumerable<Item> items = map.GetItemsInBounds(new Rectangle2D(x, y, w, h));

            try
            {
                foreach (Item item in items)
                {
                    if (item is BaseDoor)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                items.Free();
            }

            return false;
        }

        // ---- reporting -------------------------------------------------------------------------

        private static void Report(
            string mode, Map facet, List<NavHopProbeRow> rows, double seconds,
            out string summary, List<string> lines)
        {
            int divergent = 0;

            foreach (NavHopProbeRow row in rows)
            {
                if (row.Divergent)
                {
                    divergent++;
                }
            }

            summary = String.Format(
                CultureInfo.InvariantCulture,
                "Hop probe '{0}' on {1} at budget {2}: {3} row(s) in {4:F2}s. Copy divergence: {5}.",
                mode, facet, _breakCopy.MaxExpansions, rows.Count, seconds,
                divergent == 0
                    ? (_breakCopy.MaxExpansions == 300
                        ? "NONE - the copy is stock"
                        : "not comparable at a raised budget - stock runs 300")
                    : divergent + " ROW(S) - DO NOT QUOTE THE EXPANSION FIGURES");

            lines.Add(summary);

            if (divergent > 0)
            {
                lines.Add("The copy is not the algorithm it claims to be. First few:");

                int shown = 0;

                foreach (NavHopProbeRow row in rows)
                {
                    if (!row.Divergent || shown++ >= 5)
                    {
                        continue;
                    }

                    lines.Add(String.Format(
                        "  {0} {1} -> {2}: {3}", row.ProbeClass, row.FromId, row.ToId, row.Divergence));
                }
            }

            // The table the whole instrument exists to print: per class, per length, per terrain.
            var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (NavHopProbeRow row in rows)
            {
                string key = String.Format("{0}|{1:D2}|{2}", row.ProbeClass, row.Tiles, row.Terrain);

                Bucket bucket;

                if (!buckets.TryGetValue(key, out bucket))
                {
                    bucket = new Bucket { Key = key };
                    buckets[key] = bucket;
                    order.Add(key);
                }

                bucket.Add(row);
            }

            order.Sort(StringComparer.Ordinal);

            lines.Add("class / tiles / terrain : n | stock ok | break ok | continue ok | exp break | exp cont | recovered");

            foreach (string key in order)
            {
                lines.Add(buckets[key].Describe());
            }
        }

        private sealed class Bucket
        {
            public string Key;

            private int _n;
            private int _stock;
            private int _broke;
            private int _went;
            private long _breakExp;
            private long _contExp;
            private int _recovered;

            public void Add(NavHopProbeRow row)
            {
                _n++;

                if (row.StockOk) { _stock++; }
                if (row.BreakOk) { _broke++; }
                if (row.ContinueOk) { _went++; }

                _breakExp += row.BreakExpansions;
                _contExp += row.ContinueExpansions;

                // The number the memo turns on: hops the dead-end break loses and continue finds.
                if (!row.BreakOk && row.ContinueOk)
                {
                    _recovered++;
                }
            }

            public string Describe()
            {
                string[] parts = Key.Split('|');

                return String.Format(
                    CultureInfo.InvariantCulture,
                    "{0,-9} {1,2}t {2,-5} : {3,4} | {4,4} | {5,4} | {6,4} | {7,7:F0} | {8,7:F0} | {9,4}",
                    parts[0], Int32.Parse(parts[1]), parts[2],
                    _n, _stock, _broke, _went,
                    _n == 0 ? 0.0 : _breakExp / (double)_n,
                    _n == 0 ? 0.0 : _contExp / (double)_n,
                    _recovered);
            }
        }

        private static void WriteSnapshot(
            string mode, Map facet, List<NavHopProbeRow> rows, double seconds, string summary)
        {
            var builder = new StringBuilder(4096 + rows.Count * 320);

            int divergent = 0, recovered = 0;

            foreach (NavHopProbeRow row in rows)
            {
                if (row.Divergent) { divergent++; }
                if (!row.BreakOk && row.ContinueOk) { recovered++; }
            }

            builder.Append("{\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"mode\": ").Append(Json.Quote(mode)).Append(",\n");
            builder.Append("  \"map\": ").Append(Json.Quote(facet == null ? "" : facet.Name)).Append(",\n");
            builder.Append("  \"seconds\": ").Append(Fixed(seconds)).Append(",\n");
            builder.Append("  \"rows\": ").Append(rows.Count).Append(",\n");
            builder.Append("  \"divergent\": ").Append(divergent).Append(",\n");
            builder.Append("  \"recoveredByContinue\": ").Append(recovered).Append(",\n");
            builder.Append("  \"summary\": ").Append(Json.Quote(summary)).Append(",\n");
            builder.Append("  \"hops\":[");

            for (int i = 0; i < rows.Count; i++)
            {
                NavHopProbeRow row = rows[i];

                builder.Append(i == 0 ? "\n    " : ",\n    ");
                builder.Append("{\"probeClass\":").Append(Json.Quote(row.ProbeClass));
                builder.Append(",\"from\":").Append(Json.Quote(row.FromId));
                builder.Append(",\"to\":").Append(Json.Quote(row.ToId));
                builder.Append(",\"terrain\":").Append(Json.Quote(row.Terrain));
                builder.Append(",\"tiles\":").Append(row.Tiles);
                builder.Append(",\"climb\":").Append(row.Climb);
                builder.Append(",\"startX\":").Append(row.Start.X);
                builder.Append(",\"startY\":").Append(row.Start.Y);
                builder.Append(",\"startZ\":").Append(row.Start.Z);
                builder.Append(",\"x\":").Append(row.Goal.X);
                builder.Append(",\"y\":").Append(row.Goal.Y);
                builder.Append(",\"z\":").Append(row.Goal.Z);
                builder.Append(",\"stockOk\":").Append(row.StockOk ? "true" : "false");
                builder.Append(",\"stockTiles\":").Append(row.StockTiles);
                builder.Append(",\"stockMs\":").Append(Fixed3(row.StockMs));
                builder.Append(",\"breakOk\":").Append(row.BreakOk ? "true" : "false");
                builder.Append(",\"breakTiles\":").Append(row.BreakTiles);
                builder.Append(",\"breakExpansions\":").Append(row.BreakExpansions);
                builder.Append(",\"breakProbes\":").Append(row.BreakProbes);
                builder.Append(",\"breakMs\":").Append(Fixed3(row.BreakMs));
                builder.Append(",\"breakOutcome\":").Append(Json.Quote(row.BreakOutcome));
                builder.Append(",\"continueOk\":").Append(row.ContinueOk ? "true" : "false");
                builder.Append(",\"continueTiles\":").Append(row.ContinueTiles);
                builder.Append(",\"continueExpansions\":").Append(row.ContinueExpansions);
                builder.Append(",\"continueProbes\":").Append(row.ContinueProbes);
                builder.Append(",\"continueMs\":").Append(Fixed3(row.ContinueMs));
                builder.Append(",\"continueOutcome\":").Append(Json.Quote(row.ContinueOutcome));
                builder.Append(",\"divergent\":").Append(row.Divergent ? "true" : "false");

                if (row.Divergence != null)
                {
                    builder.Append(",\"divergence\":").Append(Json.Quote(row.Divergence));
                }

                builder.Append("}");
            }

            builder.Append(rows.Count == 0 ? "" : "\n  ").Append("]\n}\n");

            string error;

            if (!AtomicFile.Write(SnapshotPath, builder.ToString(), out error))
            {
                Log.Error("Could not write {0}: {1}", SnapshotPath, error);
            }
        }

        private static string Fixed(double value)
        {
            return value.ToString("F2", CultureInfo.InvariantCulture);
        }

        private static string Fixed3(double value)
        {
            return value.ToString("F3", CultureInfo.InvariantCulture);
        }

        private static bool TryParsePoint(string field, out Point3D point)
        {
            point = Point3D.Zero;

            string[] parts = field.Split(',');

            if (parts.Length < 2)
            {
                return false;
            }

            int x, y, z = 0;

            if (!Int32.TryParse(parts[0], out x) || !Int32.TryParse(parts[1], out y))
            {
                return false;
            }

            if (parts.Length >= 3 && !Int32.TryParse(parts[2], out z))
            {
                return false;
            }

            point = new Point3D(x, y, z);
            return true;
        }

        /// <summary>The last run's summary and report, for the token path, without a Mobile.</summary>
        public static void DescribeLast(out string summary, out IList<string> report)
        {
            summary = _lastSummary;
            report = _lastReport;
        }
    }
}
