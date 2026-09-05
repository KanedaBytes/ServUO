using System;
using System.Collections.Generic;
using System.IO;

namespace Server.Custom.MapExport
{
    /// <summary>
    /// Renders radar tiles for the browser editor.
    ///
    /// It reads the client files through ServUO's own Server.TileMatrix rather than a private
    /// .mul reader, so the tiles can never disagree with what the shard thinks the map is. The
    /// only bootstrap that needs is Core.DataDirectories - no world, no scripts, no timers.
    /// </summary>
    internal static class Program
    {
        private const int DefaultTileSize = 256;

        private static readonly string[,] Facets =
        {
            // name, mapID, fileIndex, width, height - from Scripts/Misc/MapDefinitions.cs
            { "Felucca", "0", "0", "7168", "4096" },
            { "Trammel", "1", "1", "7168", "4096" },
            { "Ilshenar", "2", "2", "2304", "1600" },
            { "Malas", "3", "3", "2560", "2048" },
            { "Tokuno", "4", "4", "1448", "1448" },
            { "TerMur", "5", "5", "1280", "4096" }
        };

        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            string root = FindRepoRoot();
            string client = null;
            string output = null;
            string facetName = "Trammel";
            int tileSize = DefaultTileSize;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--client":
                        client = Require(args, ++i, "--client");
                        break;
                    case "--out":
                        output = Require(args, ++i, "--out");
                        break;
                    case "--facet":
                        facetName = Require(args, ++i, "--facet");
                        break;
                    case "--tile-size":
                        tileSize = Int32.Parse(Require(args, ++i, "--tile-size"));
                        break;
                    case "--help":
                    case "-h":
                        Usage();
                        return 0;
                    default:
                        throw new ArgumentException("Unknown argument: " + args[i]);
                }
            }

            if (tileSize < 32 || tileSize > 2048)
            {
                throw new ArgumentException("--tile-size must be between 32 and 2048.");
            }

            if (client == null)
            {
                client = ReadClientPathFromConfig(root);
            }

            if (client == null)
            {
                throw new ArgumentException(
                    "No client directory. Pass --client, or set DataPath.CustomPath in Config/DataPath.cfg.");
            }

            if (!Directory.Exists(client))
            {
                throw new DirectoryNotFoundException("Client directory not found: " + client);
            }

            if (output == null)
            {
                output = Path.Combine(root, "tools", "editor", "tiles");
            }

            // The whole bootstrap. Core.FindDataFile needs nothing else.
            Core.DataDirectories.Add(client);

            string radarPath = Core.FindDataFile("radarcol.mul");

            if (radarPath == null)
            {
                throw new FileNotFoundException("radarcol.mul not found under " + client);
            }

            RadarColors radar = RadarColors.Load(radarPath);

            Console.WriteLine("Client:  " + client);
            Console.WriteLine("Output:  " + output);
            Console.WriteLine("Radar:   {0} entries", radar.Count);

            int index = FindFacet(facetName);

            string name = Facets[index, 0];
            int mapId = Int32.Parse(Facets[index, 1]);
            int fileIndex = Int32.Parse(Facets[index, 2]);
            int width = Int32.Parse(Facets[index, 3]);
            int height = Int32.Parse(Facets[index, 4]);

            Console.WriteLine("Facet:   {0} ({1}x{2}), {3} level(s), {4}px tiles",
                name, width, height, TilePyramid.LevelCount(width, height, tileSize) + 1, tileSize);
            Console.WriteLine();

            // A standalone Map, never registered in Map.Maps, so its TileMatrix can be collected
            // and nothing else in the process can accidentally depend on it.
            var map = new Map(mapId, index, fileIndex, width, height, 0, name, MapRules.FeluccaRules);

            var started = DateTime.UtcNow;

            TilePyramid.Render(name, width, height, map.Tiles, radar, output, tileSize, Console.WriteLine);

            Console.WriteLine();
            Console.WriteLine("Done in {0:F1}s.", (DateTime.UtcNow - started).TotalSeconds);

            return 0;
        }

        private static int FindFacet(string name)
        {
            for (int i = 0; i < Facets.GetLength(0); i++)
            {
                if (String.Equals(Facets[i, 0], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            var names = new List<string>();

            for (int i = 0; i < Facets.GetLength(0); i++)
            {
                names.Add(Facets[i, 0]);
            }

            throw new ArgumentException(
                String.Format("Unknown facet '{0}'. Known: {1}", name, String.Join(", ", names.ToArray())));
        }

        /// <summary>
        /// Reads DataPath.CustomPath out of Config/DataPath.cfg by hand.
        ///
        /// Server.Config would do this, but it scans every .cfg in Config/ and this tool has no
        /// business loading the shard's whole configuration to find one string.
        /// </summary>
        private static string ReadClientPathFromConfig(string root)
        {
            string path = Path.Combine(root, "Config", "DataPath.cfg");

            if (!File.Exists(path))
            {
                return null;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();

                if (trimmed.Length == 0 || trimmed[0] == '#')
                {
                    continue;
                }

                int equals = trimmed.IndexOf('=');

                if (equals <= 0)
                {
                    continue;
                }

                string key = trimmed.Substring(0, equals).Trim().TrimStart('@');

                if (String.Equals(key, "CustomPath", StringComparison.OrdinalIgnoreCase))
                {
                    string value = trimmed.Substring(equals + 1).Trim();
                    return value.Length > 0 ? value : null;
                }
            }

            return null;
        }

        /// <summary>Walks up from the executable looking for ServUO.sln.</summary>
        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ServUO.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return Directory.GetCurrentDirectory();
        }

        private static string Require(string[] args, int index, string name)
        {
            if (index >= args.Length)
            {
                throw new ArgumentException(name + " needs a value.");
            }

            return args[index];
        }

        private static void Usage()
        {
            Console.WriteLine("MapExport - renders radar tiles for the shard editor.");
            Console.WriteLine();
            Console.WriteLine("  --facet <name>     Facet to render (default Trammel).");
            Console.WriteLine("  --client <path>    Client directory (default: Config/DataPath.cfg).");
            Console.WriteLine("  --out <path>       Output root (default: tools/editor/tiles).");
            Console.WriteLine("  --tile-size <n>    Tile pixels, 32-2048 (default 256).");
            Console.WriteLine();
            Console.WriteLine("Rendering a second facet is another run, not a code change.");
        }
    }
}
