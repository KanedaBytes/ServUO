using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// Mirrors what the GG spawners are actually doing to Data/Live/spawners.json.
    ///
    /// The editor can read the spawn XML on its own; what it cannot see is the world. A spawner
    /// that is stopped, or full, or whose next tick is four minutes away, looks identical on disk
    /// to one behaving perfectly - and the whole reason to have a spawner layer is to compare what
    /// the file says with what the town is doing.
    ///
    /// SLOW ON PURPOSE, and written immediately after a reload. Finding spawners means walking
    /// World.Items - there is no index, and XmlSpawner is upstream so there is nowhere of ours to
    /// hang one without an entry in MODIFICATIONS.md. Every other World.Items walk in this shard
    /// carries a comment saying it runs only on an explicit command, never on a timer
    /// (CLAUDE.md section 15), so this one runs at sixty seconds, matching HealthSnapshot, and
    /// spawner state genuinely does not change faster: MinDelay and MaxDelay are in MINUTES. The
    /// editor does not wait for the timer after a save, because TryReloadFile writes one directly.
    ///
    /// SafeCurrentCount, never CurrentCount. The latter calls Defrag(true), which can delete world
    /// objects mid-enumeration - upstream says so itself on SafeTotalSpawnedObjects: "this can be
    /// used in loops over world objects since it will not defrag and potentially modify the world
    /// object lists". A snapshot that mutated the world it was describing would be a poor snapshot.
    /// </summary>
    public static class SpawnerSnapshot
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bridge");

        public const string OutputPath = "Data/Live/spawners.json";

        private static long _sequence;
        private static DateTime? _lastUtc;
        private static string _lastError;

        /// <summary>Seconds between writes. 0 turns it off entirely.</summary>
        public static int IntervalSeconds
        {
            get { return Config.Get("Custom.SpawnerSnapshotSeconds", 60); }
        }

        public static void Initialize()
        {
            HealthCheck.Register("Bridge.Spawners", BuildHealthResult);

            int seconds = IntervalSeconds;

            if (seconds <= 0)
            {
                return;
            }

            Timer.DelayCall(TimeSpan.FromSeconds(10.0), TimeSpan.FromSeconds(seconds), Write);
        }

        public static void Write()
        {
            try
            {
                _sequence++;

                string error;

                if (!AtomicFile.Write(OutputPath, Build(), out error))
                {
                    _lastError = error;
                    Log.Error("Could not write {0}: {1}", OutputPath, error);
                    return;
                }

                _lastError = null;
                _lastUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // A timer callback that throws takes the timer with it.
                _lastError = ex.Message;
                Log.Error(ex, "Spawner snapshot failed.");
            }
        }

        private static string Build()
        {
            Dictionary<string, string> sources = ReadSources();
            HashSet<string> migrated = MigratedNames();

            var builder = new StringBuilder(2048);

            builder.Append("{\n");
            builder.Append("  \"sequence\": ").Append(_sequence).Append(",\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"spawners\": [\n");

            bool first = true;

            // One walk, collecting both sets: the GG spawners, and the stock ones the vendor
            // migration switched off. The second set is what lets the editor draw a stock spawner
            // as disabled rather than as missing.
            foreach (Item item in World.Items.Values)
            {
                var spawner = item as XmlSpawner;

                if (spawner == null || spawner.Deleted || spawner.Name == null)
                {
                    continue;
                }

                bool mine = spawner.Name.StartsWith(GGSpawnCommands.Prefix, StringComparison.Ordinal);
                bool disabled = migrated.Contains(spawner.Name) && InMigratedRegion(spawner);

                if (!mine && !disabled)
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append(",\n");
                }

                first = false;

                AppendSpawner(builder, spawner, sources, disabled);
            }

            builder.Append("\n  ]\n}\n");

            return builder.ToString();
        }

        private static void AppendSpawner(
            StringBuilder builder, XmlSpawner spawner, Dictionary<string, string> sources, bool migrated)
        {
            string uniqueId = spawner.UniqueId ?? "";
            string file;

            if (!sources.TryGetValue(uniqueId, out file))
            {
                file = null;
            }

            builder.Append("    {");
            builder.Append("\"serial\":").Append(spawner.Serial.Value);
            builder.Append(",\"uniqueId\":").Append(Json.Quote(uniqueId));
            builder.Append(",\"name\":").Append(Json.Quote(spawner.Name));
            builder.Append(",\"map\":").Append(Json.Quote(spawner.Map == null ? "" : spawner.Map.Name));
            builder.Append(",\"x\":").Append(spawner.Location.X);
            builder.Append(",\"y\":").Append(spawner.Location.Y);
            builder.Append(",\"z\":").Append(spawner.Location.Z);
            builder.Append(",\"running\":").Append(spawner.Running ? "true" : "false");
            builder.Append(",\"count\":").Append(spawner.SafeCurrentCount);
            builder.Append(",\"maxCount\":").Append(spawner.MaxCount);

            // Negative when a tick is overdue, which is worth seeing rather than clamping: it means
            // the spawner wanted to act and something stopped it.
            builder.Append(",\"nextSpawn\":").Append((int)spawner.NextSpawn.TotalSeconds);

            // Which file this came from, so the editor can line live state up against the records
            // it is editing. Null for a spawner in the world that no file claims - an orphan, and
            // the only thing that would ever show one.
            builder.Append(",\"file\":").Append(file == null ? "null" : Json.Quote(file));
            builder.Append(",\"migrated\":").Append(migrated ? "true" : "false");
            builder.Append("}");
        }

        /// <summary>
        /// UniqueId to the file that defines it, read from Spawns/Custom.
        ///
        /// Read here rather than remembered at import time, because a map built at import would be
        /// wrong after any [XmlLoad the editor did not make, and this is a handful of kilobytes
        /// across two files. The XML is read with XmlReader rather than a DataSet: all this needs
        /// is two element values per row, and DataSet's schema inference has opinions about
        /// repeated elements that do not matter here.
        /// </summary>
        private static Dictionary<string, string> ReadSources()
        {
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string root = Path.Combine(Core.BaseDirectory, GGSpawnCommands.SpawnRoot);

            if (!Directory.Exists(root))
            {
                return sources;
            }

            foreach (string file in Directory.GetFiles(root, "*.xml", SearchOption.AllDirectories))
            {
                string relative = file.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');

                try
                {
                    ReadSourceFile(file, relative, sources);
                }
                catch (Exception ex)
                {
                    // An unreadable spawn file is the editor's problem to report, not a reason to
                    // write no snapshot at all.
                    Log.Warn("Could not index {0}: {1}", relative, ex.Message);
                }
            }

            return sources;
        }

        private static void ReadSourceFile(string file, string relative, Dictionary<string, string> sources)
        {
            using (XmlReader reader = XmlReader.Create(file, new XmlReaderSettings { IgnoreWhitespace = true }))
            {
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "UniqueId")
                    {
                        string id = reader.ReadElementContentAsString();

                        if (!String.IsNullOrEmpty(id))
                        {
                            sources[id] = relative;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Whether a spawner is in the region the migration actually searched.
        ///
        /// STOCK SPAWNER NAMES ARE NOT UNIQUE, and the first version of this file learned that the
        /// hard way: matching on name alone flagged twenty spawners as migrated when six had been
        /// touched. There are four spawners called `Vendors#20` in the world and the migration
        /// disabled exactly one of them - which is why VendorMigration.FindSpawner scopes its own
        /// search to Britain on Trammel, with a comment about a second jeweler that must not be
        /// caught. The snapshot has to scope it the same way or it reports a spawner in another
        /// town as switched off while it is happily spawning.
        /// </summary>
        private static bool InMigratedRegion(XmlSpawner spawner)
        {
            if (spawner.Map != Map.Trammel)
            {
                return false;
            }

            Region region = Region.Find(spawner.Location, spawner.Map);

            return region != null && region.IsPartOf(VendorMigration.RegionName);
        }

        /// <summary>
        /// The stock spawners the vendor migration switched off.
        ///
        /// From the persistence store rather than from britain-daily-life.json's stockSpawner
        /// fields: the config says what the migration WOULD disable, the store says what it did,
        /// and the store exists precisely because the two can disagree.
        /// </summary>
        private static HashSet<string> MigratedNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            VendorMigrationStore store = VendorMigrationStore.Instance;

            if (store == null)
            {
                return names;
            }

            foreach (string name in store.Disabled)
            {
                names.Add(name);
            }

            return names;
        }

        private static HealthResult BuildHealthResult()
        {
            if (IntervalSeconds <= 0)
            {
                return HealthResult.Ok("off (Custom.SpawnerSnapshotSeconds=0)");
            }

            if (_lastError != null)
            {
                return HealthResult.Fail(_lastError);
            }

            if (!_lastUtc.HasValue)
            {
                return HealthResult.Warn("not written yet");
            }

            return HealthResult.Ok(
                String.Format(
                    "seq {0}, written {1}Z, every {2}s",
                    _sequence,
                    _lastUtc.Value.ToString("HH:mm:ss"),
                    IntervalSeconds));
        }
    }
}
