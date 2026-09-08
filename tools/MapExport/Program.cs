using System;
using System.Collections.Generic;
using System.IO;

namespace Server.Custom.MapExport
{
    /// <summary>
    /// Renders map tiles for the browser editor, in two shapes.
    ///
    /// RADAR (the default) is the original: one pixel per game tile, coloured from radarcol.mul,
    /// cut into a pyramid in one pass over the facet. Six seconds for Trammel, run once when the
    /// client files change.
    ///
    /// ISOMETRIC (--serve, --prerender) draws the real client art. It cannot be a batch pass -
    /// Trammel's isometric canvas is 61 gigapixels per floor - so tiles are rendered one at a time
    /// on demand and cached forever. --serve is the long-lived child process the editor bridge
    /// drives; --prerender warms a world rectangle through the same code.
    ///
    /// Both read the client files through ServUO's own Server.TileMatrix rather than a private
    /// .mul reader, so the tiles can never disagree with what the shard thinks the map is. The
    /// only bootstrap that needs is Core.DataDirectories - no world, no scripts, no timers.
    /// Ultima is used by the isometric path for two things and nothing else: sprite pixels and
    /// hue ramps.
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
            bool serve = false;
            string prerender = null;
            int floorGround = FloorRules.DefaultGround;
            int floorFirst = FloorRules.DefaultFirst;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--serve":
                        serve = true;
                        break;
                    case "--prerender":
                        prerender = Require(args, ++i, "--prerender");
                        break;
                    case "--floor-ground":
                        floorGround = Int32.Parse(Require(args, ++i, "--floor-ground"));
                        break;
                    case "--floor-first":
                        floorFirst = Int32.Parse(Require(args, ++i, "--floor-first"));
                        break;
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

            int index = FindFacet(facetName);

            string name = Facets[index, 0];
            int mapId = Int32.Parse(Facets[index, 1]);
            int fileIndex = Int32.Parse(Facets[index, 2]);
            int width = Int32.Parse(Facets[index, 3]);
            int height = Int32.Parse(Facets[index, 4]);

            // A standalone Map, never registered in Map.Maps, so its TileMatrix can be collected
            // and nothing else in the process can accidentally depend on it.
            var map = new Map(mapId, index, fileIndex, width, height, 0, name, MapRules.FeluccaRules);

            if (serve || prerender != null)
            {
                // stdout is the protocol channel in --serve, so every human-readable line goes to
                // stderr. Doing the same for --prerender keeps one habit rather than two.
                Action<string> log = Console.Error.WriteLine;

                var rules = new FloorRules(floorGround, floorFirst);
                IsoTileRenderer renderer = BuildIsoRenderer(client, map, width, height, tileSize, rules, log);

                if (serve)
                {
                    return TileServer.Serve(renderer, output, name, rules);
                }

                log("Client:  " + client);
                log("Cache:   " + TileServer.CacheRoot(output, name));
                log(String.Format(
                    "Facet:   {0} ({1}x{2}), iso canvas {3}x{4}, levels 0-{5}",
                    name,
                    width,
                    height,
                    IsoTransform.CanvasWidth(width, height),
                    IsoTransform.CanvasHeight(width, height),
                    renderer.MaxLevel));
                log("");

                TileServer.Prerender(renderer, output, name, width, height, ParseBounds(prerender), log);
                return 0;
            }

            string radarPath = Core.FindDataFile("radarcol.mul");

            if (radarPath == null)
            {
                throw new FileNotFoundException("radarcol.mul not found under " + client);
            }

            RadarColors radar = RadarColors.Load(radarPath);

            Console.WriteLine("Client:  " + client);
            Console.WriteLine("Output:  " + output);
            Console.WriteLine("Radar:   {0} entries", radar.Count);

            Console.WriteLine("Facet:   {0} ({1}x{2}), {3} level(s), {4}px tiles",
                name, width, height, TilePyramid.LevelCount(width, height, tileSize) + 1, tileSize);
            Console.WriteLine();

            var started = DateTime.UtcNow;

            TilePyramid.Render(name, width, height, map.Tiles, radar, output, tileSize, Console.WriteLine);

            Console.WriteLine();
            Console.WriteLine("Done in {0:F1}s.", (DateTime.UtcNow - started).TotalSeconds);

            return 0;
        }

        /// <summary>
        /// Points Ultima at the same client directory Server is using, then builds the renderer.
        ///
        /// THE ORDER MATTERS. Ultima.Art, Ultima.Hues and Ultima.TileData each resolve their file
        /// paths in a static initialiser, from whatever Files.MulPath says the first time anything
        /// touches them - and Files' own static constructor fills that in from the Windows registry
        /// (Ultima/Files.cs:76-80). Touch Art before SetMulPath and the process spends its life
        /// reading some other install, or nothing at all. So this runs before the first mention of
        /// any Ultima type.
        /// </summary>
        private static IsoTileRenderer BuildIsoRenderer(
            string client,
            Map map,
            int width,
            int height,
            int tileSize,
            FloorRules rules,
            Action<string> log)
        {
            Ultima.Files.SetMulPath(client);

            // Art would otherwise hold a Bitmap[0xFFFF] of its own. ArtCache keeps the decoded
            // pixels instead, so the bitmaps are ours to dispose and there is nothing shared to
            // corrupt when a hue is applied.
            Ultima.Files.CacheData = false;

            string art = Ultima.Files.GetFilePath("artLegacyMUL.uop") ?? Ultima.Files.GetFilePath("art.mul");

            if (art == null)
            {
                throw new FileNotFoundException("No art.mul or artLegacyMUL.uop under " + client);
            }

            log("Client:  " + client);
            log("Art:     " + art);
            log("Hues:    " + (Ultima.Files.GetFilePath("hues.mul") ?? "MISSING"));

            return new IsoTileRenderer(map.Tiles, new ArtCache(), rules, width, height, tileSize);
        }

        /// <summary>Parses --prerender's "x,y,width,height".</summary>
        private static Rectangle2D ParseBounds(string text)
        {
            string[] parts = text.Split(',');

            if (parts.Length != 4)
            {
                throw new ArgumentException("--prerender takes x,y,width,height (got '" + text + "').");
            }

            int x = Int32.Parse(parts[0].Trim());
            int y = Int32.Parse(parts[1].Trim());
            int w = Int32.Parse(parts[2].Trim());
            int h = Int32.Parse(parts[3].Trim());

            if (w <= 0 || h <= 0)
            {
                throw new ArgumentException("--prerender width and height must be positive.");
            }

            return new Rectangle2D(x, y, w, h);
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
            Console.WriteLine("MapExport - renders map tiles for the shard editor.");
            Console.WriteLine();
            Console.WriteLine("  --facet <name>     Facet to render (default Trammel).");
            Console.WriteLine("  --client <path>    Client directory (default: Config/DataPath.cfg).");
            Console.WriteLine("  --out <path>       Output root (default: tools/editor/tiles).");
            Console.WriteLine("  --tile-size <n>    Tile pixels, 32-2048 (default 256).");
            Console.WriteLine();
            Console.WriteLine("Isometric art. There is no batch pass - a facet is 61 gigapixels per");
            Console.WriteLine("floor - so tiles are rendered on demand and cached forever:");
            Console.WriteLine();
            Console.WriteLine("  --serve              Read tile requests on stdin; the bridge's child.");
            Console.WriteLine("  --prerender x,y,w,h  Warm the cache over a world rectangle.");
            Console.WriteLine("  --floor-ground <n>   Ground cutoff above the land (default 20).");
            Console.WriteLine("  --floor-first <n>    First-floor cutoff above the land (default 40).");
            Console.WriteLine();
            Console.WriteLine("Rendering a second facet is another run, not a code change.");
        }
    }
}
