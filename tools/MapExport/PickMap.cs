using System;
using System.IO;
using System.IO.Compression;

namespace Server.Custom.MapExport
{
    /// <summary>
    /// Which world tile, and at what standing Z, each pixel of a rendered tile belongs to.
    ///
    /// WHY THIS EXISTS. The isometric projection has no inverse. A screen pixel names a world tile
    /// only once a Z is assumed, and assuming the ground plane is wrong by z*4/44 tiles - about 2.7
    /// where Britain stands, near z 30. That is why the editor refused to place or drag anything in
    /// the art view: a waypoint dropped three tiles from where it was clicked would look right and
    /// be wrong. A pick map turns the inverse into a lookup, by recording the answer while the
    /// renderer is drawing and therefore already knows it.
    ///
    /// It is written by the SAME painter's pass and the SAME floor filter as the picture, at the
    /// same two pixel-write sites (IsoTileRenderer.Blit and IsoTileRenderer.Triangle). So a click
    /// lands on the tile the eye sees - the roof at the All stop, and the floor under it at Ground,
    /// because a roof the floor filter dropped was never drawn and so was never recorded.
    ///
    /// ONE RESOLUTION ANSWERS EVERY ZOOM. A pick map is rendered only at the deepest level, 1:1,
    /// and it is read by turning a screen point into a FACET-GLOBAL CANVAS PIXEL, which is the same
    /// number at every zoom. So there is no second copy at 1:2 and no decimation question - which
    /// matters, because world coordinates cannot be box-averaged the way colour can, and any rule
    /// for picking one of four would be a lie about the other three.
    ///
    /// ---
    ///
    /// THE FORMAT, gzip-compressed whole and served with Content-Encoding: gzip, so the browser
    /// decompresses it natively and fetch().arrayBuffer() hands over the exact bytes:
    ///
    ///      0  "GGPK"                 magic
    ///      4  u8   format            Format, below
    ///      5  u8   reserved          0
    ///      6  u16  size              pixels per side
    ///      8  i32  baseX             the world x that dx 0 means
    ///     12  i32  baseY
    ///     16  size*size  kind        0 nothing drawn, 1 land, 2 static, 3 world item
    ///        +size*size  dx          worldX - baseX
    ///        +size*size  dy          worldY - baseY
    ///        +size*size  z           standing Z + 128
    ///
    /// NOT A PNG, for two reasons. Getting EXACT bytes back out of a PNG in a browser means a
    /// canvas round trip, and that path premultiplies alpha and applies colour management - and a
    /// pick map that is nearly right is a waypoint three tiles out. And an RGBA8 pixel cannot hold
    /// what is needed anyway: world x is 13 bits, y is 12 and Z is 8, which is 33.
    ///
    /// PLANAR, NOT INTERLEAVED, because each plane is piecewise constant over a drawn sprite - one
    /// land diamond is one value in all four - and deflate finds those runs only when they are
    /// adjacent. Interleaving shreds every run into stripes of four.
    ///
    /// Z IS EXACT IN A BYTE because Z is an sbyte throughout UO, so +128 covers -128..127 with
    /// nothing left over and nothing to clamp.
    ///
    /// dx AND dy FIT IN A BYTE, and the writer refuses rather than wrapping if they ever do not. A
    /// 256-pixel tile is about twelve world tiles across, but IsoTransform.Footprint widens the
    /// columns it walks by ZLift and MaxSpriteHeight - 512 iso pixels each - so a tall sprite
    /// anchored well outside the tile can still paint into it. The real span works out around
    /// sixty tiles per axis, four times inside the byte; the refusal is there because a wrapped dx
    /// would not look like a failure, it would look like a pick three hundred tiles away.
    /// </summary>
    internal sealed class PickMap
    {
        /// <summary>
        /// The sidecar's own format, independent of TileServer.Version.
        ///
        /// The cache path carries the RENDERER version, which orphans a .pick whenever the art
        /// changes - but this format could change while no pixel does, and a file cached under a
        /// version that is still current would then be stale forever. So the bridge reads this byte
        /// out of a cache hit and re-renders when it is not the one the handshake advertised.
        /// </summary>
        public const byte Format = 1;

        public const byte KindNone = 0;
        public const byte KindLand = 1;
        public const byte KindStatic = 2;
        public const byte KindItem = 3;

        /// <summary>The header, in bytes, before the first plane.</summary>
        public const int HeaderSize = 16;

        private static readonly byte[] Magic = { (byte)'G', (byte)'G', (byte)'P', (byte)'K' };

        private readonly int _size;
        private readonly byte[] _kind;
        private readonly int[] _x;
        private readonly int[] _y;
        private readonly int[] _z;

        public PickMap(int size)
        {
            if (size <= 0 || size > 0xFFFF)
            {
                throw new ArgumentOutOfRangeException("size");
            }

            _size = size;
            _kind = new byte[size * size];
            _x = new int[size * size];
            _y = new int[size * size];
            _z = new int[size * size];
        }

        public int Size { get { return _size; } }

        /// <summary>
        /// Records a pixel. Called from the two blit inner loops, so it does no work a caller could
        /// have hoisted: the cell is constant for a whole sprite and is built once per sprite.
        /// </summary>
        public void Set(int index, PickCell cell)
        {
            _kind[index] = cell.Kind;
            _x[index] = cell.X;
            _y[index] = cell.Y;
            _z[index] = cell.Z;
        }

        /// <summary>What is recorded at a pixel, for --pick and for the tests.</summary>
        public PickCell At(int pixelX, int pixelY)
        {
            int index = (pixelY * _size) + pixelX;

            return new PickCell(_x[index], _y[index], _z[index], _kind[index]);
        }

        public static string KindName(byte kind)
        {
            switch (kind)
            {
                case KindLand: return "land";
                case KindStatic: return "static";
                case KindItem: return "item";
                default: return "none";
            }
        }

        /// <summary>
        /// Writes the file, creating the directory. Returns the compressed size on disk.
        ///
        /// The whole file is gzipped rather than each plane, so the planes are adjacent in the
        /// stream and a run that spans two of them costs nothing extra.
        /// </summary>
        public long Write(string path)
        {
            int baseX, baseY;
            Bounds(out baseX, out baseY);

            var raw = new byte[HeaderSize + (_kind.Length * 4)];

            Array.Copy(Magic, 0, raw, 0, Magic.Length);
            raw[4] = Format;
            raw[5] = 0;
            raw[6] = (byte)(_size >> 8);
            raw[7] = (byte)_size;
            WriteInt32(raw, 8, baseX);
            WriteInt32(raw, 12, baseY);

            int kindAt = HeaderSize;
            int dxAt = kindAt + _kind.Length;
            int dyAt = dxAt + _kind.Length;
            int zAt = dyAt + _kind.Length;

            for (int i = 0; i < _kind.Length; i++)
            {
                if (_kind[i] == KindNone)
                {
                    // Nothing drawn here. The three coordinate planes stay 0 rather than carrying
                    // a sentinel: `kind` is the sentinel, and zeroes compress to nothing.
                    continue;
                }

                int dx = _x[i] - baseX;
                int dy = _y[i] - baseY;

                if (dx < 0 || dx > 255 || dy < 0 || dy > 255)
                {
                    throw new InvalidOperationException(String.Format(
                        "A pick tile spans more than 256 world tiles ({0},{1} against base {2},{3}). "
                            + "See the note on dx/dy in PickMap.",
                        _x[i], _y[i], baseX, baseY));
                }

                raw[kindAt + i] = _kind[i];
                raw[dxAt + i] = (byte)dx;
                raw[dyAt + i] = (byte)dy;
                raw[zAt + i] = (byte)(_z[i] + 128);
            }

            string directory = Path.GetDirectoryName(path);

            if (!String.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var gzip = new GZipStream(file, CompressionMode.Compress, true))
                {
                    gzip.Write(raw, 0, raw.Length);
                }
            }

            return new FileInfo(path).Length;
        }

        /// <summary>
        /// The lowest world x and y anything was recorded at. An empty map bases at 0,0 - nothing
        /// reads a plane whose kind is None, so the base is arbitrary there.
        /// </summary>
        private void Bounds(out int baseX, out int baseY)
        {
            baseX = Int32.MaxValue;
            baseY = Int32.MaxValue;

            for (int i = 0; i < _kind.Length; i++)
            {
                if (_kind[i] == KindNone)
                {
                    continue;
                }

                if (_x[i] < baseX) { baseX = _x[i]; }
                if (_y[i] < baseY) { baseY = _y[i]; }
            }

            if (baseX == Int32.MaxValue)
            {
                baseX = 0;
                baseY = 0;
            }
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }

    /// <summary>
    /// One pixel's answer. A struct passed by value because it is built once per SPRITE and read
    /// once per pixel of it - a sprite covers one world column at one Z, so every pixel it owns
    /// carries the same three numbers.
    /// </summary>
    internal struct PickCell
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;
        public readonly byte Kind;

        public PickCell(int x, int y, int z, byte kind)
        {
            X = x;
            Y = y;
            Z = z;
            Kind = kind;
        }
    }
}
