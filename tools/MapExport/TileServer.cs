using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Server.Custom.MapExport
{
    /// <summary>
    /// The isometric renderer as a long-lived child process, one tile per request.
    ///
    /// WHY A PROCESS AND NOT A BATCH RUN. A facet-wide isometric render cannot exist - Trammel is
    /// 61 gigapixels per floor - so tiles are rendered the first time somebody looks at them and
    /// kept forever. That makes the renderer a service, and a service it must be: process startup
    /// is cheap next to a cold TileMatrix and a cold art cache, and paying those per tile would
    /// make every pan feel broken.
    ///
    /// The protocol is one line in, one line out, over stdin and stdout:
    ///
    ///     &lt;- {"ready":true,"version":1,...}          the handshake, once
    ///     -&gt; tile &lt;floor&gt; &lt;level&gt; &lt;x&gt; &lt;y&gt;
    ///     &lt;- ok &lt;ms&gt; &lt;bytes&gt; &lt;path&gt;   or   err &lt;message&gt;
    ///
    /// Requests are answered in order and one at a time, which is not a simplification: Server's
    /// TileMatrix keeps its block buffers in static fields, so two renders at once corrupt each
    /// other. Everything diagnostic goes to stderr, so a stack trace can never be mistaken for a
    /// reply.
    /// </summary>
    internal static class TileServer
    {
        /// <summary>
        /// Bumped whenever a render would produce different pixels - a draw-order fix, a hue fix,
        /// stretched terrain. It is a path segment in the cache, so a bump orphans the whole old
        /// tree in one directory instead of leaving a cache that is half old and half new.
        /// </summary>
        public const int Version = 1;

        public static string CacheRoot(string tilesRoot, string facet)
        {
            return Path.Combine(tilesRoot, "iso", facet, "v" + Version.ToString(CultureInfo.InvariantCulture));
        }

        public static string TilePath(string tilesRoot, string facet, Floor floor, int level, int x, int y)
        {
            return Path.Combine(
                CacheRoot(tilesRoot, facet),
                FloorRules.Name(floor),
                level.ToString(CultureInfo.InvariantCulture),
                x.ToString(CultureInfo.InvariantCulture),
                y.ToString(CultureInfo.InvariantCulture) + ".png");
        }

        public static int Serve(IsoTileRenderer renderer, string tilesRoot, string facet, FloorRules rules)
        {
            Console.Out.Write(Handshake(renderer, facet, rules));
            Console.Out.Write('\n');
            Console.Out.Flush();

            string line;

            while ((line = Console.In.ReadLine()) != null)
            {
                line = line.Trim();

                if (line.Length == 0)
                {
                    continue;
                }

                if (line == "quit")
                {
                    break;
                }

                Console.Out.Write(Handle(renderer, tilesRoot, facet, line));
                Console.Out.Write('\n');
                Console.Out.Flush();
            }

            return 0;
        }

        private static string Handle(IsoTileRenderer renderer, string tilesRoot, string facet, string line)
        {
            string[] parts = line.Split(' ');

            if (parts.Length != 5 || parts[0] != "tile")
            {
                return "err bad request: " + line;
            }

            Floor floor;
            int level, x, y;

            if (!FloorRules.TryParse(parts[1], out floor))
            {
                return "err unknown floor: " + parts[1];
            }

            if (!Int32.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out level) ||
                !Int32.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ||
                !Int32.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out y))
            {
                return "err bad coordinates: " + line;
            }

            try
            {
                string path = TilePath(tilesRoot, facet, floor, level, x, y);

                var watch = Stopwatch.StartNew();
                byte[] pixels = renderer.Render(level, x, y, floor);
                PngWriter.Write(path, pixels, renderer.TileSize, renderer.TileSize, 4);
                watch.Stop();

                var written = new FileInfo(path);

                return String.Format(
                    CultureInfo.InvariantCulture,
                    "ok {0} {1} {2}",
                    watch.ElapsedMilliseconds,
                    written.Length,
                    path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return "err " + ex.Message.Replace('\n', ' ').Replace('\r', ' ');
            }
        }

        /// <summary>
        /// Everything the bridge and the editor need to address a tile, so neither holds a second
        /// copy of the projection or the level maths. Hand-written rather than serialized: net48
        /// has no System.Text.Json and pulling Newtonsoft into this tool for one object would be a
        /// permanent dependency for one line.
        /// </summary>
        public static string Handshake(IsoTileRenderer renderer, string facet, FloorRules rules)
        {
            var text = new StringBuilder();

            text.Append("{\"ready\":true");
            text.Append(",\"version\":").Append(Version);
            text.Append(",\"facet\":\"").Append(facet).Append('"');
            text.Append(",\"tileSize\":").Append(renderer.TileSize);
            text.Append(",\"maxLevel\":").Append(renderer.MaxLevel);
            text.Append(",\"artLevelDepth\":").Append(IsoTransform.ArtLevelDepth);
            text.Append(",\"floorLevelDepth\":").Append(IsoTransform.FloorLevelDepth);
            text.Append(",\"halfWidth\":").Append(IsoTransform.HalfWidth);
            text.Append(",\"halfHeight\":").Append(IsoTransform.HalfHeight);
            text.Append(",\"zStep\":").Append(IsoTransform.ZStep);
            text.Append(",\"floors\":{\"ground\":").Append(rules.Ground);
            text.Append(",\"first\":").Append(rules.First).Append('}');
            text.Append('}');

            return text.ToString();
        }

        /// <summary>
        /// Warms the cache over a world rectangle, through the same Render call the serve loop
        /// uses. An operator's tool: it exists so looking at Britain for the first time is a pan
        /// rather than a wait at every step, not because anything depends on it having been run.
        /// </summary>
        public static int Prerender(
            IsoTileRenderer renderer,
            string tilesRoot,
            string facet,
            int facetWidth,
            int facetHeight,
            Rectangle2D bounds,
            Action<string> log)
        {
            int originX = IsoTransform.OriginX(facetHeight);
            int originY = IsoTransform.OriginY();

            int x0 = bounds.Start.X;
            int y0 = bounds.Start.Y;
            int x1 = bounds.End.X;
            int y1 = bounds.End.Y;

            // The corners of a world rectangle map to the corners of the iso diamond around it:
            // widest at (x0, y1) and (x1, y0), tallest between (x0, y0) and (x1, y1).
            //
            // The pads differ per edge because sprites are not symmetrical about their anchor: one
            // is centred horizontally but rises its whole height above the anchor, and Z lifts it
            // further. Padding all four edges by the largest of those turned a 48x40 box into 1144
            // tiles, most of them empty sea.
            int padX = (IsoTransform.MaxSpriteWidth / 2) + 1;
            int padTop = IsoTransform.MaxSpriteHeight + IsoTransform.ZLift;
            int padBottom = IsoTransform.ZLift;

            int canvasLeft = IsoTransform.IsoX(x0, y1) - originX - padX;
            int canvasRight = IsoTransform.IsoX(x1, y0) - originX + padX;
            int canvasTop = IsoTransform.IsoY(x0, y0, 0) - originY - padTop;
            int canvasBottom = IsoTransform.IsoY(x1, y1, 0) - originY + padBottom;

            int written = 0;
            var watch = Stopwatch.StartNew();

            for (int level = renderer.MaxLevel - (IsoTransform.ArtLevelDepth - 1); level <= renderer.MaxLevel; level++)
            {
                if (level < 0)
                {
                    continue;
                }

                int span = renderer.TileSize * IsoTransform.Divisor(level, renderer.MaxLevel);

                int tileX0 = Math.Max(0, canvasLeft / span);
                int tileX1 = canvasRight / span;
                int tileY0 = Math.Max(0, canvasTop / span);
                int tileY1 = canvasBottom / span;

                bool floors = level > renderer.MaxLevel - IsoTransform.FloorLevelDepth;

                var stops = floors
                    ? new[] { Floor.Ground, Floor.First, Floor.All }
                    : new[] { Floor.All };

                int count = (tileX1 - tileX0 + 1) * (tileY1 - tileY0 + 1) * stops.Length;

                log(String.Format(
                    CultureInfo.InvariantCulture,
                    "  level {0}: {1} x {2} tiles, {3} floor(s), {4} render(s)",
                    level,
                    tileX1 - tileX0 + 1,
                    tileY1 - tileY0 + 1,
                    stops.Length,
                    count));

                foreach (Floor floor in stops)
                {
                    for (int tx = tileX0; tx <= tileX1; tx++)
                    {
                        for (int ty = tileY0; ty <= tileY1; ty++)
                        {
                            string path = TilePath(tilesRoot, facet, floor, level, tx, ty);

                            if (File.Exists(path))
                            {
                                continue;
                            }

                            byte[] pixels = renderer.Render(level, tx, ty, floor);
                            PngWriter.Write(path, pixels, renderer.TileSize, renderer.TileSize, 4);
                            written++;
                        }
                    }
                }
            }

            watch.Stop();

            log(String.Format(
                CultureInfo.InvariantCulture,
                "  wrote {0} tile(s) in {1:F1}s ({2} ms each)",
                written,
                watch.Elapsed.TotalSeconds,
                written > 0 ? watch.ElapsedMilliseconds / written : 0));

            return written;
        }
    }
}
