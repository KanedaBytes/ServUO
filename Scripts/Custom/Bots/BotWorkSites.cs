// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotWorkSites.cs — where the working classes work, and whether they can get there.
//
// SEAM 1's other half. BotClassHelper.StationFor answers WHAT a class works;
// this answers WHICH ONE, out of the destinations actually on the graph, and
// says so at load when the answer is "none".
//
// Upstream had no equivalent. Their gatherer took whatever the destination roll
// handed it and, if it turned out to be standing on a road, worked the road —
// which was fine, because their yield was invented rather than dug. Ours has to
// be stricter in two directions at once: the site must be REACHABLE (there is a
// route from town) and it must be REAL (there is something there the harvest
// system will accept). A site that fails either is excluded and reported, which
// is the difference between a quiet bug and a warning line.

using System;
using System.Collections.Generic;
using System.Text;

using Server.Engines.Craft;
using Server.Engines.Harvest;

namespace Server.Custom
{
    /// <summary>What a class works: a nav destination type, optionally narrowed by a tag.</summary>
    public struct BotStation
    {
        public readonly string Type;
        public readonly string Tag;

        public BotStation(string type, string tag)
        {
            Type = type;
            Tag = tag;
        }

        public bool IsNone
        {
            get { return String.IsNullOrEmpty(Type); }
        }

        public override string ToString()
        {
            return Tag == null ? Type : Type + "+" + Tag;
        }
    }

    public static class BotWorkSites
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// How many harvestable tiles an arrival point must have in reach to count as a real site.
        ///
        /// This number exists because "HarvestDefinition.Validate accepted the tile" turned out to
        /// mean almost nothing. Stock ServUO's m_MountainAndCaveTiles contains land ids that this
        /// client's tiledata names 'forest', so the first cut of the 7e sites was four arrivals on
        /// green land west of Castle Britannia with one to four mineable tiles apiece - every one
        /// of them Validate-clean, and a miner standing there swings at nothing.
        ///
        /// A real face is DENSE. The authored sites reach 11-15; the Britain wood reaches 4,
        /// because Britain is not ringed by dense forest and no cell within 500 tiles of the bank
        /// holds more than nine choppable tiles per hundred.
        /// </summary>
        public static int MinReach
        {
            get { return Config.Get("Custom.BotWorkSiteMinReach", 5); }
        }

        /// <summary>
        /// The floor for one kind of site, because rock and wood are not the same question.
        ///
        /// A mine face saturates - the authored ones reach 11 to 15 - so five is a low bar there
        /// and catches a site authored on the wrong tiles. A wood does not: Britain is not ringed
        /// by dense forest, and no cell within 500 tiles of the bank holds more than nine
        /// choppable tiles per hundred, so the Britain wood reaches 4 and is a perfectly good
        /// place to send a Lumberjack. One number for both meant either warning about a legitimate
        /// wood for ever or lowering the bar for rock until it stopped catching anything.
        ///
        /// Falls back to Custom.BotWorkSiteMinReach for any type without its own key, so a new
        /// site type inherits a sane floor rather than none.
        /// </summary>
        public static int MinReachFor(string type)
        {
            if (type != null)
            {
                if (Insensitive.Equals(type, "mine"))
                {
                    return Config.Get("Custom.BotWorkSiteMinReachMine", MinReach);
                }

                if (Insensitive.Equals(type, "lumber"))
                {
                    return Config.Get("Custom.BotWorkSiteMinReachLumber", 3);
                }
            }

            return MinReach;
        }

        /// <summary>Sites excluded at load, with the reason. Reported by Bots.Work.</summary>
        private static readonly Dictionary<string, string> _excluded =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Classes whose station does not exist on the graph. Reported by Bots.Work.</summary>
        private static readonly List<string> _stationless = new List<string>();

        /// <summary>Loads delivered since boot, and how much material moved. Reported by Bots.Work.</summary>
        public static int Deliveries { get; private set; }

        public static int Delivered { get; private set; }

        /// <summary>
        /// Units actually dug or chopped out of the ground since boot — NOT units delivered.
        ///
        /// The two diverge for exactly one reason and it matters: EquipmentTable spawns a gatherer
        /// with 3-15 of its own good as a working stash, so a bot can deliver a full load having
        /// mined nothing at all. Deliveries proves the walk; this proves the work.
        /// </summary>
        public static int Mined { get; private set; }

        public static void NoteDelivery(int amount)
        {
            Deliveries++;
            Delivered += amount;
        }

        public static void NoteMined(int amount)
        {
            if (amount > 0)
            {
                Mined += amount;
            }
        }

        public static IEnumerable<KeyValuePair<string, string>> Excluded
        {
            get { return _excluded; }
        }

        public static IList<string> Stationless
        {
            get { return _stationless; }
        }

        public static bool IsExcluded(string destinationId)
        {
            return destinationId != null && _excluded.ContainsKey(destinationId);
        }

        /// <summary>
        /// How many harvestable tiles are in reach of this spot.
        ///
        /// Deliberately the SAME 5x5 sweep BotHarvest.FindTarget performs, so the number answers
        /// the only question that matters: standing here, will the bot find something to swing at?
        /// </summary>
        public static int ReachFrom(Map map, Point3D at, HarvestDefinition definition)
        {
            if (map == null || map == Map.Internal || definition == null)
            {
                return 0;
            }

            int reach = 0;
            int range = definition.MaxRange;

            for (int dx = -range; dx <= range; dx++)
            {
                for (int dy = -range; dy <= range; dy++)
                {
                    int x = at.X + dx;
                    int y = at.Y + dy;

                    if (definition.Validate(map.Tiles.GetLandTile(x, y).ID))
                    {
                        reach++;
                        continue;
                    }

                    StaticTile[] statics = map.Tiles.GetStaticTiles(x, y, true);

                    for (int i = 0; i < statics.Length; i++)
                    {
                        if (definition.Validate((statics[i].ID & 0x3FFF) | 0x4000))
                        {
                            reach++;
                            break;
                        }
                    }
                }
            }

            return reach;
        }

        /// <summary>The harvest definition a work destination is worked with, or null.</summary>
        public static HarvestDefinition DefinitionForType(string type)
        {
            if (Insensitive.Equals(type, "mine"))
            {
                return BotHarvest.DefinitionFor(BotClass.Miner);
            }

            if (Insensitive.Equals(type, "lumber"))
            {
                return BotHarvest.DefinitionFor(BotClass.Lumberjack);
            }

            return null;
        }

        /// <summary>
        /// Work sites whose best arrival point cannot reach MinReach harvestable tiles.
        ///
        /// Folded into Nav.Data rather than kept here, because this is a fact about the NAV DATA -
        /// somebody authored a site in the editor and the ground under it is thin - and the person
        /// who needs to hear it is the one who just edited navigation.json. It is what makes
        /// hand-authoring further sites safe.
        /// </summary>
        public static List<string> ThinSites(Map map)
        {
            var thin = new List<string>();

            if (map == null || map == Map.Internal)
            {
                return thin;
            }

            foreach (NavDestination destination in Nav.Destinations(map, null, null))
            {
                HarvestDefinition definition = DefinitionForType(destination.Type);

                if (definition == null || destination.ArrivalList == null)
                {
                    continue;
                }

                int floor = MinReachFor(destination.Type);
                int best = 0;

                foreach (NavArrival arrival in destination.ArrivalList)
                {
                    best = Math.Max(best, ReachFrom(map, arrival.Location, definition));
                }

                if (best < floor)
                {
                    thin.Add(String.Format(
                        "work site '{0}' ({1}): best arrival reaches {2} harvestable tile(s), wants {3}",
                        destination.Id,
                        destination.Type,
                        best,
                        floor));
                }
            }

            thin.Sort(StringComparer.Ordinal);

            return thin;
        }

        /// <summary>Every destination type this file governs — the ones a bot goes to in order to work.</summary>
        public static bool IsWorkType(string type)
        {
            return Insensitive.Equals(type, "mine")
                || Insensitive.Equals(type, "lumber")
                || Insensitive.Equals(type, "forge");
        }

        /// <summary>The site type a gatherer class works, or none.</summary>
        public static BotStation SiteFor(BotClass cls)
        {
            if (cls == BotClass.Miner)
            {
                return new BotStation("mine", null);
            }

            if (cls == BotClass.Lumberjack)
            {
                return new BotStation("lumber", null);
            }

            return new BotStation(null, null);
        }

        /// <summary>Is this destination the station or site the bot's own class works?</summary>
        public static bool IsOwnStation(PlayerBot bot, NavDestination destination)
        {
            if (bot == null || destination == null || IsExcluded(destination.Id))
            {
                return false;
            }

            BotStation station = BotClassHelper.StationFor(bot);

            if (station.IsNone)
            {
                station = SiteFor(bot.TradeClass);
            }

            if (station.IsNone)
            {
                return false;
            }

            if (!Insensitive.Equals(destination.Type, station.Type))
            {
                return false;
            }

            return station.Tag == null || destination.HasTag(station.Tag);
        }

        /// <summary>
        /// The nearest site or station this bot's own class works, or null.
        ///
        /// Exists because a behaviour reached by HAND has no destination. Crafter and Gatherer are
        /// arrival handoffs - you become one by turning up somewhere that wants one - so the
        /// Traveler hands the destination over as it hands the brain over. [BotBehavior hands over
        /// only the brain, which left a Gatherer with a null DestinationId: ResolveSite fell
        /// through to Nav.Destination(null), found nothing, and the bot walked away again before
        /// anybody could watch it work. This is what the command uses to supply the missing half.
        ///
        /// Nearest by straight-line distance, NOT by route cost. The bot is about to ask
        /// Nav.TryRouteFrom for a real route anyway, and running the graph over every candidate to
        /// rank them would pay for a pathfind per site to choose between two.
        /// </summary>
        public static NavDestination NearestSiteFor(PlayerBot bot)
        {
            if (bot == null || bot.Map == null || bot.Map == Map.Internal)
            {
                return null;
            }

            BotStation station = BotClassHelper.StationFor(bot);

            if (station.IsNone)
            {
                station = SiteFor(bot.TradeClass);
            }

            if (station.IsNone)
            {
                return null;
            }

            NavDestination nearest = null;
            int best = 0;

            foreach (NavDestination destination in Available(bot.Map, station))
            {
                int distance = Math.Max(
                    Math.Abs(destination.X - bot.X),
                    Math.Abs(destination.Y - bot.Y));

                if (nearest == null || distance < best)
                {
                    nearest = destination;
                    best = distance;
                }
            }

            return nearest;
        }

        /// <summary>
        /// How many bots a site holds before it stops pulling.
        ///
        /// The mirror of the standing-crowd floor, and deliberately the same shape: a floor says
        /// "fewer than this and the place pulls harder", a capacity says "more than this and it
        /// stops pulling at all". Default is the destination's own arrival-point count, because
        /// that is the number of bots that can physically stand there — an uncontrolled
        /// BaseCreature cannot walk through another one (Movement.cs:411), so a fifth bot at a
        /// four-point face spends its shift shoving.
        /// </summary>
        public static int CapacityOf(NavDestination destination)
        {
            if (destination == null)
            {
                return 0;
            }

            int configured = BotLifecycle.Config_.CapacityFor(destination.Id);

            if (configured > 0)
            {
                return configured;
            }

            return destination.ArrivalList == null ? 1 : Math.Max(1, destination.ArrivalList.Count);
        }

        /// <summary>
        /// The weight multiplier for how full a site is: 1.0 when empty, 0.0 at capacity.
        ///
        /// Linear rather than a cliff, so a half-full face is half as attractive as an empty one
        /// rather than equally attractive right up to the last slot. Bots already routing there
        /// count as occupying it — the same rule the bank floor uses, and for the same reason:
        /// without it the whole shift converges on one face and arrives to find it full.
        /// </summary>
        public static double VacancyFactor(NavDestination destination)
        {
            int capacity = CapacityOf(destination);

            if (capacity <= 0)
            {
                return 1.0;
            }

            int occupancy = BotCrowds.CountFor(destination.Id);

            if (occupancy >= capacity)
            {
                return 0.0;
            }

            return (double)(capacity - occupancy) / capacity;
        }

        /// <summary>
        /// Validate every work destination on the graph, once, at load.
        ///
        /// Two deliberate failure surfaces, both of which the brief asked for and both of which
        /// are real rather than contrived:
        ///
        ///   1. A SITE WITH NO ROUTE warns and is excluded. Without this a Miner picks it every
        ///      tick, fails to route, and stands still — which from the outside is
        ///      indistinguishable from a broken walker.
        ///   2. A CLASS WITH NO STATION warns. The Fisherman produces this for free: its station
        ///      is `dock` and there is no dock destination in the graph, because the fishing half
        ///      is a later session. That is exactly the shape of the failure, so it is left to
        ///      speak for itself rather than being special-cased into silence.
        ///
        /// A third check comes free with using the real harvest system: a site whose tiles the
        /// definition does not accept is excluded too. Upstream could not have had this one —
        /// nothing about their yield depended on the ground.
        /// </summary>
        public static void Validate(Map map)
        {
            _excluded.Clear();
            _stationless.Clear();

            if (map == null || map == Map.Internal)
            {
                return;
            }

            foreach (NavDestination destination in Nav.Destinations(map, null, null))
            {
                if (!IsWorkType(destination.Type))
                {
                    continue;
                }

                string reason = ReasonToExclude(destination);

                if (reason == null)
                {
                    continue;
                }

                _excluded[destination.Id] = reason;

                Log.Warn("Work site '{0}' is excluded: {1}", destination.Id, reason);
            }

            foreach (BotClass cls in BotClassHelper.Rollable())
            {
                BotStation station = BotClassHelper.StationFor(cls);

                if (station.IsNone)
                {
                    station = SiteFor(cls);
                }

                if (station.IsNone)
                {
                    continue;
                }

                if (Available(map, station).Count > 0)
                {
                    continue;
                }

                _stationless.Add(String.Format(
                    "{0} works '{1}' and the graph has none",
                    BotClassHelper.DisplayName(cls),
                    station));

                Log.Warn(
                    "{0} has no station: nothing on this facet is a '{1}' destination. Bots of that class will never work.",
                    BotClassHelper.DisplayName(cls),
                    station);
            }

            WriteReachSnapshot(map, null, null);
        }

        /// <summary>Where the editor reads per-arrival reach from.</summary>
        public const string ReachPath = "Data/Live/site-reach.json";

        /// <summary>How many candidate tiles a zone sweep offers back. See SweepZone.</summary>
        public const int MaxCandidates = 24;

        /// <summary>Above this many tiles a zone sweep is worth warning about; see SweepZone.</summary>
        public const int SweepWarnTiles = 1600;

        /// <summary>And above this it is refused outright.</summary>
        public const int SweepMaxTiles = 4096;

        /// <summary>
        /// The best places to stand inside a proposed work zone.
        ///
        /// The Site tool used to have no proposal step at all: the author clicked tiles and the
        /// shard answered with the reach under each one, which meant authoring a site was a
        /// guessing game played one click at a time against a cliff face. Worse, the answer was
        /// drawn without its canFit, so a tile up on unstandable rock with twenty ore around it
        /// looked like the BEST tile on the map. This inverts it - the shard, which is the only
        /// thing here holding the map, proposes and the author accepts.
        ///
        /// The sweep runs on the game thread inside the request poller, so it is bounded twice:
        /// by area (a 40x40 zone is already 1600 ReachFrom calls, each a 5x5 tile sweep) and by
        /// how many it hands back. Ranked by reach, then by distance to the zone centre - a
        /// far-flung arrival is how brit-mine-north got an arrival 16 tiles from its nearest
        /// waypoint against a 12-tile hop cap, and stranded every miner sent to it.
        /// </summary>
        public static List<Point3D> SweepZone(Map map, Rectangle2D zone, string type, out string problem)
        {
            problem = null;

            var found = new List<Point3D>();

            if (map == null || map == Map.Internal)
            {
                problem = "no map";
                return found;
            }

            HarvestDefinition definition = DefinitionForType(type);

            if (definition == null)
            {
                problem = String.Format("'{0}' is not a work site type; expected mine or lumber", type);
                return found;
            }

            int area = zone.Width * zone.Height;

            if (area <= 0)
            {
                problem = "the zone is empty";
                return found;
            }

            if (area > SweepMaxTiles)
            {
                problem = String.Format(
                    "the zone is {0}x{1} = {2} tiles; the sweep is capped at {3}",
                    zone.Width, zone.Height, area, SweepMaxTiles);
                return found;
            }

            int floor = MinReachFor(type);
            double centreX = zone.X + (zone.Width / 2.0);
            double centreY = zone.Y + (zone.Height / 2.0);

            var scored = new List<SweptTile>();

            for (int x = zone.X; x < zone.X + zone.Width; x++)
            {
                for (int y = zone.Y; y < zone.Y + zone.Height; y++)
                {
                    var at = new Point3D(x, y, map.GetAverageZ(x, y));

                    if (!map.CanSpawnMobile(at.X, at.Y, at.Z))
                    {
                        continue;
                    }

                    int reach = ReachFrom(map, at, definition);

                    if (reach < floor)
                    {
                        continue;
                    }

                    double dx = x - centreX;
                    double dy = y - centreY;

                    scored.Add(new SweptTile(at, reach, (dx * dx) + (dy * dy)));
                }
            }

            scored.Sort(
                delegate(SweptTile a, SweptTile b)
                {
                    int byReach = b.Reach.CompareTo(a.Reach);

                    return byReach != 0 ? byReach : a.FromCentre.CompareTo(b.FromCentre);
                });

            for (int i = 0; i < scored.Count && i < MaxCandidates; i++)
            {
                found.Add(scored[i].At);
            }

            if (scored.Count == 0)
            {
                problem = String.Format(
                    "no tile in this zone can be stood on with {0}+ harvestable tile(s) in reach", floor);
            }
            else if (area > SweepWarnTiles)
            {
                problem = String.Format(
                    "the zone is {0}x{1} = {2} tiles, which is a lot to sweep; consider a tighter zone",
                    zone.Width, zone.Height, area);
            }

            return found;
        }

        private struct SweptTile
        {
            public readonly Point3D At;
            public readonly int Reach;
            public readonly double FromCentre;

            public SweptTile(Point3D at, int reach, double fromCentre)
            {
                At = at;
                Reach = reach;
                FromCentre = fromCentre;
            }
        }

        /// <summary>
        /// Write the reach of EVERY arrival at every work site, not just the best one.
        ///
        /// Nav.Data only reports the best, because that is the question it is asking - "is this
        /// site worth sending anyone to at all". Somebody authoring a site needs the other
        /// question: which of these tiles is actually any good. A face has thin edges by nature,
        /// and an arrival on one is not a data error, but it IS the arrival that produces a miner
        /// standing in the right place swinging at nothing.
        ///
        /// <paramref name="probe"/> carries points that are not in navigation.json yet, so the
        /// Site tool can show reach for a tile as it is being placed rather than after a save and
        /// a reload. They are answered against <paramref name="probeType"/>'s harvest definition
        /// and reported separately, because they are a question rather than data.
        /// </summary>
        public static void WriteReachSnapshot(Map map, IList<Point3D> probe, string probeType)
        {
            if (map == null || map == Map.Internal)
            {
                return;
            }

            var builder = new StringBuilder(2048);

            builder.Append("{\n");
            builder.Append("  \"utc\": \"").Append(DateTime.UtcNow.ToString("o")).Append("\",\n");
            builder.Append("  \"map\": ").Append(Json.Quote(map.Name)).Append(",\n");
            builder.Append("  \"arrivals\": [\n");

            bool first = true;

            foreach (NavDestination destination in Nav.Destinations(map, null, null))
            {
                HarvestDefinition definition = DefinitionForType(destination.Type);

                if (definition == null || destination.ArrivalList == null)
                {
                    continue;
                }

                int floor = MinReachFor(destination.Type);

                for (int i = 0; i < destination.ArrivalList.Count; i++)
                {
                    NavArrival arrival = destination.ArrivalList[i];

                    if (!first)
                    {
                        builder.Append(",\n");
                    }

                    first = false;

                    builder.Append("    {");
                    builder.Append("\"destination\":").Append(Json.Quote(destination.Id));
                    builder.Append(",\"type\":").Append(Json.Quote(destination.Type));
                    builder.Append(",\"index\":").Append(i);
                    builder.Append(",\"x\":").Append(arrival.X);
                    builder.Append(",\"y\":").Append(arrival.Y);
                    builder.Append(",\"z\":").Append(arrival.Z);
                    builder.Append(",\"reach\":").Append(ReachFrom(map, arrival.Location, definition));
                    builder.Append(",\"min\":").Append(floor);
                    builder.Append("}");
                }
            }

            builder.Append(first ? "\n" : "\n");
            builder.Append("  ],\n  \"probe\": [\n");

            HarvestDefinition probeDefinition = DefinitionForType(probeType);

            if (probe != null && probeDefinition != null)
            {
                for (int i = 0; i < probe.Count; i++)
                {
                    Point3D point = probe[i];

                    // Resolve Z the way the walker would, so the answer is for the tile a bot
                    // would actually end up standing on rather than the one the editor guessed.
                    var at = new Point3D(point.X, point.Y, map.GetAverageZ(point.X, point.Y));

                    builder.Append(i > 0 ? ",\n" : "");
                    builder.Append("    {");
                    builder.Append("\"type\":").Append(Json.Quote(probeType));
                    builder.Append(",\"x\":").Append(at.X);
                    builder.Append(",\"y\":").Append(at.Y);
                    builder.Append(",\"z\":").Append(at.Z);
                    builder.Append(",\"reach\":").Append(ReachFrom(map, at, probeDefinition));
                    builder.Append(",\"min\":").Append(MinReachFor(probeType));
                    builder.Append(",\"canFit\":").Append(map.CanSpawnMobile(at.X, at.Y, at.Z) ? "true" : "false");
                    builder.Append("}");
                }

                builder.Append("\n");
            }

            builder.Append("  ]\n}\n");

            string error;

            if (!AtomicFile.Write(ReachPath, builder.ToString(), out error))
            {
                Log.Error("Site reach snapshot not written: {0}", error);
            }
        }

        private static string ReasonToExclude(NavDestination destination)
        {
            // Reachability first: it is the cheaper check and the likelier fault.
            NavRoute route;
            string error;

            if (!Nav.TryRoute("brit-gate-west", destination.Id, out route, out error))
            {
                return "no route from the town gate (" + error + ")";
            }

            // Then whether there is anything there to work. A forge is validated against the
            // engine's own fixture check instead — see ForgeIsWorkable.
            if (Insensitive.Equals(destination.Type, "forge"))
            {
                return ForgeIsWorkable(destination) ? null : "no arrival point stands within two tiles of both a forge and an anvil";
            }

            HarvestDefinition definition = Insensitive.Equals(destination.Type, "mine")
                ? BotHarvest.DefinitionFor(BotClass.Miner)
                : BotHarvest.DefinitionFor(BotClass.Lumberjack);

            if (definition == null)
            {
                return "no harvest definition for type '" + destination.Type + "'";
            }

            if (destination.ArrivalList == null || destination.ArrivalList.Count == 0)
            {
                return "no arrival points authored";
            }

            int best = 0;

            foreach (NavArrival arrival in destination.ArrivalList)
            {
                best = Math.Max(best, ReachFrom(destination.Map, arrival.Location, definition));
            }

            // EXCLUSION is for a site that yields NOTHING; a thin one is a warning, not a
            // disqualification - Nav.Data reports it through ThinSites and the shard still works
            // it. Excluding at MinReach would have thrown away the Britain wood, which is thin
            // because Britain is thin, not because it is wrong.
            return best > 0
                ? null
                : "no arrival point has a harvestable tile within " + definition.MaxRange + " tiles";
        }

        /// <summary>
        /// Does any arrival point at this forge stand within two tiles of both fixtures?
        ///
        /// DefBlacksmithy.CheckAnvilAndForge is the authority, but it takes a Mobile — it needs
        /// one to measure Z from and to test InLOS against — and there is no bot standing at the
        /// forge when the graph is validated. NavAudit already faced the same choice and answered
        /// it the same way: it re-walks a returned path rather than spawning a probe creature
        /// "for the sake of a report" (NavAudit.cs:330-334). Placing a mobile in the world to
        /// answer a config question is worse than approximating one check.
        ///
        /// So this mirrors CheckAnvilAndForge's item and static sweeps and its Z window, and
        /// deliberately drops only the line-of-sight test. That makes it OPTIMISTIC: a forge
        /// behind a wall passes here and fails at the anvil. The bot finds out the moment it
        /// tries — CanCraft returns 1044267 and CrafterBehavior reports it through Bots.Work —
        /// so the two together cover what neither covers alone. The pessimistic half, a fixture
        /// that is simply not there, is the one that actually happens when a coordinate is wrong,
        /// and that half is exact.
        /// </summary>
        private static bool ForgeIsWorkable(NavDestination destination)
        {
            if (destination.ArrivalList == null || destination.ArrivalList.Count == 0)
            {
                return false;
            }

            foreach (NavArrival arrival in destination.ArrivalList)
            {
                if (HasAnvilAndForge(destination.Map, arrival.Location, 2))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAnvilId(int id)
        {
            return id == 4015 || id == 4016 || id == 0x2DD5 || id == 0x2DD6;
        }

        private static bool IsForgeId(int id)
        {
            return id == 4017 || (id >= 6522 && id <= 6569) || id == 0x2DD8 || id == 0xA531 || id == 0xA535;
        }

        private static bool HasAnvilAndForge(Map map, Point3D at, int range)
        {
            if (map == null || map == Map.Internal)
            {
                return false;
            }

            bool anvil = false;
            bool forge = false;

            IPooledEnumerable eable = map.GetItemsInRange(at, range);

            try
            {
                foreach (Item item in eable)
                {
                    if ((at.Z + 16) < item.Z || (item.Z + 16) < at.Z)
                    {
                        continue;
                    }

                    anvil = anvil || IsAnvilId(item.ItemID);
                    forge = forge || IsForgeId(item.ItemID);

                    if (anvil && forge)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                eable.Free();
            }

            for (int x = -range; x <= range; x++)
            {
                for (int y = -range; y <= range; y++)
                {
                    StaticTile[] tiles = map.Tiles.GetStaticTiles(at.X + x, at.Y + y, true);

                    for (int i = 0; i < tiles.Length; i++)
                    {
                        if ((at.Z + 16) < tiles[i].Z || (tiles[i].Z + 16) < at.Z)
                        {
                            continue;
                        }

                        anvil = anvil || IsAnvilId(tiles[i].ID);
                        forge = forge || IsForgeId(tiles[i].ID);

                        if (anvil && forge)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>Every usable destination of a station's shape, excluding the ones that failed validation.</summary>
        public static List<NavDestination> Available(Map map, BotStation station)
        {
            var usable = new List<NavDestination>();

            if (map == null || map == Map.Internal || station.IsNone)
            {
                return usable;
            }

            foreach (NavDestination destination in Nav.Destinations(map, station.Type, station.Tag))
            {
                if (!IsExcluded(destination.Id))
                {
                    usable.Add(destination);
                }
            }

            return usable;
        }

        /// <summary>
        /// A census line for Bots.Work: each work destination, who is on it, and out of how many.
        /// </summary>
        public static List<string> Census(Map map)
        {
            var lines = new List<string>();

            if (map == null || map == Map.Internal)
            {
                return lines;
            }

            foreach (NavDestination destination in Nav.Destinations(map, null, null))
            {
                if (!IsWorkType(destination.Type))
                {
                    continue;
                }

                if (IsExcluded(destination.Id))
                {
                    lines.Add(String.Format("{0} EXCLUDED", destination.Id));
                    continue;
                }

                lines.Add(String.Format(
                    "{0} {1}/{2}",
                    destination.Id,
                    BotCrowds.CountFor(destination.Id),
                    CapacityOf(destination)));
            }

            lines.Sort(StringComparer.Ordinal);

            return lines;
        }
    }
}
