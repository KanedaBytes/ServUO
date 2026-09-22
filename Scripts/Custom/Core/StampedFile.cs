using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Server.Custom
{
    /// <summary>What a file on disk turned out to be when StampedFile.Inspect looked at it.</summary>
    public enum StampKind
    {
        /// <summary>No file.</summary>
        Missing,

        /// <summary>Zero bytes - what Persistence.Deserialize's ensure leaves behind on a first boot.</summary>
        Empty,

        /// <summary>
        /// The format CustomPersistence wrote before 21 September 2026: an int version and then the
        /// payload, no stamp of any kind. Loadable, but it belongs to no particular world save.
        /// </summary>
        Legacy,

        /// <summary>A complete stamped file whose header, trailer and payload hash all agree.</summary>
        Stamped,

        /// <summary>A stamped header that promised more bytes than the file has.</summary>
        HalfWritten,

        /// <summary>Bytes that are neither a legacy file nor a whole stamped one.</summary>
        Corrupt
    }

    /// <summary>The result of inspecting one store file. Numbers are what the reader needs to quote.</summary>
    public sealed class StampInfo
    {
        public StampKind Kind;

        /// <summary>The generation the header claims. -1 for anything that is not stamped.</summary>
        public int Generation = -1;

        public int StoreVersion = -1;

        public DateTime SavedUtc = DateTime.MinValue;

        /// <summary>Bytes the header says the whole file should be. -1 when there is no header to say.</summary>
        public long DeclaredLength = -1;

        /// <summary>Bytes actually on disk.</summary>
        public long ActualLength = -1;

        /// <summary>Hex SHA-256 of the payload as the header recorded it (not recomputed unless Stamped).</summary>
        public string Sha256;

        /// <summary>Plain words for a report line: what was wrong, or nothing when Stamped.</summary>
        public string Detail;
    }

    /// <summary>
    /// The on-disk format of a custom persistence store from 21 September 2026 (REVIEW.md F6).
    ///
    /// A stamped file carries the world-save generation it was written with, a length and a hash of
    /// its payload, and a trailer that repeats the generation. Together those let a reader tell a
    /// whole file from a half one WITHOUT deserializing it, and let PersistenceGeneration prove at
    /// boot that every store on disk was written by the same save as the world files beside it.
    ///
    ///     header  : int32 magic, int32 headerVersion, int32 generation, int64 savedUtcTicks,
    ///               int32 storeVersion, int32 payloadLength, byte[32] sha256(payload)
    ///     payload : payloadLength bytes, exactly what SerializeCore wrote
    ///     trailer : int32 trailerMagic, int32 generation
    ///
    /// The magic is chosen so a legacy file - which begins with its store version, a small int - can
    /// never be mistaken for a stamped one, and a stamped one never for legacy.
    ///
    /// Nothing here opens a file for longer than one using block. AutoSave.Backup() moves the whole
    /// Saves/ directory before every save (Scripts/Misc/AutoSave.cs:161-164), and one handle left open
    /// under it turns the backup rotation off with a warning that scrolls past.
    /// </summary>
    public static class StampedFile
    {
        /// <summary>'G','C','P','S' - Goblin Gang Custom Persistence Stamp.</summary>
        public const int Magic = 0x53504347;

        /// <summary>'G','E','N','D'.</summary>
        public const int TrailerMagic = 0x444E4547;

        public const int HeaderVersion = 1;

        public const int Sha256Bytes = 32;

        /// <summary>magic + headerVersion + generation + savedUtcTicks + storeVersion + payloadLength + sha.</summary>
        public const int HeaderBytes = 4 + 4 + 4 + 8 + 4 + 4 + Sha256Bytes;

        public const int TrailerBytes = 4 + 4;

        /// <summary>
        /// Builds the whole file in memory. The caller writes it atomically; nothing here touches
        /// disk, so a payload that could not be produced never reaches a file.
        /// </summary>
        public static byte[] Build(int generation, int storeVersion, DateTime savedUtc, byte[] payload, out string sha256Hex)
        {
            if (payload == null)
            {
                payload = new byte[0];
            }

            byte[] hash = Hash(payload);
            sha256Hex = Hex(hash);

            var bytes = new byte[HeaderBytes + payload.Length + TrailerBytes];

            using (var ms = new MemoryStream(bytes))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(HeaderVersion);
                w.Write(generation);
                w.Write(savedUtc.Ticks);
                w.Write(storeVersion);
                w.Write(payload.Length);
                w.Write(hash);
                w.Write(payload);
                w.Write(TrailerMagic);
                w.Write(generation);
            }

            return bytes;
        }

        /// <summary>
        /// Classifies a file without trusting it. Reads the whole payload only when the header and
        /// trailer already agree, and store files are a few hundred bytes.
        /// </summary>
        public static StampInfo Inspect(string fullPath)
        {
            var info = new StampInfo();

            try
            {
                var file = new FileInfo(fullPath);

                if (!file.Exists)
                {
                    info.Kind = StampKind.Missing;
                    info.Detail = "missing";
                    return info;
                }

                info.ActualLength = file.Length;

                if (file.Length == 0)
                {
                    info.Kind = StampKind.Empty;
                    info.Detail = "zero bytes";
                    return info;
                }

                if (file.Length < 4)
                {
                    info.Kind = StampKind.Corrupt;
                    info.Detail = String.Format(CultureInfo.InvariantCulture, "{0} byte(s), too short to be anything", file.Length);
                    return info;
                }

                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var r = new BinaryReader(fs))
                {
                    int magic = r.ReadInt32();

                    if (magic != Magic)
                    {
                        info.Kind = StampKind.Legacy;
                        info.Detail = String.Format(
                            CultureInfo.InvariantCulture,
                            "legacy format, no stamp (begins with version {0})", magic);
                        return info;
                    }

                    if (file.Length < HeaderBytes)
                    {
                        info.Kind = StampKind.HalfWritten;
                        info.DeclaredLength = HeaderBytes;
                        info.Detail = String.Format(
                            CultureInfo.InvariantCulture,
                            "{0} bytes on disk, the header alone is {1}", file.Length, HeaderBytes);
                        return info;
                    }

                    int headerVersion = r.ReadInt32();

                    if (headerVersion != HeaderVersion)
                    {
                        info.Kind = StampKind.Corrupt;
                        info.Detail = String.Format(
                            CultureInfo.InvariantCulture,
                            "stamp header version {0}; this build reads {1}", headerVersion, HeaderVersion);
                        return info;
                    }

                    info.Generation = r.ReadInt32();
                    info.SavedUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
                    info.StoreVersion = r.ReadInt32();

                    int payloadLength = r.ReadInt32();

                    if (payloadLength < 0)
                    {
                        info.Kind = StampKind.Corrupt;
                        info.Detail = "negative payload length in the header";
                        return info;
                    }

                    byte[] recorded = r.ReadBytes(Sha256Bytes);
                    info.Sha256 = Hex(recorded);
                    info.DeclaredLength = HeaderBytes + (long)payloadLength + TrailerBytes;

                    if (file.Length < info.DeclaredLength)
                    {
                        info.Kind = StampKind.HalfWritten;
                        info.Detail = String.Format(
                            CultureInfo.InvariantCulture,
                            "{0} bytes on disk, the header declares {1} (generation {2})",
                            file.Length, info.DeclaredLength, info.Generation);
                        return info;
                    }

                    if (file.Length > info.DeclaredLength)
                    {
                        info.Kind = StampKind.Corrupt;
                        info.Detail = String.Format(
                            CultureInfo.InvariantCulture,
                            "{0} bytes on disk, the header declares {1} - bytes after the trailer",
                            file.Length, info.DeclaredLength);
                        return info;
                    }

                    byte[] payload = r.ReadBytes(payloadLength);

                    if (payload.Length != payloadLength)
                    {
                        info.Kind = StampKind.HalfWritten;
                        info.Detail = "the payload read short";
                        return info;
                    }

                    int trailerMagic = r.ReadInt32();
                    int trailerGeneration = r.ReadInt32();

                    if (trailerMagic != TrailerMagic)
                    {
                        info.Kind = StampKind.Corrupt;
                        info.Detail = "the trailer marker is missing";
                        return info;
                    }

                    if (trailerGeneration != info.Generation)
                    {
                        info.Kind = StampKind.Corrupt;
                        info.Detail = String.Format(
                            CultureInfo.InvariantCulture,
                            "header says generation {0}, trailer says {1}", info.Generation, trailerGeneration);
                        return info;
                    }

                    string actual = Hex(Hash(payload));

                    if (!String.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        info.Kind = StampKind.Corrupt;
                        info.Detail = "the payload hash does not match the header";
                        return info;
                    }

                    info.Kind = StampKind.Stamped;
                    info.Detail = null;
                    return info;
                }
            }
            catch (Exception ex)
            {
                info.Kind = StampKind.Corrupt;
                info.Detail = "could not be read: " + ex.Message;
                return info;
            }
        }

        /// <summary>
        /// Reads the payload of a file Inspect already called Stamped. Returns false with the
        /// reason otherwise; never throws.
        /// </summary>
        public static bool TryReadPayload(string fullPath, out StampInfo info, out byte[] payload, out string error)
        {
            payload = null;
            error = null;
            info = Inspect(fullPath);

            if (info.Kind != StampKind.Stamped)
            {
                error = info.Detail ?? info.Kind.ToString();
                return false;
            }

            try
            {
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var r = new BinaryReader(fs))
                {
                    fs.Seek(HeaderBytes, SeekOrigin.Begin);
                    payload = r.ReadBytes((int)(info.DeclaredLength - HeaderBytes - TrailerBytes));
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static string Sha256Hex(byte[] data)
        {
            return Hex(Hash(data ?? new byte[0]));
        }

        private static byte[] Hash(byte[] data)
        {
            // SHA256.Create, never SHA256Managed: the managed class throws under a Windows FIPS policy.
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        private static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);

            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}
