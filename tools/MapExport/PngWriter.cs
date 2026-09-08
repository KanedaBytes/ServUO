using System;
using System.IO;
using System.IO.Compression;

namespace Server.Custom.MapExport
{
    /// <summary>
    /// A minimal 8-bit truecolour PNG encoder, RGB or RGBA.
    ///
    /// The radar pyramid writes RGB; the isometric renderer writes RGBA, because a rectangular
    /// tile over a diamond projection is mostly empty at its corners and opaque black there would
    /// read as real map. Transparency is also what lets the radar underlay show through an art
    /// tile that only partly covers the world.
    ///
    /// Hand-rolled because the repo has no imaging dependency and does not want one:
    /// System.Drawing.Common is Windows-only from .NET 7 onward, and this shard is meant to run
    /// on Linux later. A tile writer is about eighty lines; a cross-platform imaging library is
    /// a permanent dependency.
    ///
    /// Two things here exist purely because the tool targets net48, which it must in order to
    /// reference Server.csproj:
    ///   - ZLibStream is .NET 6+, so the zlib wrapper (2-byte header, Adler-32 trailer) around
    ///     DeflateStream is written out by hand.
    ///   - System.IO.Hashing.Crc32 is not in the box, so CRC32 is here too.
    /// </summary>
    internal static class PngWriter
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static void Write(string path, byte[] pixels, int width, int height)
        {
            Write(path, pixels, width, height, 3);
        }

        public static void Write(string path, byte[] pixels, int width, int height, int bytesPerPixel)
        {
            if (bytesPerPixel != 3 && bytesPerPixel != 4)
            {
                throw new ArgumentOutOfRangeException("bytesPerPixel", "Only 3 (RGB) and 4 (RGBA) are supported.");
            }

            string directory = Path.GetDirectoryName(path);

            if (!String.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(Signature, 0, Signature.Length);

                var header = new byte[13];
                WriteInt32(header, 0, width);
                WriteInt32(header, 4, height);
                header[8] = 8;  // bit depth
                header[9] = (byte)(bytesPerPixel == 4 ? 6 : 2); // 2 = truecolour RGB, 6 = RGB + alpha
                header[10] = 0; // deflate
                header[11] = 0; // adaptive filtering
                header[12] = 0; // no interlace

                WriteChunk(file, "IHDR", header);
                WriteChunk(file, "IDAT", Compress(pixels, width, height, bytesPerPixel));
                WriteChunk(file, "IEND", new byte[0]);
            }
        }

        /// <summary>
        /// Filters each scanline with type 1 (Sub) and deflates the lot, wrapped in a zlib
        /// container that DeflateStream does not provide on its own.
        /// </summary>
        private static byte[] Compress(byte[] pixels, int width, int height, int bytesPerPixel)
        {
            int stride = width * bytesPerPixel;

            var raw = new byte[(stride + 1) * height];

            for (int y = 0; y < height; y++)
            {
                int source = y * stride;
                int target = y * (stride + 1);

                raw[target] = 1; // Sub

                for (int x = 0; x < stride; x++)
                {
                    byte left = x >= bytesPerPixel ? pixels[source + x - bytesPerPixel] : (byte)0;
                    raw[target + 1 + x] = (byte)(pixels[source + x] - left);
                }
            }

            using (var output = new MemoryStream())
            {
                // zlib header: deflate, 32K window, default level, no preset dictionary.
                output.WriteByte(0x78);
                output.WriteByte(0x9C);

                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                {
                    deflate.Write(raw, 0, raw.Length);
                }

                uint adler = Adler32(raw);

                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)adler);

                return output.ToArray();
            }
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            WriteInt32(length, 0, data.Length);
            stream.Write(length, 0, 4);

            var payload = new byte[4 + data.Length];

            for (int i = 0; i < 4; i++)
            {
                payload[i] = (byte)type[i];
            }

            Buffer.BlockCopy(data, 0, payload, 4, data.Length);
            stream.Write(payload, 0, payload.Length);

            uint crc = Crc32(payload);

            stream.WriteByte((byte)(crc >> 24));
            stream.WriteByte((byte)(crc >> 16));
            stream.WriteByte((byte)(crc >> 8));
            stream.WriteByte((byte)crc);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1;
            uint b = 0;

            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }

            return (b << 16) | a;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];

            for (uint i = 0; i < 256; i++)
            {
                uint c = i;

                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }

        private static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;

            for (int i = 0; i < data.Length; i++)
            {
                c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            }

            return c ^ 0xFFFFFFFFu;
        }
    }
}
