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

            int floor = MinReach;

            foreach (NavDestination destination in Nav.Destinations(map, null, null))
            {
                HarvestDefinition definition = DefinitionForType(destination.Type);

                if (definition == null || destination.ArrivalList == null)
                {
                    continue;
                }

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

            BotStation station = BotClassHelper.StationFor(bot.Class);

            if (station.IsNone)
            {
                station = SiteFor(bot.Class);
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
