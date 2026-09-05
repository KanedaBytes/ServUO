using System;
using System.IO;

namespace Server.Custom.MapExport
{
    /// <summary>
    /// Renders a facet to a pyramid of PNG tiles.
    ///
    /// This is a plain PIXEL pyramid, not a slippy/Mercator one: there is no projection, because
    /// a UO facet is already a flat grid. Level 0 is the whole facet in a single tile and each
    /// level down doubles the resolution, so the DEEPEST level is exactly one pixel per game
    /// tile. Nothing finer is generated - the viewer magnifies with nearest-neighbour, which is
    /// honest about what the data is. A genuinely finer level would mean rendering art tiles
    /// rather than radar colours, which is a different tool.
    ///
    /// The level count must match view.js maxZoomFor() exactly, or the editor asks for tiles
    /// that were never rendered. Both use ceil-halving until the facet fits one tile.
    /// </summary>
    internal static class TilePyramid
    {
        private const int BytesPerPixel = 3;

        public static void Render(string facetName, int width, int height, Server.TileMatrix tiles,
            RadarColors radar, string outputRoot, int tileSize, Action<string> log)
        {
            int levels = LevelCount(width, height, tileSize);

            string facetRoot = Path.Combine(outputRoot, facetName);

            byte[] pixels = RenderFacet(width, height, tiles, radar, log);

            int levelWidth = width;
            int levelHeight = height;

            for (int z = levels; z >= 0; z--)
            {
                int written = WriteLevel(facetRoot, z, pixels, levelWidth, levelHeight, tileSize);

                log(String.Format("  level {0}: {1}x{2} px, {3} tile(s)", z, levelWidth, levelHeight, written));

                if (z == 0)
                {
                    break;
                }

                pixels = Downsample(pixels, ref levelWidth, ref levelHeight);
            }
        }

        /// <summary>
        /// Must match view.js maxZoomFor(). Ceil-halve until the facet fits in one tile.
        /// </summary>
        public static int LevelCount(int width, int height, int tileSize)
        {
            int levels = 0;

            while (width > tileSize || height > tileSize)
            {
                width = (width + 1) / 2;
                height = (height + 1) / 2;
                levels++;
            }

            return levels;
        }

        /// <summary>
        /// One pixel per game tile, statics composited over land by highest Z.
        ///
        /// Statics matter more than they sound: rivers and docks are static water and plank
        /// items sitting on dirt land, so a land-only render shows tan riverbeds and no docks.
        /// </summary>
        private static byte[] RenderFacet(int width, int height, Server.TileMatrix tiles,
            RadarColors radar, Action<string> log)
        {
            var pixels = new byte[width * height * BytesPerPixel];

            int blockWidth = width >> 3;
            int blockHeight = height >> 3;

            for (int bx = 0; bx < blockWidth; bx++)
            {
                Server.LandTile[] land = null;
                Server.StaticTile[][][] statics = null;

                for (int by = 0; by < blockHeight; by++)
                {
                    land = tiles.GetLandBlock(bx, by);
                    statics = tiles.GetStaticBlock(bx, by);

                    for (int tx = 0; tx < 8; tx++)
                    {
                        for (int ty = 0; ty < 8; ty++)
                        {
                            Server.LandTile tile = land[(ty << 3) + tx];

                            uint color = radar.Land(tile.ID);

                            Server.StaticTile[] column = statics[tx][ty];

                            int top = tile.Z;
                            int topId = -1;

                            for (int i = 0; i < column.Length; i++)
                            {
                                // >= so the last static in file order wins a tie, which is the
                                // client's own draw order.
                                if (column[i].Z >= top)
                                {
                                    top = column[i].Z;
                                    topId = column[i].ID;
                                }
                            }

                            if (topId >= 0)
                            {
                                color = radar.Static(topId);
                            }

                            int px = (bx << 3) + tx;
                            int py = (by << 3) + ty;
                            int offset = ((py * width) + px) * BytesPerPixel;

                            pixels[offset] = (byte)(color >> 16);
                            pixels[offset + 1] = (byte)(color >> 8);
                            pixels[offset + 2] = (byte)color;
                        }
                    }
                }

                if (blockWidth >= 16 && bx % (blockWidth / 16) == 0)
                {
                    log(String.Format("  rendering... {0}%", (bx * 100) / blockWidth));
                }
            }

            return pixels;
        }

        private static int WriteLevel(string facetRoot, int z, byte[] pixels, int width, int height, int tileSize)
        {
            string levelRoot = Path.Combine(facetRoot, z.ToString());

            int columns = (width + tileSize - 1) / tileSize;
            int rows = (height + tileSize - 1) / tileSize;

            var tile = new byte[tileSize * tileSize * BytesPerPixel];

            int written = 0;

            for (int tx = 0; tx < columns; tx++)
            {
                string columnRoot = Path.Combine(levelRoot, tx.ToString());

                for (int ty = 0; ty < rows; ty++)
                {
                    CopyTile(pixels, width, height, tile, tx, ty, tileSize);

                    PngWriter.Write(Path.Combine(columnRoot, ty + ".png"), tile, tileSize, tileSize);
                    written++;
                }
            }

            return written;
        }

        /// <summary>Edge tiles are padded with black rather than cropped, so every tile is square.</summary>
        private static void CopyTile(byte[] pixels, int width, int height, byte[] tile, int tx, int ty, int tileSize)
        {
            Array.Clear(tile, 0, tile.Length);

            int originX = tx * tileSize;
            int originY = ty * tileSize;

            for (int y = 0; y < tileSize; y++)
            {
                int sourceY = originY + y;

                if (sourceY >= height)
                {
                    break;
                }

                int copyWidth = Math.Min(tileSize, width - originX);

                if (copyWidth <= 0)
                {
                    break;
                }

                Buffer.BlockCopy(
                    pixels,
                    ((sourceY * width) + originX) * BytesPerPixel,
                    tile,
                    y * tileSize * BytesPerPixel,
                    copyWidth * BytesPerPixel);
            }
        }

        /// <summary>
        /// 2x2 box average, not nearest-neighbour, so a one-tile road or river survives being
        /// zoomed out instead of flickering in and out depending on which pixel was sampled.
        /// </summary>
        private static byte[] Downsample(byte[] pixels, ref int width, ref int height)
        {
            int newWidth = (width + 1) / 2;
            int newHeight = (height + 1) / 2;

            var result = new byte[newWidth * newHeight * BytesPerPixel];

            for (int y = 0; y < newHeight; y++)
            {
                int y0 = y * 2;
                int y1 = Math.Min(y0 + 1, height - 1);

                for (int x = 0; x < newWidth; x++)
                {
                    int x0 = x * 2;
                    int x1 = Math.Min(x0 + 1, width - 1);

                    int a = ((y0 * width) + x0) * BytesPerPixel;
                    int b = ((y0 * width) + x1) * BytesPerPixel;
                    int c = ((y1 * width) + x0) * BytesPerPixel;
                    int d = ((y1 * width) + x1) * BytesPerPixel;

                    int target = ((y * newWidth) + x) * BytesPerPixel;

                    for (int channel = 0; channel < BytesPerPixel; channel++)
                    {
                        int sum = pixels[a + channel] + pixels[b + channel]
                                + pixels[c + channel] + pixels[d + channel];

                        result[target + channel] = (byte)(sum >> 2);
                    }
                }
            }

            width = newWidth;
            height = newHeight;

            return result;
        }
    }
}
