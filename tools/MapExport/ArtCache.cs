using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Server.Custom.MapExport
{
    /// <summary>One decoded art tile: ARGB1555 pixels, 0 meaning transparent.</summary>
    internal sealed class Sprite
    {
        public readonly int Width;
        public readonly int Height;
        public readonly ushort[] Pixels;

        public Sprite(int width, int height, ushort[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }
    }

    /// <summary>
    /// Land and static art, decoded once and hued on our own pixels.
    ///
    /// THIS IS THE ONLY PLACE THE TOOL TOUCHES SYSTEM.DRAWING. Ultima.Art hands back a
    /// System.Drawing.Bitmap and offers no decoded-pixel path (GetRawStatic returns the undecoded
    /// MUL blob), so the Bitmap is unavoidable at the read boundary - but it stops here. Everything
    /// downstream works on ushort[], which is what keeps PngWriter's stated "no imaging dependency"
    /// position worth having, and what makes swapping in a hand-written decoder later a one-class
    /// change rather than a rewrite.
    ///
    /// Two landmines in Ultima.Art, both avoided rather than worked around:
    ///
    ///   - Hue.ApplyTo MUTATES the bitmap it is given, and Art.GetStatic hands back the one out of
    ///     its own cache (Ultima/Art.cs:356). Hueing through it would corrupt that id for every
    ///     later caller. So ApplyTo is never called: we copy the ramp out of Hues and remap our own
    ///     pixels, which is three lines and has no shared state to break.
    ///   - Art.GetStatic does GetLegalItemID(index) + 0x4000 and then indexes arrays of 0xFFFF
    ///     (Ultima/Art.cs:322-341), so an id at or above 0xBFFF throws. Server.TileMatrix does not
    ///     clamp ids the way Ultima.TileMatrix does, so the guard has to be on this side.
    ///
    /// Files.CacheData is turned off by the caller, so Ultima.Art holds no Bitmap[0xFFFF] of its
    /// own. In a long-lived --serve process that array is the difference between a few megabytes of
    /// decoded sprites and tens of thousands of live GDI bitmaps.
    /// </summary>
    internal sealed class ArtCache
    {
        /// <summary>Ultima.Art's arrays are 0xFFFF long and it adds 0x4000 before indexing them.</summary>
        private const int MaxStaticId = 0xFFFF - 0x4000;

        /// <summary>Land 0x02 is the client's "draw nothing here" tile.</summary>
        private const int NoDrawLand = 0x02;

        private readonly Dictionary<long, Sprite> _sprites = new Dictionary<long, Sprite>();

        /// <summary>
        /// Texmaps, in their own table rather than sharing the sprite one.
        ///
        /// Key would collide otherwise: it packs id, hue and one land flag, and a texture id lives
        /// in the same 0..0x3FFF space a land id does with no bit left to tell them apart. A
        /// second dictionary is also the honest shape - a LandTile carries no hue, so a texture is
        /// keyed by nothing but its id.
        /// </summary>
        private readonly Dictionary<int, Sprite> _textures = new Dictionary<int, Sprite>();

        private int _misses;

        public int Count { get { return _sprites.Count + _textures.Count; } }

        public int Decoded { get { return _misses; } }

        /// <summary>The land diamond for a land id, or null where nothing should be drawn.</summary>
        public Sprite Land(int id)
        {
            id &= 0x3FFF;

            if (id == NoDrawLand)
            {
                return null;
            }

            return Get(Key(id, 0, true), id, 0, true);
        }

        /// <summary>A static's sprite at its hue, or null where the id has no usable art.</summary>
        public Sprite Static(int id, int hue)
        {
            if (id < 0 || id > MaxStaticId)
            {
                return null;
            }

            return Get(Key(id, hue < 0 ? 0 : hue, false), id, hue < 0 ? 0 : hue, false);
        }

        /// <summary>
        /// The texmap for a land tile's TextureID, or null where there is none.
        ///
        /// THE BITMAP IS NOT DISPOSED, and that is the opposite of what Decode does for art. The
        /// two cache in opposite directions: Art stores into m_Cache only when Files.CacheData is
        /// TRUE (Ultima/Art.cs:354-361), and we turn that off, so an art bitmap is ours to dispose.
        /// Textures stores when CacheData is FALSE (Ultima/Textures.cs:182-189) - inverted, almost
        /// certainly by accident upstream - so the bitmap handed back here belongs to
        /// Textures.m_Cache and disposing it would poison that id for every later tile. Bounded
        /// anyway: 4,116 valid entries, about 46 MB, for the life of a --serve process.
        ///
        /// Texmap pixels are never transparent - GetTexture ORs in the alpha bit on every one
        /// (Ultima/Textures.cs:174) - so the quad rasteriser needs no transparency test. Land is
        /// the bottom layer and everything else is painted over it.
        /// </summary>
        public Sprite Texture(int textureId)
        {
            if (textureId <= 0)
            {
                return null;
            }

            Sprite sprite;

            if (_textures.TryGetValue(textureId, out sprite))
            {
                return sprite;
            }

            sprite = DecodeTexture(textureId);
            _textures[textureId] = sprite; // null is cached too, so a missing id is asked once
            _misses++;
            return sprite;
        }

        private static Sprite DecodeTexture(int textureId)
        {
            Bitmap bitmap;

            try
            {
                if (!Ultima.Textures.TestTexture(textureId))
                {
                    return null;
                }

                bitmap = Ultima.Textures.GetTexture(textureId);
            }
            catch (Exception)
            {
                return null;
            }

            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return null;
            }

            try
            {
                // 64x64 or 128x128, decided by the texidx entry's `extra` field. Read, never
                // assumed: 555 of the 4,116 valid entries are the larger size.
                return new Sprite(
                    bitmap.Width, bitmap.Height, ReadPixels(bitmap, bitmap.Width, bitmap.Height));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Ids reach 0xFFFF and hues reach 3000, so the id needs 20 bits of clearance above the hue
        // and the land flag. Packed rather than a tuple because net48 has no ValueTuple in the box.
        private static long Key(int id, int hue, bool land)
        {
            return ((long)id << 20) | ((long)hue << 1) | (land ? 1L : 0L);
        }

        private Sprite Get(long key, int id, int hue, bool land)
        {
            Sprite sprite;

            if (_sprites.TryGetValue(key, out sprite))
            {
                return sprite;
            }

            sprite = Decode(id, hue, land);
            _sprites[key] = sprite; // null is cached too - a missing id must not be retried per tile
            _misses++;
            return sprite;
        }

        private static Sprite Decode(int id, int hue, bool land)
        {
            Bitmap bitmap;

            try
            {
                bitmap = land ? Ultima.Art.GetLand(id) : Ultima.Art.GetStatic(id);
            }
            catch (Exception)
            {
                // A malformed or out-of-range entry costs one sprite, never the tile.
                return null;
            }

            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return null;
            }

            int width = bitmap.Width;
            int height = bitmap.Height;

            ushort[] pixels;

            // Disposed because Files.CacheData is off, so this bitmap belongs to us and nothing
            // else will ever ask for it again. Leaving them to the finaliser exhausts GDI handles
            // in a few thousand tiles.
            using (bitmap)
            {
                try
                {
                    pixels = ReadPixels(bitmap, width, height);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            if (hue > 0)
            {
                ApplyHue(pixels, hue, !land && IsPartialHue(id));
            }

            return new Sprite(width, height, pixels);
        }

        /// <summary>
        /// Art is Format16bppArgb1555 (Ultima/Settings.cs:9-13), so it is locked in its own format
        /// and copied row by row - the stride is not width*2 and assuming it was would shear every
        /// odd-width sprite.
        /// </summary>
        private static ushort[] ReadPixels(Bitmap bitmap, int width, int height)
        {
            var pixels = new ushort[width * height];
            var row = new short[width];

            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format16bppArgb1555);

            try
            {
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, width);

                    int offset = y * width;

                    for (int x = 0; x < width; x++)
                    {
                        pixels[offset + x] = (ushort)row[x];
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return pixels;
        }

        /// <summary>
        /// The client's hue is a 32-entry ramp indexed by the pixel's five red bits - exactly what
        /// Ultima's own Hue.ApplyTo does (Ultima/Hues.cs:328-390), minus the shared bitmap.
        /// A partial hue recolours only the grey pixels, which is what the flag means.
        /// </summary>
        private static void ApplyHue(ushort[] pixels, int hue, bool partial)
        {
            Ultima.Hue ramp = Ultima.Hues.GetHue(hue - 1);

            if (ramp == null || ramp.Colors == null || ramp.Colors.Length < 32)
            {
                return;
            }

            short[] colors = ramp.Colors;

            for (int i = 0; i < pixels.Length; i++)
            {
                ushort pixel = pixels[i];

                if (pixel == 0)
                {
                    continue;
                }

                if (partial)
                {
                    int r = (pixel >> 10) & 0x1F;
                    int g = (pixel >> 5) & 0x1F;
                    int b = pixel & 0x1F;

                    if (r != g || g != b)
                    {
                        continue;
                    }
                }

                pixels[i] = (ushort)colors[(pixel >> 10) & 0x1F];
            }
        }

        private static bool IsPartialHue(int id)
        {
            ItemData data = TileData.ItemTable[id & TileData.MaxItemValue];
            return (data.Flags & TileFlag.PartialHue) != 0;
        }
    }
}
