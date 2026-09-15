// WorldCensus.cs - what the fleet is keeping awake, and where it is standing.
//
// WHY THIS EXISTS
// ---------------
// The population scale ramp has to say WHICH COST GROWS when the bot target is raised, and there
// were two it could not separate. BotTickManager.DescribeCost answers "what do the brains cost",
// and says so in its own comment; LoopCost answers "what does the whole cycle cost". Between them
// sits a third quantity that nothing in this tree could report and that grows with the fleet:
//
//   A Player-flagged mobile WAKES SECTORS, and a woken sector runs every creature's AI inside it.
//
// PlayerBot sets Player = true (PlayerBot.cs:529). Sector.OnEnter calls Map.ActivateSectors when a
// sector takes its first player (Sector.cs:148-154), and with Map.SectorActiveRange = 2 and
// SectorSize = 16 that is a 5x5 block of sectors - an 80x80 tile footprint - per bot.
// Sector.Activate then calls OnSectorActivate on every mobile in every one of them
// (Sector.cs:234-255), and BaseCreature.OnSectorActivate starts that creature's AI timer if it is
// PlayerRangeSensitive (BaseCreature.cs:7858-7864). BaseAI gates on exactly the same condition
// (BaseAI.cs:87: `!m.Map.GetSector(m).Active` means do not activate).
//
// So sixty bots do not cost sixty bots' worth of thinking. They cost sixty bots' worth of thinking
// PLUS every ordinary creature standing within forty tiles of one of them, which would otherwise be
// asleep. That is the term this file counts, and counting it is what lets the ramp's memo say
// whether it grows with the population or plateaus as the fleet's footprints start to overlap.
//
// WITH NO CLIENT CONNECTED, EVERY ACTIVE SECTOR IS THE FLEET'S. That is what makes this separable
// without a control boot: there is no other Player-flagged mobile on the shard during a measurement
// window, so the census needs no baseline subtraction. If somebody connects a client mid-window the
// number stops meaning that, which is why the report names the player count it saw.
//
// WHY REFLECTION, WHICH IS NOT A THING THIS TREE DOES LIGHTLY
// ----------------------------------------------------------
// Sector.Active is public (Sector.cs:360) but there is no public way to ENUMERATE sectors. Map
// exposes GetSector/GetRealSector, and both run InternalGetSector, which ALLOCATES a Sector on
// first touch (Map.cs:1626-1648). Trammel is 7168x4096, so 448x256 = 114,688 sectors: walking the
// grid through the public accessor would materialise every one of them and the instrument would
// become a larger cost than the thing it measures, permanently, for the rest of the boot.
//
// The private field is a jagged Sector[][] that is sparse by construction - only the rows and cells
// the world has actually touched exist. Reading it answers the question at its real size and
// allocates nothing. So: one cached FieldInfo, read-only, never written.
//
// This reads an upstream private field. It does NOT edit an upstream file, and there is no
// Custom/-side way to get the same answer - which is the test CLAUDE.md section 15 sets. If a
// future ServUO exposes the sector grid, this should drop the reflection and use it.
//
// WHERE THE FLEET IS, which is the other half and is here because it is the same walk
// ------------------------------------------------------------------------------------
// Bots.Recipe and Bots.Population both split the fleet by town, and both split it by HomeTown -
// where a bot was BORN. That was the right question when destinations.towns was the whole graph.
// It is not any more: the graph carries eight town tags and roughly a third of every destination
// roll now crosses a moongate, while destinations.towns is still ["britain", "trinsic"], so the
// home split can only ever name two towns however far the fleet has scattered. This bucket is by
// Region.Name - where a bot IS - and is meant to be read beside the home split, not instead of it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

using Server.Mobiles;

namespace Server.Custom
{
    public static class WorldCensus
    {
        private static readonly CustomLogger Log = CustomLogger.For("Core");

        public const string SnapshotPath = "Data/Live/world-census.json";

        /// <summary>
        /// Map.m_Sectors, resolved once. Null means the field has moved or been renamed, which is
        /// reported as a refusal rather than guessed around - a sector count that silently reads
        /// zero would be indistinguishable from an idle shard, which is the one answer this must
        /// never give by accident.
        /// </summary>
        private static readonly FieldInfo SectorsField =
            typeof(Map).GetField("m_Sectors", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class Census
        {
            public string MapName;
            public int ActiveSectors;
            public int SectorsAllocated;
            public int AwakeCreatures;
            public int SleepingCreatures;
            public int PlayerFlagged;
            public int Bots;
            public int Clients;
            public readonly Dictionary<string, int> ByRegion =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Walk one map's allocated sectors. Read-only; allocates nothing in the world.
        ///
        /// Returns false only when the field could not be read at all.
        /// </summary>
        private static bool TryCount(Map map, Census census, out string error)
        {
            error = null;

            if (SectorsField == null)
            {
                error = "Map.m_Sectors could not be resolved - the engine's sector grid has moved, "
                    + "and this instrument needs updating rather than trusting.";
                return false;
            }

            var grid = SectorsField.GetValue(map) as Sector[][];

            if (grid == null)
            {
                error = "Map.m_Sectors read back null for " + map;
                return false;
            }

            for (int x = 0; x < grid.Length; x++)
            {
                Sector[] column = grid[x];

                if (column == null)
                {
                    continue;
                }

                for (int y = 0; y < column.Length; y++)
                {
                    Sector sector = column[y];

                    if (sector == null)
                    {
                        continue;
                    }

                    census.SectorsAllocated++;

                    bool active = sector.Active;

                    if (active)
                    {
                        census.ActiveSectors++;
                        census.PlayerFlagged += sector.Players.Count;
                        census.Clients += sector.Clients.Count;
                    }

                    // The creature split is over every allocated sector, active or not, because
                    // the interesting figure is the RATIO: how many of the world's range-sensitive
                    // creatures the fleet is currently holding awake.
                    List<Mobile> mobiles = sector.Mobiles;

                    for (int i = 0; i < mobiles.Count; i++)
                    {
                        var creature = mobiles[i] as BaseCreature;

                        if (creature == null || creature.Deleted || !creature.PlayerRangeSensitive
                            || creature.AIObject == null)
                        {
                            continue;
                        }

                        if (active)
                        {
                            census.AwakeCreatures++;
                        }
                        else
                        {
                            census.SleepingCreatures++;
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>Where the live fleet is standing, by region name.</summary>
        private static void CountFleet(Census census)
        {
            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
                {
                    continue;
                }

                census.Bots++;

                Region region = bot.Region;
                string name = region == null || String.IsNullOrWhiteSpace(region.Name)
                    ? "(unnamed)"
                    : region.Name;

                int have;
                census.ByRegion[name] = census.ByRegion.TryGetValue(name, out have) ? have + 1 : 1;
            }
        }

        private static Census Build(Map map)
        {
            var census = new Census { MapName = map == null ? "(none)" : map.Name };

            string error;

            if (map != null && !TryCount(map, census, out error))
            {
                Log.Error("World census could not read the sector grid: {0}", error);
                census.ActiveSectors = -1;
            }

            CountFleet(census);

            return census;
        }

        /// <summary>The map the ramp measures, from the same key the snapshots read.</summary>
        private static Map Facet
        {
            get
            {
                Map map;
                return JsonConfig.TryParseMap(Config.Get("Custom.PrimaryFacet", "Trammel"), out map)
                    ? map
                    : Map.Trammel;
            }
        }

        /// <summary>
        /// The lines a headless reader takes off the ack.
        ///
        /// One walk per call and the census passed around, rather than each formatter building its
        /// own: the report and the file it is written beside have to be the SAME sample, and two
        /// walks a few milliseconds apart are two samples.
        /// </summary>
        public static List<string> Describe()
        {
            return Describe(Build(Facet));
        }

        private static List<string> Describe(Census census)
        {
            var culture = CultureInfo.InvariantCulture;
            var lines = new List<string>();

            if (census.ActiveSectors < 0)
            {
                lines.Add("REFUSED: the engine's sector grid could not be read - see the console.");
                return lines;
            }

            // Sixty bots at a 5x5 block each is 1500 sector-activations nominal; the union is what
            // the grid actually holds, and the gap between them IS the overlap. Reporting both is
            // what lets the ramp see the footprint saturate rather than infer it.
            int nominal = census.PlayerFlagged * 25;

            lines.Add(String.Format(
                culture,
                "{0}: {1} sector(s) active of {2} allocated; {3} player-flagged mobile(s) in them "
                + "would nominally wake {4}, so {5} of that is overlap",
                census.MapName, census.ActiveSectors, census.SectorsAllocated,
                census.PlayerFlagged, nominal,
                nominal - census.ActiveSectors));

            int sensitive = census.AwakeCreatures + census.SleepingCreatures;

            lines.Add(String.Format(
                culture,
                "{0} range-sensitive creature(s) awake inside them, {1} asleep outside ({2} total, "
                + "{3:F1}% awake)",
                census.AwakeCreatures, census.SleepingCreatures, sensitive,
                sensitive == 0 ? 0.0 : (census.AwakeCreatures * 100.0) / sensitive));

            lines.Add(String.Format(
                culture,
                "{0} live bot(s); {1} connected client(s) - a non-zero client count means the "
                + "sector figures are NOT the fleet's alone",
                census.Bots, census.Clients));

            var names = new List<string>(census.ByRegion.Keys);
            names.Sort(StringComparer.Ordinal);

            var where = new StringBuilder("where the fleet is standing: ");

            if (names.Count == 0)
            {
                where.Append("nowhere - no live bots");
            }
            else
            {
                for (int i = 0; i < names.Count; i++)
                {
                    if (i > 0)
                    {
                        where.Append(", ");
                    }

                    where.Append(names[i]).Append(' ').Append(census.ByRegion[names[i]]);
                }
            }

            lines.Add(where.ToString());
            lines.Add(ProcessHealth.DescribeCollections() + "; "
                + ProcessHealth.ThreadCount + " thread(s)");

            return lines;
        }

        public static string BuildJson()
        {
            return BuildJson(Build(Facet));
        }

        private static string BuildJson(Census census)
        {
            var culture = CultureInfo.InvariantCulture;
            var builder = new StringBuilder(2048);

            builder.Append("{\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"map\": ").Append(Json.Quote(census.MapName)).Append(",\n");
            builder.Append("  \"sectorsActive\": ").Append(census.ActiveSectors).Append(",\n");
            builder.Append("  \"sectorsAllocated\": ").Append(census.SectorsAllocated).Append(",\n");
            builder.Append("  \"sectorsNominal\": ").Append(census.PlayerFlagged * 25).Append(",\n");
            builder.Append("  \"playerFlagged\": ").Append(census.PlayerFlagged).Append(",\n");
            builder.Append("  \"clients\": ").Append(census.Clients).Append(",\n");
            builder.Append("  \"creaturesAwake\": ").Append(census.AwakeCreatures).Append(",\n");
            builder.Append("  \"creaturesAsleep\": ").Append(census.SleepingCreatures).Append(",\n");
            builder.Append("  \"bots\": ").Append(census.Bots).Append(",\n");
            builder.Append("  \"gcWindowSeconds\": ")
                .Append(ProcessHealth.CounterWindowSeconds.ToString("F1", culture)).Append(",\n");
            builder.Append("  \"threads\": ").Append(ProcessHealth.ThreadCount).Append(",\n");
            builder.Append("  \"byRegion\": [\n");

            var names = new List<string>(census.ByRegion.Keys);
            names.Sort(StringComparer.Ordinal);

            for (int i = 0; i < names.Count; i++)
            {
                builder.Append("    {\"region\":").Append(Json.Quote(names[i]));
                builder.Append(",\"bots\":").Append(census.ByRegion[names[i]]).Append("}");

                if (i < names.Count - 1)
                {
                    builder.Append(",");
                }

                builder.Append("\n");
            }

            builder.Append("  ]\n}\n");

            return builder.ToString();
        }

        /// <summary>Write the snapshot and hand back the same sample's lines.</summary>
        public static bool TryWrite(out string message, out List<string> lines)
        {
            Census census = Build(Facet);

            lines = Describe(census);

            string error;

            if (!AtomicFile.Write(SnapshotPath, BuildJson(census), out error))
            {
                message = error;
                Log.Error("Could not write world-census.json: {0}", error);
                return false;
            }

            message = lines.Count > 0 ? lines[0] : "nothing counted";

            return true;
        }
    }
}
