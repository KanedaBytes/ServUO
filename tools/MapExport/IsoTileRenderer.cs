using System;
using System.Collections.Generic;

namespace Server.Custom.MapExport
{
    /// <summary>
    /// Which of the two tile layers is being drawn.
    ///
    /// Map is the client's world - land and statics - and never changes, so it is cached under the
    /// renderer version and kept forever. Items is the shard's own furniture, which changes every
    /// time somebody decorates, so it is cached under the SNAPSHOT's identity instead and thrown
    /// away wholesale when a new one arrives. Baking the two together would mean a re-decorate
    /// invalidated a map cache that costs minutes to rebuild, for furniture that costs seconds.
    /// </summary>
    internal enum Layer
    {
        Map,
        Items
    }

    /// <summary>Which storeys a render keeps. The editor's floor slider, one value per stop.</summary>
    internal enum Floor
    {
        Ground,
        First,
        All
    }

    /// <summary>
    /// How high above the ground each floor stop cuts.
    ///
    /// Height ABOVE THE GROUND, never absolute Z: Britain's upper and lower town differ by about
    /// thirty Z, so one absolute threshold cannot suit both. UO's storey height is 20, which is
    /// where the defaults come from; both are overridable because this is the rule most likely to
    /// want an eyeball tune, and a rebuild is a poor way to try a number.
    ///
    /// WHAT "THE GROUND" IS took two goes. The first version used the land tile in the static's own
    /// column, and it deleted the smithy's back wall at the Ground stop. That wall stands at
    /// 1415,1553 where the land is the river bank at z -15, while the building's floor - which is
    /// LAND here, `0x040A wooden floor` - is at z 30 one tile east. So the rule measured a
    /// ground-floor wall as forty-five above its ground and threw it away, at both the Ground and
    /// the First stop. See IsoTileRenderer.GroundLevels.
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

        /// <summary>
        /// Who wrote each pixel, while an Items layer is being drawn.
        ///
        /// THIS IS HOW OCCLUSION STAYS EXACT. An item layer drawn on its own would put a forge on
        /// top of the wall in front of it, because nothing in a transparent overlay knows what is
        /// between the furniture and the viewer. So an item tile paints the WHOLE column - land,
        /// statics and items together, in one sorted pass - and records which pixels the items
        /// ended up owning. Only those are emitted. A bench in the tavern is then behind the
        /// tavern's wall, exactly as a static bench would be, and the map layer underneath shows
        /// through everywhere else.
        /// </summary>
        private byte[] _owner;

        private const byte OwnerNone = 0;
        private const byte OwnerWorld = 1;
        private const byte OwnerItem = 2;

        private WorldItems _items = null;

        private readonly int[] _ground = new int[9];

        private int[] _staticKeys = new int[64];
        private int[] _itemOrder = new int[8];
        private int[] _itemKeys = new int[8];

        // What --terrain-report counts. Cheap enough to keep always, so the report is a mode rather
        // than a build.
        private int _stretched;
        private int _terraced;

        public int StretchedTiles { get { return _stretched; } }

        public int TerracedTiles { get { return _terraced; } }

        public void ResetTerrainCounters()
        {
            _stretched = 0;
            _terraced = 0;
        }

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
            _items = WorldItems.None(facetHeight);
        }

        /// <summary>The world-item snapshot in force. Swapped when the shard writes a new one.</summary>
        public WorldItems Items
        {
            get { return _items; }
            set { _items = value ?? WorldItems.None(_facetHeight); }
        }

        public int MaxLevel { get { return _maxLevel; } }

        public int TileSize { get { return _tileSize; } }

        /// <summary>RGBA bytes for one tile, tileSize square. Never null.</summary>
        public byte[] Render(int level, int tileX, int tileY, Floor floor)
        {
            return Render(Layer.Map, level, tileX, tileY, floor);
        }

        /// <summary>
        /// RGBA bytes for one tile, tileSize square - or null for an Items layer with nothing in
        /// it, which is most of them. Null rather than a transparent buffer so the caller can skip
        /// writing a file at all: an empty item tile that reached disk would be tens of thousands
        /// of identical 70-byte PNGs.
        /// </summary>
        public byte[] Render(Layer layer, int level, int tileX, int tileY, Floor floor)
        {
            if (level < 0 || level > _maxLevel)
            {
                throw new ArgumentOutOfRangeException("level");
            }

            int divisor = IsoTransform.Divisor(level, _maxLevel);
            int span = _tileSize * divisor;

            int canvasX = tileX * span;
            int canvasY = tileY * span;

            if (layer == Layer.Items && !HasItems(canvasX, canvasY, span))
            {
                return null;
            }

            var scratch = new byte[span * span * BytesPerPixel];

            _owner = layer == Layer.Items ? new byte[span * span] : null;

            try
            {
                Paint(scratch, span, canvasX, canvasY, floor, layer);

                if (layer == Layer.Items)
                {
                    KeepItemsOnly(scratch, span);
                }
            }
            finally
            {
                _owner = null;
            }

            while (divisor > 1)
            {
                scratch = Halve(scratch, span);
                span /= 2;
                divisor /= 2;
            }

            return scratch;
        }

        /// <summary>
        /// Whether any item could reach this tile, before a pixel is touched.
        ///
        /// It walks the same footprint the render walks, so a tall sprite anchored outside the tile
        /// still counts - which is the whole reason the footprint is wider than the tile. Cheap: a
        /// dictionary lookup per column against a snapshot where nearly every column is empty.
        /// </summary>
        private bool HasItems(int canvasX, int canvasY, int span)
        {
            if (!_items.Any)
            {
                return false;
            }

            int dMin, dMax, eMin, eMax;

            IsoTransform.Footprint(
                canvasX, canvasY, span, _facetWidth, _facetHeight, out dMin, out dMax, out eMin, out eMax);

            for (int d = dMin; d <= dMax; d++)
            {
                int e = eMin;

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

                    if (_items.At(x, y) != null)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Blanks every pixel the items did not end up owning.</summary>
        private void KeepItemsOnly(byte[] target, int span)
        {
            for (int i = 0; i < _owner.Length; i++)
            {
                if (_owner[i] == OwnerItem)
                {
                    continue;
                }

                int at = i * BytesPerPixel;

                target[at] = 0;
                target[at + 1] = 0;
                target[at + 2] = 0;
                target[at + 3] = 0;
            }
        }

        private void Paint(byte[] target, int span, int canvasX, int canvasY, Floor floor, Layer layer)
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

                    PaintColumn(target, span, isoX0, isoY0, x, y, floor, layer);
                }
            }
        }

        private void PaintColumn(
            byte[] target, int span, int isoX0, int isoY0, int x, int y, Floor floor, Layer layer)
        {
            int anchorX = IsoTransform.IsoX(x, y) - isoX0;

            LandTile land = _tiles.GetLandTile(x, y);
            int landZ = land.Z;

            PaintLand(target, span, isoX0, isoY0, x, y, land, anchorX);

            StaticTile[] column = _tiles.GetStaticTiles(x, y);
            List<WorldItem> items = layer == Layer.Items ? _items.At(x, y) : null;

            // Gathered once per column rather than per static: nine lookups either way, but the
            // statics in a column share them.
            GroundLevels(x, y, _ground);

            int statics = Sort(column, landZ, floor);
            int shardItems = SortItems(items, landZ, floor);

            // ONE MERGED PASS, not statics then items. They interleave by the same key - a bench in
            // front of a wall has to be drawn after it and behind the wall on the near side - and
            // two passes would put every item in front of every static regardless of where it
            // stands. This is the same merge a client does; the two sources just happen to be a
            // .mul file and the shard's save.
            int si = 0;
            int ii = 0;

            while (si < statics || ii < shardItems)
            {
                bool takeItem;

                if (si >= statics)
                {
                    takeItem = true;
                }
                else if (ii >= shardItems)
                {
                    takeItem = false;
                }
                else
                {
                    takeItem = _itemKeys[ii] <= _staticKeys[si];
                }

                if (takeItem)
                {
                    WorldItem item = items[_itemOrder[ii++]];
                    Sprite sprite = _art.Static(item.ID, item.Hue);

                    if (sprite != null)
                    {
                        Blit(target, span, sprite,
                            anchorX, IsoTransform.IsoY(x, y, item.Z) - isoY0, OwnerItem);
                    }

                    continue;
                }

                StaticTile tile = column[_order[si++]];
                Sprite staticSprite = _art.Static(tile.ID, tile.Hue);

                if (staticSprite != null)
                {
                    Blit(target, span, staticSprite,
                        anchorX, IsoTransform.IsoY(x, y, tile.Z) - isoY0, OwnerWorld);
                }
            }
        }

        /// <summary>
        /// The items this floor keeps on one column, in draw order, by the same key the statics
        /// use. A world item IS a static as far as drawing goes - same art, same tiledata, same
        /// flags - so it sorts by the same rule and the floor rules apply to it unchanged.
        /// </summary>
        private int SortItems(List<WorldItem> items, int landZ, Floor floor)
        {
            if (items == null || items.Count == 0)
            {
                return 0;
            }

            if (_itemOrder.Length < items.Count)
            {
                _itemOrder = new int[items.Count];
                _itemKeys = new int[items.Count];
            }

            int count = 0;

            for (int i = 0; i < items.Count; i++)
            {
                ItemData data = TileData.ItemTable[items[i].ID & TileData.MaxItemValue];

                int groundZ = GroundFor(_ground, landZ, items[i].Z);

                if (!_rules.Keeps(floor, items[i].Z, groundZ, (data.Flags & TileFlag.Roof) != 0))
                {
                    continue;
                }

                int key = Key(items[i].Z, data);
                int at = count++;

                while (at > 0 && _itemKeys[at - 1] > key)
                {
                    _itemOrder[at] = _itemOrder[at - 1];
                    _itemKeys[at] = _itemKeys[at - 1];
                    at--;
                }

                _itemOrder[at] = i;
                _itemKeys[at] = key;
            }

            return count;
        }

        /// <summary>
        /// The land tile: a stretched texture where the client would stretch one, the flat 44x44
        /// diamond where it would not.
        ///
        /// THE RULE IS THE CLIENT'S AND IT IS NOT STATED ANYWHERE IN THIS TREE. Server/TileData.cs
        /// discards the land texture id outright (":219" and ":259", `bin.ReadInt16(); // skip 2
        /// bytes -- textureID`), Ultima/Textures.cs has no caller, and Ultima/Map.cs renders radar
        /// colours and never opens a texmap. So it is cited from ClassicUO's Land.ApplyStretch,
        /// which refuses to stretch when TexmapsLoader.GetValidRefEntry(TileData.TexID).Length is
        /// zero and otherwise stretches only when the sampled corners differ:
        ///
        ///     stretched  iff  TextureID != 0  and the texmap exists  and the corners differ
        ///
        /// BOTH PATHS ARE LOAD-BEARING. Only 4,085 of 16,384 land ids have a texture at all - just
        /// under a quarter - so a stretched-only renderer would blank three land tiles in four.
        /// A slope built from untextured ids therefore still terraces, exactly as it does in the
        /// client, and --terrain-report is the mode that says how much of a given hillside that is.
        ///
        /// THE FOUR CORNERS ARE THE SHARD'S OWN. Server/Map.cs:552-592 (GetAverageZ) samples
        /// (x,y), (x+1,y), (x,y+1) and (x+1,y+1) - the four lattice points around the tile - and
        /// every movement decision on this shard is made from them. Reading the same four means a
        /// slope is drawn from the numbers the walker walks, which is the same reason this tool
        /// reads Server.TileMatrix rather than a private .mul reader. GetAverageZ collapses them to
        /// a min/avg/top and we want them raw, so the samples are repeated rather than called.
        /// </summary>
        private void PaintLand(
            byte[] target, int span, int isoX0, int isoY0, int x, int y, LandTile land, int anchorX)
        {
            Sprite flat = _art.Land(land.ID);

            int zTop = land.Z;
            int zRight = LandZ(x + 1, y);
            int zLeft = LandZ(x, y + 1);
            int zBottom = LandZ(x + 1, y + 1);

            bool level = zTop == zRight && zTop == zLeft && zTop == zBottom;

            if (!level)
            {
                // Ultima.TileData, not Server.TileData - the ServUO copy throws the texture id away
                // at read time. A documented exception to "map data from Server, pixels from
                // Ultima": a texture id is art metadata, not map data, so Ultima is its home.
                int textureId = Ultima.TileData.LandTable[land.ID & 0x3FFF].TextureID;
                Sprite texture = _art.Texture(textureId);

                if (texture != null)
                {
                    BlitQuad(target, span, texture,
                        IsoTransform.IsoCornerX(x, y) - isoX0,
                        IsoTransform.IsoCornerY(x, y, zTop) - isoY0,
                        IsoTransform.IsoCornerX(x + 1, y) - isoX0,
                        IsoTransform.IsoCornerY(x + 1, y, zRight) - isoY0,
                        IsoTransform.IsoCornerX(x + 1, y + 1) - isoX0,
                        IsoTransform.IsoCornerY(x + 1, y + 1, zBottom) - isoY0,
                        IsoTransform.IsoCornerX(x, y + 1) - isoX0,
                        IsoTransform.IsoCornerY(x, y + 1, zLeft) - isoY0);

                    _stretched++;
                    return;
                }

                _terraced++;
            }

            if (flat != null)
            {
                Blit(target, span, flat, anchorX, IsoTransform.IsoY(x, y, zTop) - isoY0);
            }
        }

        /// <summary>
        /// The land heights around a column, which is what a storey is measured from.
        ///
        /// WHY A NEIGHBOURHOOD AND NOT THE COLUMN. A wall sits on the BOUNDARY of the floor it
        /// belongs to, and at a cliff edge the column under it is the drop rather than the
        /// building. The smithy's back wall is exactly that: the wall is at 1415,1553 over the
        /// river bank at z -15, and the building's floor is the wooden-floor LAND at z 30 in the
        /// very next column. Widening the reference by one tile is the smallest change that lets a
        /// wall belong to the building it is part of, and one tile is all the case needs - a wall
        /// is never more than a tile from its own floor.
        ///
        /// LAND ONLY, NEVER A SURFACE STATIC, and that is the part worth being careful about. The
        /// obvious refinement - "the highest surface at or below this static" - collapses the whole
        /// idea: every item stands on some floor, so every item would measure as storey zero and
        /// the slider would stop doing anything at all. Land is the terrain and statics are the
        /// building; keeping that line is what makes a storey countable. A deck of surface statics
        /// over water is the miss, and it errs towards showing too much, which is the safe way to
        /// be wrong about a wall.
        ///
        /// THE CLIENT DOES NOT SETTLE THIS. Its own roof-hiding works off the PLAYER's Z, not a
        /// per-tile classification, so there is no client rule to copy here - only the shard's own
        /// habit of treating land as the surface a mobile stands on (Server/Map.cs GetAverageZ).
        /// Said plainly rather than dressed up as fidelity.
        /// </summary>
        private void GroundLevels(int x, int y, int[] into)
        {
            int at = 0;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    into[at++] = LandZ(x + dx, y + dy);
                }
            }
        }

        /// <summary>
        /// The ground a static at this Z is standing above: the highest land nearby that is not
        /// above it. Capped that way so a cliff TOP behind a building does not become the reference
        /// for something standing at its foot. With nothing at or below, the column's own land.
        /// </summary>
        private static int GroundFor(int[] levels, int ownLandZ, int staticZ)
        {
            int best = Int32.MinValue;

            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] <= staticZ && levels[i] > best)
                {
                    best = levels[i];
                }
            }

            return best == Int32.MinValue ? ownLandZ : best;
        }

        /// <summary>
        /// A corner's land Z. Off the facet TileMatrix hands back a zeroed block
        /// (Server/TileMatrix.cs:335-340), so the east and south edges would read a corner at Z 0
        /// and shear the last row into the sea. Clamping to the edge tile makes those tiles level
        /// instead, which is what they look like anyway.
        /// </summary>
        private int LandZ(int x, int y)
        {
            // Clamped at both ends since GroundLevels samples x-1 and y-1: TileMatrix returns a
            // zeroed block off the facet, and a phantom Z 0 next to a mountain would read as ground.
            if (x < 0) { x = 0; }
            if (y < 0) { y = 0; }
            if (x >= _facetWidth) { x = _facetWidth - 1; }
            if (y >= _facetHeight) { y = _facetHeight - 1; }

            return _tiles.GetLandTile(x, y).Z;
        }

        /// <summary>
        /// Maps a texture onto the tile's four projected corners, as two triangles.
        ///
        /// Nothing in this repo rasterises anything, so this is written rather than reused. Two
        /// triangles - (N,E,S) and (N,S,W) - with barycentric interpolation of the texture
        /// coordinates, which is exact for an affine map and close enough over 44 pixels for the
        /// bilinear patch the quad really is.
        ///
        /// EDGE TESTS ARE INCLUSIVE, which is the detail that decides whether the ground has holes
        /// in it. Adjacent tiles share corner vertices exactly and at integer pixels, so a
        /// >= 0 test on all three barycentric coordinates covers every shared edge from both sides:
        /// some pixels are written twice, which costs nothing because land is opaque and drawn
        /// first, and none are written zero times, which would be a one-pixel crack running the
        /// length of every hillside.
        ///
        /// The quad can self-intersect where corners differ sharply - a 30z step is 120 pixels, and
        /// UO cliffs do that. The two triangles then overlap or gap, which is what the client shows
        /// too; smeared cliffs are a UO look, not a bug being introduced here.
        ///
        /// Texture (0,0) goes to the north corner and u grows with world x, so the texture's axes
        /// follow the world's. If a directional texture - a path, a road - runs visibly the wrong
        /// way, this assignment is the thing to transpose.
        /// </summary>
        private void BlitQuad(
            byte[] target, int span, Sprite texture,
            int nx, int ny, int ex, int ey, int sx, int sy, int wx, int wy)
        {
            int size = texture.Width;

            Triangle(target, span, texture, _owner,
                nx, ny, 0, 0,
                ex, ey, size, 0,
                sx, sy, size, size);

            Triangle(target, span, texture, _owner,
                nx, ny, 0, 0,
                sx, sy, size, size,
                wx, wy, 0, size);
        }

        private static void Triangle(
            byte[] target, int span, Sprite texture, byte[] owner,
            int ax, int ay, int au, int av,
            int bx, int by, int bu, int bv,
            int cx, int cy, int cu, int cv)
        {
            int area = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));

            if (area == 0)
            {
                return; // degenerate: the three corners are collinear, so there is no surface
            }

            int left = Math.Max(0, Math.Min(ax, Math.Min(bx, cx)));
            int right = Math.Min(span - 1, Math.Max(ax, Math.Max(bx, cx)));
            int top = Math.Max(0, Math.Min(ay, Math.Min(by, cy)));
            int bottom = Math.Min(span - 1, Math.Max(ay, Math.Max(by, cy)));

            if (left > right || top > bottom)
            {
                return;
            }

            // Winding is whatever the corner heights made it, so the sign is normalised rather than
            // assumed - an inverted quad on a cliff face is a legitimate shape here.
            int sign = area < 0 ? -1 : 1;
            int magnitude = area < 0 ? -area : area;

            for (int py = top; py <= bottom; py++)
            {
                for (int px = left; px <= right; px++)
                {
                    int w0 = sign * (((bx - ax) * (py - ay)) - ((by - ay) * (px - ax)));
                    int w1 = sign * (((cx - bx) * (py - by)) - ((cy - by) * (px - bx)));
                    int w2 = sign * (((ax - cx) * (py - cy)) - ((ay - cy) * (px - cx)));

                    if (w0 < 0 || w1 < 0 || w2 < 0)
                    {
                        continue;
                    }

                    // w1 weights a, w2 weights b, w0 weights c - the barycentric coordinate of a
                    // vertex is the area of the triangle opposite it.
                    int u = ((w1 * au) + (w2 * bu) + (w0 * cu)) / magnitude;
                    int v = ((w1 * av) + (w2 * bv) + (w0 * cv)) / magnitude;

                    if (u < 0) { u = 0; }
                    if (v < 0) { v = 0; }
                    if (u >= texture.Width) { u = texture.Width - 1; }
                    if (v >= texture.Height) { v = texture.Height - 1; }

                    ushort pixel = texture.Pixels[(v * texture.Width) + u];

                    int destination = ((py * span) + px) * BytesPerPixel;

                    int r = (pixel >> 10) & 0x1F;
                    int g = (pixel >> 5) & 0x1F;
                    int b = pixel & 0x1F;

                    target[destination] = (byte)((r << 3) | (r >> 2));
                    target[destination + 1] = (byte)((g << 3) | (g >> 2));
                    target[destination + 2] = (byte)((b << 3) | (b >> 2));
                    target[destination + 3] = 255;

                    if (owner != null)
                    {
                        owner[(py * span) + px] = OwnerWorld;
                    }
                }
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
            if (column == null || column.Length == 0)
            {
                return 0;
            }

            if (_order.Length < column.Length)
            {
                _order = new int[column.Length];
                _staticKeys = new int[column.Length];
            }

            int count = 0;

            for (int i = 0; i < column.Length; i++)
            {
                ItemData data = TileData.ItemTable[column[i].ID & TileData.MaxItemValue];

                int groundZ = GroundFor(_ground, landZ, column[i].Z);

                if (!_rules.Keeps(floor, column[i].Z, groundZ, (data.Flags & TileFlag.Roof) != 0))
                {
                    continue;
                }

                int key = Key(column[i].Z, data);
                int at = count++;

                // The key is carried alongside the index rather than recomputed per comparison -
                // the merge below needs it anyway, and the old form looked up tiledata twice per
                // step of the insertion.
                while (at > 0 && _staticKeys[at - 1] > key)
                {
                    _order[at] = _order[at - 1];
                    _staticKeys[at] = _staticKeys[at - 1];
                    at--;
                }

                _order[at] = i;
                _staticKeys[at] = key;
            }

            return count;
        }

        private static int Key(int z, ItemData data)
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
            return ((z + threshold) << 2) | threshold;
        }

        /// <summary>
        /// A sprite's bottom centre goes on the anchor - the rule from Ultima/Multis.cs:505-507.
        /// Source-over with a 1-bit alpha, because that is all ARGB1555 art carries.
        /// </summary>
        private void Blit(byte[] target, int span, Sprite sprite, int anchorX, int anchorY)
        {
            Blit(target, span, sprite, anchorX, anchorY, OwnerWorld);
        }

        private void Blit(byte[] target, int span, Sprite sprite, int anchorX, int anchorY, byte owner)
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

                        if (_owner != null)
                        {
                            _owner[destination / BytesPerPixel] = owner;
                        }
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
