using System;
using System.Collections.Generic;
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
    ///     &lt;- {"ready":true,"version":2,...}          the handshake, once
    ///     -&gt; items &lt;path&gt;
    ///     &lt;- ok &lt;count&gt; &lt;id&gt;               or   err &lt;message&gt;
    ///     -&gt; tile &lt;layer&gt; &lt;floor&gt; &lt;level&gt; &lt;x&gt; &lt;y&gt;
    ///     &lt;- ok &lt;ms&gt; &lt;bytes&gt; &lt;path&gt;       or   empty   or   err &lt;message&gt;
    ///
    /// `layer` is `map` - the client's land and statics, cached under the renderer version and kept
    /// forever - or `items`, the shard's own furniture, cached under the SNAPSHOT's identity so a
    /// re-decorate throws away seconds of work rather than minutes. `empty` is the answer for an
    /// item tile with nothing in it, which is most of them, and nothing is written to disk for it.
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
        public const int Version = 2;

        /// <summary>The map layer's directory. Items live under `items-&lt;snapshot id&gt;` beside it.</summary>
        public const string MapLayer = "map";

        public static string CacheRoot(string tilesRoot, string facet)
        {
            return Path.Combine(tilesRoot, "iso", facet, "v" + Version.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The directory a layer's tiles live in. `map` is constant; an item layer carries the
        /// snapshot's own id, so a new snapshot is a new directory and a stale render can never be
        /// served as a current one.
        /// </summary>
        public static string LayerName(Layer layer, WorldItems items)
        {
            return layer == Layer.Map ? MapLayer : "items-" + items.Id;
        }

        public static string TilePath(
            string tilesRoot, string facet, string layer, Floor floor, int level, int x, int y)
        {
            return Path.Combine(
                CacheRoot(tilesRoot, facet),
                layer,
                FloorRules.Name(floor),
                level.ToString(CultureInfo.InvariantCulture),
                x.ToString(CultureInfo.InvariantCulture),
                y.ToString(CultureInfo.InvariantCulture) + ".png");
        }

        /// <summary>
        /// Deletes item layers that are not the current snapshot's.
        ///
        /// A snapshot is thrown away wholesale rather than invalidated, so without this every
        /// re-decorate leaves a full tree behind and the cache grows without bound. The map layer
        /// is never touched: it is the expensive one and it is still correct.
        /// </summary>
        public static void SweepStaleItemLayers(
            string tilesRoot, string facet, WorldItems items, Action<string> log)
        {
            string root = CacheRoot(tilesRoot, facet);

            if (!Directory.Exists(root))
            {
                return;
            }

            string keep = items.Any ? LayerName(Layer.Items, items) : null;

            foreach (string directory in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(directory);

                if (!name.StartsWith("items-", StringComparison.Ordinal) || name == keep)
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, true);
                    log("  swept stale item layer " + name);
                }
                catch (Exception ex)
                {
                    log("  could not sweep " + name + ": " + ex.Message);
                }
            }
        }

        public static int Serve(
            IsoTileRenderer renderer, string tilesRoot, string facet, FloorRules rules, int facetHeight)
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

                Console.Out.Write(Handle(renderer, tilesRoot, facet, facetHeight, line));
                Console.Out.Write('\n');
                Console.Out.Flush();
            }

            return 0;
        }

        private static string Handle(
            IsoTileRenderer renderer, string tilesRoot, string facet, int facetHeight, string line)
        {
            string[] parts = line.Split(' ');

            if (parts.Length == 2 && parts[0] == "items")
            {
                return LoadItems(renderer, tilesRoot, facet, facetHeight, parts[1]);
            }

            if (parts.Length != 6 || parts[0] != "tile")
            {
                return "err bad request: " + line;
            }

            Layer layer;

            if (parts[1] == "map")
            {
                layer = Layer.Map;
            }
            else if (parts[1] == "items")
            {
                layer = Layer.Items;
            }
            else
            {
                return "err unknown layer: " + parts[1];
            }

            if (layer == Layer.Items && !renderer.Items.Any)
            {
                return "err no world-item snapshot is loaded";
            }

            Floor floor;
            int level, x, y;

            if (!FloorRules.TryParse(parts[2], out floor))
            {
                return "err unknown floor: " + parts[2];
            }

            if (!Int32.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out level) ||
                !Int32.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ||
                !Int32.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out y))
            {
                return "err bad coordinates: " + line;
            }

            try
            {
                var watch = Stopwatch.StartNew();
                byte[] pixels = renderer.Render(layer, level, x, y, floor);

                if (pixels == null)
                {
                    // No item can reach this tile. Answered rather than written: tens of thousands
                    // of identical transparent PNGs is not a cache, it is litter.
                    return "empty";
                }

                string path = TilePath(
                    tilesRoot, facet, LayerName(layer, renderer.Items), floor, level, x, y);

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

        private static string LoadItems(
            IsoTileRenderer renderer, string tilesRoot, string facet, int facetHeight, string path)
        {
            string error;
            WorldItems items = WorldItems.Load(path, facet, facetHeight, out error);

            if (error != null)
            {
                return "err " + error;
            }

            renderer.Items = items;
            SweepStaleItemLayers(tilesRoot, facet, items, Console.Error.WriteLine);

            return String.Format(CultureInfo.InvariantCulture, "ok {0} {1}", items.Count, items.Id);
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
        /// How much of a hillside actually stretched, and what stopped the rest.
        ///
        /// "Does this fix the slopes" deserves a number before it deserves an opinion. Only 4,085
        /// of 16,384 land ids carry a texmap, so a slope built from the other three quarters still
        /// terraces - faithfully, because that is what the client shows, but the question of
        /// whether any given hillside is in that quarter is empirical. This answers it, and names
        /// the untextured ids so the decision about warping the flat art onto a quad later is made
        /// from a list rather than a hunch.
        /// </summary>
        public static void TerrainReport(
            TileMatrix tiles, int facetWidth, int facetHeight, Rectangle2D bounds, Action<string> log)
        {
            int x0 = Math.Max(0, bounds.Start.X);
            int y0 = Math.Max(0, bounds.Start.Y);
            int x1 = Math.Min(facetWidth - 1, bounds.End.X);
            int y1 = Math.Min(facetHeight - 1, bounds.End.Y);

            int total = 0, level = 0, stretched = 0, terraced = 0;
            var untextured = new Dictionary<int, int>();

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    LandTile land = tiles.GetLandTile(x, y);

                    int zTop = land.Z;
                    int zRight = EdgeZ(tiles, x + 1, y, facetWidth, facetHeight);
                    int zLeft = EdgeZ(tiles, x, y + 1, facetWidth, facetHeight);
                    int zBottom = EdgeZ(tiles, x + 1, y + 1, facetWidth, facetHeight);

                    total++;

                    if (zTop == zRight && zTop == zLeft && zTop == zBottom)
                    {
                        level++;
                        continue;
                    }

                    int id = land.ID & 0x3FFF;
                    int textureId = Ultima.TileData.LandTable[id].TextureID;

                    if (textureId != 0 && Ultima.Textures.TestTexture(textureId))
                    {
                        stretched++;
                        continue;
                    }

                    terraced++;

                    int seen;
                    untextured.TryGetValue(id, out seen);
                    untextured[id] = seen + 1;
                }
            }

            int sloped = stretched + terraced;

            log(String.Format(
                CultureInfo.InvariantCulture,
                "  {0},{1} {2}x{3}: {4} tile(s), {5} level, {6} sloped",
                bounds.Start.X, bounds.Start.Y, bounds.Width, bounds.Height, total, level, sloped));

            log(String.Format(
                CultureInfo.InvariantCulture,
                "  of the sloped: {0} stretched ({1:F1}%), {2} terraced for want of a texture ({3:F1}%)",
                stretched,
                sloped == 0 ? 0.0 : (stretched * 100.0) / sloped,
                terraced,
                sloped == 0 ? 0.0 : (terraced * 100.0) / sloped));

            if (untextured.Count == 0)
            {
                log("  every sloped tile here had a texture.");
                return;
            }

            var ids = new List<KeyValuePair<int, int>>(untextured);
            ids.Sort((a, b) => b.Value.CompareTo(a.Value));

            log(String.Format(
                CultureInfo.InvariantCulture,
                "  {0} untextured land id(s) on slopes here:", ids.Count));

            for (int i = 0; i < ids.Count && i < 12; i++)
            {
                log(String.Format(
                    CultureInfo.InvariantCulture,
                    "    0x{0:X4}  {1,5} tile(s)  {2}",
                    ids[i].Key,
                    ids[i].Value,
                    Ultima.TileData.LandTable[ids[i].Key].Name));
            }

            if (ids.Count > 12)
            {
                log(String.Format(CultureInfo.InvariantCulture, "    ... and {0} more.", ids.Count - 12));
            }
        }

        private static int EdgeZ(TileMatrix tiles, int x, int y, int facetWidth, int facetHeight)
        {
            if (x >= facetWidth)
            {
                x = facetWidth - 1;
            }

            if (y >= facetHeight)
            {
                y = facetHeight - 1;
            }

            return tiles.GetLandTile(x, y).Z;
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
                            string path = TilePath(tilesRoot, facet, MapLayer, floor, level, tx, ty);

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
