using System;
using System.Collections.Generic;
using System.IO;

namespace Server.Custom
{
    /// <summary>
    /// Reusable base for system-level persisted data, wrapping Server.Persistence and the
    /// EventSink.WorldSave / WorldLoad pair. This is the ServUO analogue of ModernUO's
    /// GenericPersistence, which self-registered; ServUO's Persistence helper does not, so this
    /// base does the registering.
    ///
    /// Subclass, call the base constructor with a name and version, and implement the two Core
    /// methods. No event wiring in the subclass:
    ///
    ///     public sealed class MyStore : CustomPersistence
    ///     {
    ///         public MyStore() : base("MyStore", 0) { }
    ///         protected override void SerializeCore(GenericWriter w) { ... }
    ///         protected override void DeserializeCore(GenericReader r, int version) { ... }
    ///     }
    ///
    /// Failure contract: a store that cannot load goes DEGRADED. It loads empty, logs red, and
    /// then REFUSES TO SAVE, so a corrupt or unreadable file is never overwritten with empty
    /// state. The degraded flag surfaces through HealthCheck (so [CoreSmoke reports it) and the
    /// fix is to restore the .bin from Backups/ and restart.
    /// </summary>
    public abstract class CustomPersistence
    {
        private static readonly CustomLogger Log = CustomLogger.For("Persistence");

        private static readonly List<CustomPersistence> _stores = new List<CustomPersistence>();

        private static bool _hooked;

        public string Name { get; private set; }

        /// <summary>Version written ahead of the payload, for forward migration.</summary>
        public int Version { get; private set; }

        /// <summary>True once a load or save has failed. While true, saving is suppressed.</summary>
        public bool IsDegraded { get; private set; }

        public string DegradedReason { get; private set; }

        public DateTime? LastLoadedUtc { get; private set; }

        public DateTime? LastSavedUtc { get; private set; }

        public virtual string FilePath
        {
            get { return Path.Combine("Saves", "Custom", Name, "Persistence.bin"); }
        }

        public static IList<CustomPersistence> Stores { get { return _stores.AsReadOnly(); } }

        protected CustomPersistence(string name, int version)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A persistence store needs a name.", "name");
            }

            Name = name;
            Version = version;

            _stores.Add(this);

            HealthCheck.Register("Persistence:" + name, BuildHealthResult);
        }

        /// <summary>Write the payload. The version stamp is already written.</summary>
        protected abstract void SerializeCore(GenericWriter writer);

        /// <summary>
        /// Read the payload written by SerializeCore at the given version. Not called at all
        /// for a first boot (missing or zero-length file), so fields keep their defaults.
        /// </summary>
        protected abstract void DeserializeCore(GenericReader reader, int version);

        /// <summary>Optional hook for a store to reset itself before a reload.</summary>
        protected virtual void Reset()
        {
        }

        public static void Configure()
        {
            // Guarded because ScriptCompiler.Invoke walks every type; if a subclass ever exposes
            // this static through its own surface, we must still only hook once.
            if (_hooked)
            {
                return;
            }

            _hooked = true;

            EventSink.WorldSave += OnWorldSave;
            EventSink.WorldLoad += OnWorldLoad;
        }

        private static void OnWorldLoad()
        {
            foreach (CustomPersistence store in _stores)
            {
                store.Load();
            }
        }

        private static void OnWorldSave(WorldSaveEventArgs e)
        {
            foreach (CustomPersistence store in _stores)
            {
                store.Save();
            }
        }

        private void Load()
        {
            string path = Path.Combine(Core.BaseDirectory, FilePath);

            try
            {
                Reset();

                Persistence.Deserialize(
                    path,
                    reader =>
                    {
                        // A first boot leaves a zero-length file behind; End() keeps that out of
                        // the exception path entirely rather than relying on Persistence
                        // swallowing the EndOfStreamException.
                        if (reader.End())
                        {
                            return;
                        }

                        int version = reader.ReadInt();
                        DeserializeCore(reader, version);
                    });

                LastLoadedUtc = DateTime.UtcNow;
                Log.Debug("[{0}] Loaded.", Name);
            }
            catch (Exception ex)
            {
                // Persistence rethrows a plain Exception with the original only as text
                // (Server/Persistence/Persistence.cs), so ex.Message is all we get.
                MarkDegraded("load failed: " + ex.Message);

                // Refusing to save protects the file for exactly one save cycle, because
                // AutoSave.Backup() MOVES the whole Saves/ directory into
                // Backups/Automatic/Most Recent before each save. After three rotations the
                // original would be gone, and the boot after that would find no file, load
                // clean, and quietly persist empty state. Copy it somewhere the rotation does
                // not reach, right now, while it still exists.
                string quarantined = Quarantine(path);

                Log.Error(
                    "[{0}] DEGRADED - could not load {1}. Saving is now DISABLED for this store so " +
                    "the file is not overwritten.",
                    Name,
                    FilePath);
                Log.Error("[{0}] Reason: {1}", Name, ex.Message);

                if (quarantined != null)
                {
                    Log.Error("[{0}] The unreadable file was copied to {1}", Name, quarantined);
                }

                Log.Error(
                    "[{0}] To recover: stop the shard, restore {1} from Backups/, and restart.",
                    Name,
                    FilePath);
            }
        }

        /// <summary>
        /// Copies an unreadable save to Backups/Degraded/, which AutoSave never rotates or
        /// prunes. Returns the destination, or null if it could not be copied.
        /// </summary>
        private string Quarantine(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                string directory = Path.Combine(Core.BaseDirectory, "Backups", "Degraded");

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string destination = Path.Combine(
                    directory,
                    String.Format("{0}-{1}.bin", Name, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));

                File.Copy(path, destination, true);
                return destination;
            }
            catch (Exception ex)
            {
                Log.Error("[{0}] Could not quarantine the unreadable save: {1}", Name, ex.Message);
                return null;
            }
        }

        private void Save()
        {
            if (IsDegraded)
            {
                Log.Error(
                    "[{0}] DEGRADED - skipping save to avoid overwriting {1}. Reason: {2}",
                    Name,
                    FilePath,
                    DegradedReason);
                return;
            }

            try
            {
                string path = Path.Combine(Core.BaseDirectory, FilePath);
                var file = new FileInfo(path);

                if (file.Directory != null && !file.Directory.Exists)
                {
                    file.Directory.Create();
                }

                // Deliberately not Persistence.Serialize: it opens with OpenWrite
                // (FileMode.OpenOrCreate), which does not truncate, so a save that writes fewer
                // bytes than the last one leaves stale tail bytes behind. FileMode.Create
                // truncates. Reads still go through Persistence.Deserialize for its
                // first-boot handling. Logged in Scripts/Custom/MODIFICATIONS.md.
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var writer = new BinaryFileWriter(fs, true);

                    try
                    {
                        writer.Write(Version);
                        SerializeCore(writer);
                    }
                    finally
                    {
                        writer.Flush();
                        writer.Close();
                    }
                }

                LastSavedUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // An exception escaping here is rethrown by World.Save as
                // "FATAL: Exception in EventSink.WorldSave" and terminates the shard, so it
                // must not propagate. Degrade instead.
                MarkDegraded("save failed: " + ex.Message);
                Log.Error(ex, "[{0}] Save failed; store marked degraded.", Name);
            }
        }

        protected void MarkDegraded(string reason)
        {
            IsDegraded = true;
            DegradedReason = reason ?? "unknown";
        }

        /// <summary>
        /// Clears the degraded flag, re-enabling saves. Only call after the underlying problem
        /// is fixed - it does not re-read the file.
        /// </summary>
        public void ClearDegraded()
        {
            IsDegraded = false;
            DegradedReason = null;
        }

        private HealthResult BuildHealthResult()
        {
            if (IsDegraded)
            {
                return HealthResult.Fail(
                    String.Format("SAVING DISABLED - {0} ({1})", DegradedReason, FilePath));
            }

            string loaded = LastLoadedUtc.HasValue
                ? LastLoadedUtc.Value.ToString("HH:mm:ss") + "Z"
                : "never";

            string saved = LastSavedUtc.HasValue
                ? LastSavedUtc.Value.ToString("HH:mm:ss") + "Z"
                : "never";

            return HealthResult.Ok(String.Format("v{0}, loaded {1}, saved {2}", Version, loaded, saved));
        }
    }
}
