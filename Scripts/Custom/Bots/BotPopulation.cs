// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotPopulation.cs — how many bots the world has, and where they stand.
//
// This is uo-offline's [GenerateBots (GenerateBotsCommand.cs), with one large
// difference and several small ones.
//
// THE LARGE ONE: upstream MINTS WORLD ITEMS; this WRITES A FILE.
//
// [GenerateBots walks a hardcoded city table and news up a PlayerBotSpawner at
// each computed point. Those spawners are Items, so they land in the world save
// and are the authoritative record of the population - which is why upstream
// needs [ClearSpawners, a StartupCap "runaway brake" that prints "you likely
// have stacked spawners", and a BankFixtures pass that tops up a spawner whose
// count was "baked in" on an earlier boot. Every one of those exists to mop up
// the same puddle: a derived thing was written somewhere that outlives the
// derivation.
//
// So the recipe writes Spawns/Custom/<facet>/GG_BotPop.xml instead, and
// [GG_Reimport - which this shard already has, already tests, and already
// scopes by the GG_ prefix - turns the file into spawners. The population is
// then in git, visible in the editor's spawner layer beside every other GG_
// spawner, and impossible to stack: a regen replaces by UniqueId, and the
// UniqueId is derived from the slot's identity rather than minted.
//
// The small ones are marked (DEVIATION) at each site.
//
// The recipe reads the SAME graph the bots read. There is no city column here
// and no coordinate table: a town is a tag in bots.json destinations.towns, a
// bank is a `bank` destination carrying that tag, a station is whatever
// CrafterProfiles says the class works. Author a destination in the editor and
// the recipe grows; nothing here knows the name "Britain".

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>One spawner the recipe wants to exist.</summary>
    public sealed class BotSlot
    {
        /// <summary>The town tag this slot belongs to.</summary>
        public string Town;

        /// <summary>bank | shop | station | roam - the recipe slot, and the roles key.</summary>
        public string Kind;

        /// <summary>The destination this slot is anchored on.</summary>
        public string DestinationId;

        public string DestinationName;

        public BotRole Role;

        public string Behaviour;

        /// <summary>The class a station slot forces, or null.</summary>
        public BotClass? Class;

        /// <summary>The home town a fixture is given, or null. See PlayerBot.SeedHome.</summary>
        public string Home;

        /// <summary>The station a crafter is pinned to, or null.</summary>
        public string Station;

        public Point3D Location;

        public int Count;

        /// <summary>Half-width of the spawn area in tiles. 0 is the single tile.</summary>
        public int Spread;

        public string Name;

        public string UniqueId;

        /// <summary>The leading segment of the Objects2 entry: PlayerBot plus its property setters.</summary>
        public string TypeString
        {
            get
            {
                var text = new StringBuilder(96);

                text.Append("PlayerBot");
                text.Append("/Role/").Append(Role);

                if (Class != null)
                {
                    text.Append("/SeedClass/").Append(Class.Value);
                }

                if (!String.IsNullOrEmpty(Home))
                {
                    text.Append("/SeedHome/").Append(Home);
                }

                if (!String.IsNullOrEmpty(Station))
                {
                    text.Append("/SeedStation/").Append(Station);
                }

                // Seed last only because it reads that way; the setters are order-free by
                // construction (PlayerBot.ScheduleSeed).
                text.Append("/Seed/").Append(Behaviour);

                return text.ToString();
            }
        }

        public string Objects2
        {
            get
            {
                return String.Format(
                    "{0}:MX={1}:SB=0:RT=0:TO=0:KL=0:RK=0:CA=1:DN=-1:DX=-1:SP=1:PR=-1",
                    TypeString,
                    Count);
            }
        }

        public string Describe()
        {
            return String.Format(
                "{0} x{1} {2}{3} at {4} ({5},{6})",
                Role == BotRole.Fixed ? "FIXED" : "seed",
                Count,
                Class == null ? "" : BotClassHelper.DisplayName(Class.Value) + " ",
                Behaviour,
                DestinationId,
                Location.X,
                Location.Y);
        }
    }

    /// <summary>The whole recipe for one facet, plus what it could not do.</summary>
    public sealed class BotRecipe
    {
        public readonly List<BotSlot> Slots = new List<BotSlot>();

        /// <summary>Town -> the anchor destination its roamers spawn around, and how it was chosen.</summary>
        public readonly Dictionary<string, string> RoamAnchors =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Town -> the number of bots the recipe assigns it.</summary>
        public readonly Dictionary<string, int> TownTargets =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Town -> how many of those are pinned rather than roaming.</summary>
        public readonly Dictionary<string, int> TownPinned =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Things an author should know: a town with no work, a class with no station.</summary>
        public readonly List<string> Notes = new List<string>();

        public int TotalBots
        {
            get
            {
                int total = 0;

                foreach (BotSlot slot in Slots)
                {
                    total += slot.Count;
                }

                return total;
            }
        }

        public int FixedBots
        {
            get
            {
                int total = 0;

                foreach (BotSlot slot in Slots)
                {
                    if (slot.Role == BotRole.Fixed)
                    {
                        total += slot.Count;
                    }
                }

                return total;
            }
        }
    }

    public static class BotPopulation
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// Every spawner this recipe owns is named with this prefix, and it owns nothing else.
        ///
        /// The same idea as upstream's "CustomSpawn:" prefix (GenerateCustomSpawnersCommand.cs:37)
        /// and the same idea as this shard's GG_, one level narrower: GG_ scopes the shard's
        /// spawners against upstream's, and this scopes the generated ones against the
        /// hand-authored ones so a regen cannot flatten somebody's work.
        /// </summary>
        public const string NamePrefix = "GG_BotPop_";

        /// <summary>Hand-authored bot spawners live here and are never touched by the generator.</summary>
        public const string HandFileName = "GG_Bots.xml";

        public const string GeneratedFileName = "GG_BotPop.xml";

        /// <summary>The facet the recipe runs on, from Custom.PrimaryFacet as the snapshots read it.</summary>
        public static Map Facet
        {
            get
            {
                Map map;

                return JsonConfig.TryParseMap(Config.Get("Custom.PrimaryFacet", "Trammel"), out map)
                    ? map
                    : Map.Trammel;
            }
        }

        public static string GeneratedPath(Map map)
        {
            return Path.Combine(
                "Spawns",
                "Custom",
                (map ?? Map.Trammel).Name.ToLowerInvariant(),
                GeneratedFileName).Replace('\\', '/');
        }

        // -------------------------------------------------------------------
        // The recipe
        // -------------------------------------------------------------------

        /// <summary>
        /// Work out what the population should be, from the graph and bots.json. Reads only; the
        /// audit and the generator both come through here so they can never disagree.
        /// </summary>
        public static BotRecipe Build(Map map)
        {
            var recipe = new BotRecipe();

            if (map == null || map == Map.Internal)
            {
                recipe.Notes.Add("no facet");
                return recipe;
            }

            BotStore store = BotSystem.Store;
            BotPopulationConfig config = store.Population;
            IList<string> towns = store.Destinations.Towns;

            if (towns == null || towns.Count == 0)
            {
                recipe.Notes.Add(
                    "bots.json destinations.towns is empty, so there are no towns to populate");
                return recipe;
            }

            List<string> unknownTowns = config.UnknownTowns(towns);

            if (unknownTowns.Count > 0)
            {
                recipe.Notes.Add(String.Format(
                    "population.perTown names {0} that is not a town, so it does nothing",
                    String.Join(", ", unknownTowns.ToArray())));
            }

            Dictionary<string, double> shares = config.Shares(towns, DestinationCounts(map, towns));

            int bankCrowd = Math.Max(1, store.Life.FloorFor("bank"));

            foreach (string town in towns)
            {
                double share;
                shares.TryGetValue(town, out share);

                int townTarget = (int)Math.Round(config.Target * share);
                recipe.TownTargets[town] = townTarget;

                int pinned = 0;

                // ---- 1. The bank crowd. Fixed, so it is there at 05:00 as at 19:00.
                //
                // AND IT NAMES ITS BANK, which it did not until the floor was measured. The station
                // argument was null here and `SeedStation` only appears in the spawn string when it
                // is set, so a fixed BankSitter reached BotCrowds.CountFor with no DestinationId and
                // counted toward NO destination's floor. Twelve permanent sitters - four banks,
                // three each - were invisible to the very census they exist to satisfy, so every
                // bank on the facet read under its floor of 3 for ever and pulled at up to 4x
                // against shops, taverns and inns that were not. The garrison itself was never
                // missing: measured at exactly 3.00 standing at all four banks in 449 of 449
                // samples. A station is the right field for it - a bank is where this bot is
                // posted, exactly as a forge is where a Smith is posted.
                foreach (NavDestination bank in InTown(Nav.Destinations(map, "bank", null), town))
                {
                    recipe.Slots.Add(Slot(
                        town,
                        BotPopulationConfig.RoleBank,
                        bank,
                        BotRole.Fixed,
                        config.RoleFor(BotPopulationConfig.RoleBank),
                        null,
                        town,
                        bank.Id,
                        bankCrowd,
                        // A crowd wants room. Upstream's pinned bounds are 3 (GenerateBotsCommand
                        // .cs:306) and BankSitter re-homes itself with PickScatteredHome anyway,
                        // so this only has to stop three bots landing on one tile.
                        1));

                    pinned += bankCrowd;
                }

                // ---- 2. The staffed benches. One per STATION, not per arrival (DEVIATION).
                //
                // Upstream pins one crafter per craft-station ARRIVAL POINT (PinCrafters,
                // GenerateBotsCommand.cs:445-466), which on this graph would put four smiths at
                // one anvil: our forge arrivals are the four tiles within 2 of both the forge and
                // the anvil, which is a capacity ceiling and not a staffing target. "Staffed by
                // construction" needs one.
                var staffed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (CrafterProfile profile in CrafterProfiles.All)
                {
                    var station = new BotStation(profile.StationType, profile.StationTag);

                    if (station.IsNone)
                    {
                        continue;
                    }

                    // Available, not Destinations: a site the work layer excluded at load is one
                    // nobody can work, and staffing it would produce a bot standing at a bench
                    // reporting Blocked for ever.
                    List<NavDestination> stations = InTown(BotWorkSites.Available(map, station), town);

                    if (stations.Count == 0)
                    {
                        continue;
                    }

                    foreach (NavDestination bench in stations)
                    {
                        staffed.Add(bench.Id);

                        recipe.Slots.Add(Slot(
                            town,
                            BotPopulationConfig.RoleStation,
                            bench,
                            BotRole.Fixed,
                            config.RoleFor(BotPopulationConfig.RoleStation),
                            profile.Type,
                            town,
                            bench.Id,
                            1,
                            0));

                        pinned++;
                    }
                }

                // ---- 3. The shoppers. A lifecycle seed: it browses, then travels on.
                foreach (NavDestination shop in InTown(Nav.Destinations(map, "shop", null), town))
                {
                    // A shop that is somebody's bench already has somebody at it.
                    if (staffed.Contains(shop.Id))
                    {
                        continue;
                    }

                    recipe.Slots.Add(Slot(
                        town,
                        BotPopulationConfig.RoleShop,
                        shop,
                        BotRole.Lifecycle,
                        config.RoleFor(BotPopulationConfig.RoleShop),
                        null,
                        null,
                        null,
                        1,
                        0));

                    pinned++;
                }

                recipe.TownPinned[town] = pinned;

                // ---- 4. The roamers: the town's remaining share, in chunks.
                NavDestination anchor = RoamAnchor(map, town, recipe);

                if (anchor == null)
                {
                    recipe.Notes.Add(String.Format(
                        "{0} has nowhere to anchor a roaming spawner - no plaza, wander, gate or bank",
                        town));
                    continue;
                }

                int remaining = townTarget - pinned;

                if (remaining <= 0)
                {
                    if (remaining < 0)
                    {
                        recipe.Notes.Add(String.Format(
                            "{0} is pinned to {1} bot(s), which is over its share of {2} - it gets no roamers",
                            town,
                            pinned,
                            townTarget));
                    }

                    continue;
                }

                int index = 0;

                while (remaining > 0)
                {
                    int chunk = Math.Min(config.PerSpawner, remaining);
                    remaining -= chunk;

                    BotSlot roam = Slot(
                        town,
                        BotPopulationConfig.RoleRoam,
                        anchor,
                        BotRole.Lifecycle,
                        config.RoleFor(BotPopulationConfig.RoleRoam),
                        null,
                        null,
                        null,
                        chunk,
                        config.SpawnerRange);

                    // Several roaming spawners share one anchor, so the identity needs the index
                    // or they would all derive the same UniqueId and the file would hold one.
                    Rename(roam, String.Format("{0}_{1}", roam.Name, ++index));

                    recipe.Slots.Add(roam);
                }
            }

            return recipe;
        }

        /// <summary>
        /// Where a town's roamers spawn: a plaza, else the town square, else a gate, else the bank.
        ///
        /// A RULE OVER TAGS, deliberately, and not a table of town names. Britain answers at the
        /// first two stops; Trinsic has none of them and lands on its bank, which is the decided
        /// answer for now. Author a plaza-tagged square in Trinsic and this picks it up with no
        /// code change - which is the requirement, not a convenience. Bots.Recipe reports which
        /// stop each town resolved at, so the day that happens it is visible rather than silent.
        /// </summary>
        private static NavDestination RoamAnchor(Map map, string town, BotRecipe recipe)
        {
            NavDestination byTag = First(InTown(Nav.Destinations(map, null, "plaza"), town));

            if (byTag != null)
            {
                recipe.RoamAnchors[town] = byTag.Id + " (plaza tag)";
                return byTag;
            }

            string[] types = { "wander", "gate", "bank" };

            foreach (string type in types)
            {
                NavDestination found = First(InTown(Nav.Destinations(map, type, null), town));

                if (found != null)
                {
                    recipe.RoamAnchors[town] = found.Id + " (" + type + ")";
                    return found;
                }
            }

            return null;
        }

        private static BotSlot Slot(
            string town,
            string kind,
            NavDestination destination,
            BotRole role,
            string behaviour,
            BotClass? cls,
            string home,
            string station,
            int count,
            int spread)
        {
            var slot = new BotSlot
            {
                Town = town,
                Kind = kind,
                DestinationId = destination.Id,
                DestinationName = destination.Name,
                Role = role,
                Behaviour = behaviour,
                Class = cls,
                Home = home,
                Station = station,
                Location = SpawnPoint(destination),
                Count = Math.Max(1, count),
                Spread = Math.Max(0, spread)
            };

            Rename(slot, String.Format("{0}{1}_{2}_{3}", NamePrefix, town, kind, destination.Id));

            return slot;
        }

        /// <summary>
        /// Name a slot and derive its identity from that name.
        ///
        /// The UniqueId is a hash of the name rather than a fresh Guid, and that is the whole
        /// reason a regen replaces instead of stacking: [XmlLoad keys on UniqueId, so the same
        /// slot has to produce the same one on every machine and every run. Upstream mints a new
        /// spawner each time and deletes the old ones first, which is why it needs a runaway brake
        /// for the day the delete half does not happen.
        /// </summary>
        private static void Rename(BotSlot slot, string name)
        {
            slot.Name = Sanitise(name);
            slot.UniqueId = DeterministicId(slot.Name);
        }

        /// <summary>
        /// The tile a spawner sits on: the destination's first arrival point, or the destination
        /// itself when it has none.
        ///
        /// The arrival, not the destination, because a destination's own coordinate is the
        /// LANDMARK - a bank counter, the forge tile - and a spawner placed on it is a spawner
        /// asking for a bot to stand inside the furniture. The arrivals are exactly the authored
        /// standable tiles, which is what they are for.
        /// </summary>
        private static Point3D SpawnPoint(NavDestination destination)
        {
            if (destination.ArrivalList != null && destination.ArrivalList.Count > 0)
            {
                return destination.ArrivalList[0].Location;
            }

            return destination.Location;
        }

        private static Dictionary<string, int> DestinationCounts(Map map, IList<string> towns)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string town in towns)
            {
                counts[town] = InTown(Nav.Destinations(map, null, null), town).Count;
            }

            return counts;
        }

        private static List<NavDestination> InTown(List<NavDestination> destinations, string town)
        {
            var result = new List<NavDestination>();

            if (destinations == null)
            {
                return result;
            }

            foreach (NavDestination destination in destinations)
            {
                if (destination.HasTag(town))
                {
                    result.Add(destination);
                }
            }

            // Ordinal by id, so two runs on the same graph produce the same file. Nav's own
            // ordering is the file's, which is stable enough in practice and not guaranteed to be.
            result.Sort((a, b) => String.CompareOrdinal(a.Id, b.Id));

            return result;
        }

        private static NavDestination First(List<NavDestination> destinations)
        {
            return destinations.Count == 0 ? null : destinations[0];
        }

        // -------------------------------------------------------------------
        // Writing the file
        // -------------------------------------------------------------------

        /// <summary>
        /// Write the recipe to Spawns/Custom/&lt;facet&gt;/GG_BotPop.xml.
        ///
        /// Through AtomicFile so a half-written spawn file can never exist: [GG_Reimport deletes
        /// every GG_ spawner before it reads, and a truncated file at that moment would empty the
        /// world of bots and report success.
        /// </summary>
        public static bool Write(Map map, BotRecipe recipe, out string error)
        {
            error = null;

            if (recipe == null || recipe.Slots.Count == 0)
            {
                error = "the recipe is empty, and writing an empty spawn file would delete the population";
                return false;
            }

            var text = new StringBuilder(recipe.Slots.Count * 1200);

            text.Append("<Spawns>\n");

            foreach (BotSlot slot in recipe.Slots)
            {
                AppendPoint(text, map, slot);
            }

            text.Append("</Spawns>");

            return AtomicFile.Write(GeneratedPath(map), text.ToString(), out error);
        }

        /// <summary>
        /// One &lt;Points&gt; block, with every element the shard's own writer emits.
        ///
        /// The list and its order are the editor's createShape (tools/editor/spawners.js:354-376),
        /// which is itself XmlSpawner's DataSet column order. It matters because the file is read
        /// and rewritten by the editor as well as by the shard, and "a file whose rows disagree
        /// about which elements exist is a file nobody can diff".
        /// </summary>
        private static void AppendPoint(StringBuilder text, Map map, BotSlot slot)
        {
            int spread = slot.Spread;

            text.Append("  <Points>\n");

            Element(text, "Name", slot.Name);
            Element(text, "UniqueId", slot.UniqueId);
            Element(text, "Map", (map ?? Map.Trammel).Name);

            // X/Y/Width/Height are the spawn AREA; CentreX/Y/Z is the spawner item itself. A
            // zero-size area is the single tile, which is what a pinned slot wants.
            Element(text, "X", slot.Location.X - spread);
            Element(text, "Y", slot.Location.Y - spread);
            Element(text, "Width", spread * 2);
            Element(text, "Height", spread * 2);
            Element(text, "CentreX", slot.Location.X);
            Element(text, "CentreY", slot.Location.Y);
            Element(text, "CentreZ", slot.Location.Z);

            // <Range> is HomeRange, and 0 is deliberate: a bot is untethered by construction
            // (PlayerBot.OnAfterSpawn clears Home and RangeHome), so anything else here would be a
            // number that looks like it does something and does not.
            Element(text, "Range", 0);
            Element(text, "MaxCount", slot.Count);

            BotPopulationConfig config = BotSystem.Store.Population;

            Element(text, "MinDelay", (int)config.RespawnMin.TotalMinutes);
            Element(text, "MaxDelay", (int)config.RespawnMax.TotalMinutes);
            Element(text, "DelayInSec", false);
            Element(text, "Duration", 0);
            Element(text, "DespawnTime", 0);
            Element(text, "ProximityRange", -1);
            Element(text, "ProximityTriggerSound", 500);
            Element(text, "TriggerProbability", 1);
            Element(text, "InContainer", false);
            Element(text, "MinRefractory", 0);
            Element(text, "MaxRefractory", 0);
            Element(text, "TODStart", 0);
            Element(text, "TODEnd", 0);
            Element(text, "TODMode", 0);
            Element(text, "KillReset", 1);
            Element(text, "ExternalTriggering", false);
            Element(text, "SequentialSpawning", -1);
            Element(text, "AllowGhostTriggering", false);
            Element(text, "AllowNPCTriggering", false);
            Element(text, "SpawnOnTrigger", false);
            Element(text, "SmartSpawning", false);
            Element(text, "TickReset", false);
            Element(text, "Team", 0);
            Element(text, "Amount", 1);
            Element(text, "IsGroup", false);
            Element(text, "IsRunning", true);
            Element(text, "IsHomeRangeRelative", false);
            Element(text, "Objects2", slot.Objects2);

            text.Append("  </Points>\n");
        }

        private static void Element(StringBuilder text, string name, object value)
        {
            string rendered = value is bool ? ((bool)value ? "True" : "False") : Convert.ToString(value);

            text.Append("    <").Append(name).Append('>')
                .Append(Escape(rendered))
                .Append("</").Append(name).Append(">\n");
        }

        /// <summary>
        /// XmlTextWriter's escaping, which is what DataSet.WriteXml would have produced. `&gt;` is
        /// escaped as well as `&lt;` because that writer does, and the editor's round-trip test
        /// compares bytes.
        /// </summary>
        private static string Escape(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return String.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }

        /// <summary>
        /// A spawner name has to survive the editor's own key regex
        /// (tools/editor/whitelist.js:150 and its siblings), so keep it to what that allows.
        /// </summary>
        private static string Sanitise(string name)
        {
            var text = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                text.Append(Char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }

            return text.ToString();
        }

        /// <summary>
        /// A GUID derived from a string, so the same slot is the same spawner every time.
        ///
        /// MD5 for the arithmetic only - this names a spawner, it does not protect anything - and
        /// because it is the one hash of the right width in the framework on both Windows and Mono.
        /// </summary>
        private static string DeterministicId(string name)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(name));

                return new Guid(hash).ToString();
            }
        }

        // -------------------------------------------------------------------
        // Comparing the recipe against what is on disk
        // -------------------------------------------------------------------

        /// <summary>
        /// What differs between the recipe and GG_BotPop.xml, as lines an author can act on.
        ///
        /// This exists because the recipe is DERIVED and the file is not: author a Trinsic square
        /// in the editor and the recipe changes its mind about where Trinsic's roamers stand,
        /// while the file - and therefore the world - goes on saying the old thing, for ever,
        /// silently. An empty list means the file is what the graph currently asks for.
        ///
        /// Reading is a DataSet.ReadXml, the same thing XmlSpawner's loader does and the same
        /// thing GGSpawnCommands.TryCountSpawnPoints does as its pre-flight, rather than a second
        /// XML reader with its own opinions.
        /// </summary>
        public static List<string> DiffAgainstFile(Map map, BotRecipe recipe)
        {
            var differences = new List<string>();
            string path = GeneratedPath(map);
            string full = Path.Combine(Core.BaseDirectory, path);

            if (!File.Exists(full))
            {
                differences.Add(String.Format(
                    "{0} does not exist - run [BotPopulationGen", path));
                return differences;
            }

            var onDisk = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                var set = new DataSet("Spawns");

                using (FileStream stream = File.OpenRead(full))
                {
                    set.ReadXml(stream);
                }

                DataTable points = set.Tables["Points"];

                if (points != null)
                {
                    foreach (DataRow row in points.Rows)
                    {
                        string name = Column(row, "Name");

                        if (String.IsNullOrEmpty(name))
                        {
                            continue;
                        }

                        onDisk[name] = String.Format(
                            "{0}|{1}|{2}|{3}|{4}",
                            Column(row, "CentreX"),
                            Column(row, "CentreY"),
                            Column(row, "CentreZ"),
                            Column(row, "MaxCount"),
                            Column(row, "Objects2"));
                    }
                }
            }
            catch (Exception ex)
            {
                differences.Add(String.Format("{0} cannot be read: {1}", path, ex.Message));
                return differences;
            }

            foreach (BotSlot slot in recipe.Slots)
            {
                string want = String.Format(
                    "{0}|{1}|{2}|{3}|{4}",
                    slot.Location.X,
                    slot.Location.Y,
                    slot.Location.Z,
                    slot.Count,
                    slot.Objects2);

                string have;

                if (!onDisk.TryGetValue(slot.Name, out have))
                {
                    differences.Add("missing: " + slot.Name);
                    continue;
                }

                if (!String.Equals(want, have, StringComparison.Ordinal))
                {
                    differences.Add(String.Format("changed: {0} ({1} -> {2})", slot.Name, have, want));
                }

                onDisk.Remove(slot.Name);
            }

            foreach (var entry in onDisk)
            {
                // Only ours. A hand-authored spawner that happens to share the file is somebody
                // else's business, and deleting it is exactly what the prefix exists to prevent.
                if (entry.Key.StartsWith(NamePrefix, StringComparison.Ordinal))
                {
                    differences.Add("stale: " + entry.Key);
                }
            }

            differences.Sort(StringComparer.Ordinal);

            return differences;
        }

        private static string Column(DataRow row, string name)
        {
            return row.Table.Columns.Contains(name) && row[name] != DBNull.Value
                ? Convert.ToString(row[name])
                : String.Empty;
        }

        // -------------------------------------------------------------------
        // The audit text
        // -------------------------------------------------------------------

        /// <summary>
        /// What each town would get, without spawning anything. The shape of [NavAudit's answer,
        /// and for the same reason: the cheapest way to find a data gap is to ask before a boot.
        /// </summary>
        public static List<string> Describe(Map map, BotRecipe recipe)
        {
            var lines = new List<string>();
            BotPopulationConfig config = BotSystem.Store.Population;

            lines.Add(String.Format(
                "target {0} on {1}: {2} bot(s) across {3} spawner(s), {4} of them fixed.",
                config.Target,
                (map ?? Map.Trammel).Name,
                recipe.TotalBots,
                recipe.Slots.Count,
                recipe.FixedBots));

            foreach (var entry in recipe.TownTargets)
            {
                string town = entry.Key;
                int pinned;
                recipe.TownPinned.TryGetValue(town, out pinned);

                string anchor;
                recipe.RoamAnchors.TryGetValue(town, out anchor);

                var kinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (BotSlot slot in recipe.Slots)
                {
                    if (!Insensitive.Equals(slot.Town, town))
                    {
                        continue;
                    }

                    int count;
                    kinds.TryGetValue(slot.Kind, out count);
                    kinds[slot.Kind] = count + slot.Count;
                }

                var parts = new List<string>();

                foreach (var kind in kinds)
                {
                    parts.Add(String.Format("{0} {1}", kind.Value, kind.Key));
                }

                parts.Sort(StringComparer.Ordinal);

                lines.Add(String.Format(
                    "  {0}: share {1}, pinned {2} - {3}. roamers anchor on {4}.",
                    town,
                    entry.Value,
                    pinned,
                    parts.Count == 0 ? "nothing" : String.Join(", ", parts.ToArray()),
                    anchor ?? "NOTHING"));
            }

            foreach (string note in recipe.Notes)
            {
                lines.Add("  note: " + note);
            }

            return lines;
        }

        /// <summary>Live bot spawners in the world - the ones this recipe owns.</summary>
        public static List<XmlSpawner> LiveSpawners()
        {
            var found = new List<XmlSpawner>();

            // A World.Items walk, which custom code otherwise avoids (CLAUDE.md section 15).
            // Justified the same way GGSpawnCommands.DeleteExisting justifies its own: there is no
            // index of spawners by name, and this runs from a command, a probe or the one boot
            // sweep - never on a tick.
            foreach (Item item in World.Items.Values)
            {
                var spawner = item as XmlSpawner;

                if (spawner != null && !spawner.Deleted && spawner.Name != null
                    && spawner.Name.StartsWith(NamePrefix, StringComparison.Ordinal))
                {
                    found.Add(spawner);
                }
            }

            return found;
        }

        /// <summary>
        /// Fill every bot spawner now rather than on its own five-to-fifteen-minute timer.
        ///
        /// Upstream's RespawnAllSpawners (BotStartupManager.cs:119-154), minus its runaway brake:
        /// that brake exists because upstream's spawners persist in the world save and can stack,
        /// and ours are rebuilt from a file whose UniqueIds are derived, so there is nothing to
        /// stack. Upstream's other half - a World.Mobiles sweep deleting stale bots - is not
        /// ported either, because PlayerBot.Deserialize already deletes every bot on load, for
        /// every bot, however it got into the save.
        /// </summary>
        public static int RespawnAll()
        {
            List<XmlSpawner> spawners = LiveSpawners();
            int done = 0;

            foreach (XmlSpawner spawner in spawners)
            {
                try
                {
                    spawner.Respawn();
                    done++;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Bot spawner {0} would not respawn.", spawner.Name);
                }
            }

            if (done > 0)
            {
                Log.Info("Filled {0} bot spawner(s).", done);
            }

            // Re-arm the probe's settling window. Every fill is a fresh population that has to
            // walk to its benches before Bots.Recipe can fairly ask whether it is at them - and a
            // reimport mid-session is exactly the moment it would otherwise assert too early and
            // report eight empty stations that are simply still on their way.
            BotPopulationProbe.NoteFilled();

            return done;
        }
    }
}
