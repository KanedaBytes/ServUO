using System;
using System.IO;

namespace Server.Custom.MapExport
{
    /// <summary>
    /// The client's own radar palette, from radarcol.mul.
    ///
    /// Not Ultima.RadarCol, deliberately: that class runs a Windows registry probe in its static
    /// constructor to find the client directory, and this tool already knows where the client is
    /// from Config/DataPath.cfg. Reading the file directly is a dozen lines and keeps the tool
    /// working on a machine with no client installed under the registry key.
    /// </summary>
    internal sealed class RadarColors
    {
        /// <summary>Static item colours start here; below it is land.</summary>
        private const int ItemOffset = 0x4000;

        private readonly uint[] _colors;

        private RadarColors(uint[] colors)
        {
            _colors = colors;
        }

        public int Count
        {
            get { return _colors.Length; }
        }

        /// <summary>
        /// Entry count comes from the file length rather than an assumed 0x10000: modern clients
        /// ship a shorter table, and reading past it would be a silent black tile.
        /// </summary>
        public static RadarColors Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            var colors = new uint[bytes.Length / 2];

            for (int i = 0; i < colors.Length; i++)
            {
                int value = bytes[i * 2] | (bytes[(i * 2) + 1] << 8);

                // ARGB1555, alpha ignored. The 5->8 bit expansion repeats the high bits into the
                // low ones so 31 becomes 255 rather than 248.
                uint r = Expand((value >> 10) & 0x1F);
                uint g = Expand((value >> 5) & 0x1F);
                uint b = Expand(value & 0x1F);

                colors[i] = (r << 16) | (g << 8) | b;
            }

            return new RadarColors(colors);
        }

        public uint Land(int id)
        {
            return (uint)id < (uint)_colors.Length ? _colors[id] : 0u;
        }

        public uint Static(int id)
        {
            int index = ItemOffset + id;
            return (uint)index < (uint)_colors.Length ? _colors[index] : 0u;
        }

        private static uint Expand(int value)
        {
            return (uint)((value << 3) | (value >> 2));
        }
    }
}
