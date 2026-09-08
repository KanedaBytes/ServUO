using System;

namespace Server.Custom.MapExport
{
    /// <summary>Which storeys a render keeps. The editor's floor slider, one value per stop.</summary>
    internal enum Floor
    {
        Ground,
        First,
        All
    }

    /// <summary>
    /// How high above the land surface each floor stop cuts.
    ///
    /// Height ABOVE THE LAND, never absolute Z: Britain's upper and lower town differ by about
    /// thirty Z, so one absolute threshold cannot suit both. UO's storey height is 20, which is
    /// where the defaults come from; both are overridable because this is the rule most likely to
    /// want an eyeball tune, and a rebuild is a poor way to try a number.
    /// </summary>
    internal sealed class FloorRules
    {
        public const int DefaultGround = 20;
        public const int DefaultFirst = 40;

        public readonly int Ground;
        public readonly int First;

        public FloorRules(int ground, int first)
        {
            Ground = ground;
            First = first;
        }

        public static FloorRules Default
        {
            get { return new FloorRules(DefaultGround, DefaultFirst); }
        }

        /// <summary>Keeps a static, given the land surface under it.</summary>
        public bool Keeps(Floor floor, int staticZ, int landZ, bool roof)
        {
            if (floor == Floor.All)
            {
                return true;
            }

            // A roof is dropped outright below the top stop. The height cutoff alone would keep a
            // one-storey roof at landZ+20 on the First stop, which is exactly the thing the slider
            // is being moved to get rid of.
            if (roof)
            {
                return false;
            }

            int limit = floor == Floor.Ground ? Ground : First;

            return staticZ < landZ + limit;
        }

        public static bool TryParse(string name, out Floor floor)
        {
            switch ((name ?? String.Empty).ToLowerInvariant())
            {
                case "ground":
                    floor = Floor.Ground;
                    return true;
                case "first":
                    floor = Floor.First;
                    return true;
                case "all":
                    floor = Floor.All;
                    return true;
            }

            floor = Floor.All;
            return false;
        }

        public static string Name(Floor floor)
        {
            switch (floor)
            {
                case Floor.Ground:
                    return "ground";
                case Floor.First:
                    return "first";
                default:
                    return "all";
            }
        }
    }

    /// <summary>
    /// Renders one isometric tile of the facet-global grid.
    ///
    /// Self-contained by design: a tile knows its own canvas rectangle, works out which world
    /// columns can reach it, and draws them. There is no whole-facet buffer and no batch pass,
    /// because a whole-facet isometric canvas is 61 gigapixels (see IsoTransform).
    ///
    /// A coarse tile is rendered at 1:1 into a scratch buffer of tileSize*divisor and box-
    /// downsampled, rather than by sampling every Nth column - a road one tile wide has to survive
    /// zooming out, which is the same reason TilePyramid.Downsample averages instead of picking.
    ///
    /// Map data comes from Server.TileMatrix and flags from Server.TileData, keeping MapExport's
    /// founding rule that the tiles can never disagree with what the shard thinks the map is.
    /// Ultima supplies the pixels and nothing else.
    ///
    /// NOT THREAD SAFE, and it cannot be: Server.TileMatrix keeps its decompression buffers in
    /// static fields, so two renders at once corrupt each other. The serve loop is serial.
    /// </summary>
    internal sealed class IsoTileRenderer
    {
        private const int BytesPerPixel = 4;

        private readonly TileMatrix _tiles;
        private readonly ArtCache _art;
        private readonly FloorRules _rules;
        private readonly int _facetWidth;
        private readonly int _facetHeight;
        private readonly int _tileSize;
        private readonly int _maxLevel;

        private int[] _order = new int[64];

        public IsoTileRenderer(
            TileMatrix tiles,
            ArtCache art,
            FloorRules rules,
            int facetWidth,
            int facetHeight,
            int tileSize)
        {
            _tiles = tiles;
            _art = art;
            _rules = rules;
            _facetWidth = facetWidth;
            _facetHeight = facetHeight;
            _tileSize = tileSize;
            _maxLevel = IsoTransform.MaxLevel(facetWidth, facetHeight, tileSize);
        }

        public int MaxLevel { get { return _maxLevel; } }

        public int TileSize { get { return _tileSize; } }

        /// <summary>RGBA bytes for one tile, tileSize square. Never null.</summary>
        public byte[] Render(int level, int tileX, int tileY, Floor floor)
        {
            if (level < 0 || level > _maxLevel)
            {
                throw new ArgumentOutOfRangeException("level");
            }

            int divisor = IsoTransform.Divisor(level, _maxLevel);
            int span = _tileSize * divisor;

            int canvasX = tileX * span;
            int canvasY = tileY * span;

            var scratch = new byte[span * span * BytesPerPixel];

            Paint(scratch, span, canvasX, canvasY, floor);

            while (divisor > 1)
            {
                scratch = Halve(scratch, span);
                span /= 2;
                divisor /= 2;
            }

            return scratch;
        }

        private void Paint(byte[] target, int span, int canvasX, int canvasY, Floor floor)
        {
            int originX = IsoTransform.OriginX(_facetHeight);
            int originY = IsoTransform.OriginY();

            int isoX0 = canvasX + originX;
            int isoY0 = canvasY + originY;

            int dMin, dMax, eMin, eMax;

            IsoTransform.Footprint(
                canvasX, canvasY, span, _facetWidth, _facetHeight, out dMin, out dMax, out eMin, out eMax);

            // Painter's order: larger (x + y) is nearer the viewer, so diagonals ascending puts
            // the near side down last. Within a diagonal the columns sit side by side, so their
            // order only matters where a tall sprite leans over its neighbour - which the static
            // sort below settles for the common case.
            for (int d = dMin; d <= dMax; d++)
            {
                int e = eMin;

                // x = (d + e) / 2 is only whole where d and e share a parity.
                if (((d + e) & 1) != 0)
                {
                    e++;
                }

                for (; e <= eMax; e += 2)
                {
                    int x = (d + e) / 2;
                    int y = (d - e) / 2;

                    if (x < 0 || y < 0 || x >= _facetWidth || y >= _facetHeight)
                    {
                        continue;
                    }

                    PaintColumn(target, span, isoX0, isoY0, x, y, floor);
                }
            }
        }

        private void PaintColumn(byte[] target, int span, int isoX0, int isoY0, int x, int y, Floor floor)
        {
            int anchorX = IsoTransform.IsoX(x, y) - isoX0;

            LandTile land = _tiles.GetLandTile(x, y);
            int landZ = land.Z;

            Sprite ground = _art.Land(land.ID);

            if (ground != null)
            {
                Blit(target, span, ground, anchorX, IsoTransform.IsoY(x, y, landZ) - isoY0);
            }

            StaticTile[] column = _tiles.GetStaticTiles(x, y);

            if (column == null || column.Length == 0)
            {
                return;
            }

            int count = Sort(column, landZ, floor);

            for (int i = 0; i < count; i++)
            {
                StaticTile tile = column[_order[i]];
                Sprite sprite = _art.Static(tile.ID, tile.Hue);

                if (sprite == null)
                {
                    continue;
                }

                Blit(target, span, sprite, anchorX, IsoTransform.IsoY(x, y, tile.Z) - isoY0);
            }
        }

        /// <summary>
        /// Fills _order with the indices of the statics this floor keeps, in draw order.
        ///
        /// The key is Ultima's own MTile.CompareTo (Ultima/TileMatrix.cs:769-817): Z plus a
        /// threshold that counts a non-zero height and a non-background flag, then the threshold
        /// itself, then file order. Insertion sort because a column is a handful of tiles and the
        /// sort has to be stable on that last term.
        /// </summary>
        private int Sort(StaticTile[] column, int landZ, Floor floor)
        {
            if (_order.Length < column.Length)
            {
                _order = new int[column.Length];
            }

            int count = 0;

            for (int i = 0; i < column.Length; i++)
            {
                ItemData data = TileData.ItemTable[column[i].ID & TileData.MaxItemValue];

                if (!_rules.Keeps(floor, column[i].Z, landZ, (data.Flags & TileFlag.Roof) != 0))
                {
                    continue;
                }

                int key = Key(column[i], data);
                int at = count++;

                while (at > 0)
                {
                    int previous = _order[at - 1];
                    ItemData other = TileData.ItemTable[column[previous].ID & TileData.MaxItemValue];

                    if (Key(column[previous], other) <= key)
                    {
                        break;
                    }

                    _order[at] = previous;
                    at--;
                }

                _order[at] = i;
            }

            return count;
        }

        private static int Key(StaticTile tile, ItemData data)
        {
            int threshold = 0;

            if (data.Height > 0)
            {
                threshold++;
            }

            // Server.ItemData has no Background property (Ultima's does); the flag is the same bit.
            if ((data.Flags & TileFlag.Background) == 0)
            {
                threshold++;
            }

            // Z plus threshold is the primary term and threshold breaks its ties, so packing them
            // into one int keeps the comparison a single subtraction, as the original's does.
            return ((tile.Z + threshold) << 2) | threshold;
        }

        /// <summary>
        /// A sprite's bottom centre goes on the anchor - the rule from Ultima/Multis.cs:505-507.
        /// Source-over with a 1-bit alpha, because that is all ARGB1555 art carries.
        /// </summary>
        private static void Blit(byte[] target, int span, Sprite sprite, int anchorX, int anchorY)
        {
            int left = anchorX - (sprite.Width / 2);
            int top = anchorY - sprite.Height;

            if (left >= span || top >= span || left + sprite.Width <= 0 || top + sprite.Height <= 0)
            {
                return;
            }

            int x0 = left < 0 ? -left : 0;
            int y0 = top < 0 ? -top : 0;
            int x1 = Math.Min(sprite.Width, span - left);
            int y1 = Math.Min(sprite.Height, span - top);

            for (int y = y0; y < y1; y++)
            {
                int source = y * sprite.Width;
                int destination = (((top + y) * span) + left + x0) * BytesPerPixel;

                for (int x = x0; x < x1; x++)
                {
                    ushort pixel = sprite.Pixels[source + x];

                    if (pixel != 0)
                    {
                        // 5 bits to 8: (v << 3) | (v >> 2), so 31 lands on 255 rather than 248 -
                        // the same expansion RadarColors.Expand uses.
                        int r = (pixel >> 10) & 0x1F;
                        int g = (pixel >> 5) & 0x1F;
                        int b = pixel & 0x1F;

                        target[destination] = (byte)((r << 3) | (r >> 2));
                        target[destination + 1] = (byte)((g << 3) | (g >> 2));
                        target[destination + 2] = (byte)((b << 3) | (b >> 2));
                        target[destination + 3] = 255;
                    }

                    destination += BytesPerPixel;
                }
            }
        }

        /// <summary>
        /// 2x2 box average, weighted by alpha so a transparent pixel contributes no colour.
        ///
        /// Straight averaging would pull the black of an untouched pixel into the edge of every
        /// sprite beside it, which shows up as a dark fringe around every building at 1:2.
        /// </summary>
        private static byte[] Halve(byte[] source, int span)
        {
            int half = span / 2;
            var target = new byte[half * half * BytesPerPixel];

            for (int y = 0; y < half; y++)
            {
                for (int x = 0; x < half; x++)
                {
                    int a = (((y * 2) * span) + (x * 2)) * BytesPerPixel;
                    int b = a + BytesPerPixel;
                    int c = a + (span * BytesPerPixel);
                    int d = c + BytesPerPixel;

                    int alpha = source[a + 3] + source[b + 3] + source[c + 3] + source[d + 3];
                    int offset = ((y * half) + x) * BytesPerPixel;

                    if (alpha == 0)
                    {
                        continue;
                    }

                    for (int channel = 0; channel < 3; channel++)
                    {
                        int sum = (source[a + channel] * source[a + 3])
                                + (source[b + channel] * source[b + 3])
                                + (source[c + channel] * source[c + 3])
                                + (source[d + channel] * source[d + 3]);

                        target[offset + channel] = (byte)(sum / alpha);
                    }

                    target[offset + 3] = (byte)(alpha / 4);
                }
            }

            return target;
        }
    }
}
