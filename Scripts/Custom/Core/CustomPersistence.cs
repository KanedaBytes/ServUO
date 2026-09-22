using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// ON DISK (from 21 September 2026, REVIEW.md F6): Saves/Custom/<Name>/Persistence.bin is a
    /// STAMPED file - see StampedFile.cs - carrying the world-save generation it was written with,
    /// its payload length and hash, and a trailer. It is produced in memory first, so a
    /// SerializeCore that throws touches no file, and then written to Persistence.bin.tmp and
    /// renamed over the live file, so a crash mid-write can never leave a half file as the live
    /// one. When a live file is replaced in place (a save without the backup rotation) the previous
    /// one is kept beside it as Persistence.bin.prev. Files in the format before that date - an int
    /// version and then the payload, no stamp - still load, and PersistenceGeneration accepts them
    /// only on a tree that has no manifest at all.
    ///
    /// The generation is PersistenceGeneration's: every store is stamped with the same number in the
    /// same save, and the manifest written after them records each one, which is what lets the next
    /// boot prove the world files and the custom files were written together.
    ///
    /// Failure contract: a store that cannot load goes DEGRADED. It loads empty, logs red, and
    /// then REFUSES TO SAVE, so a corrupt or unreadable file is never overwritten with empty
    /// state. A store whose save fails is degraded too, and its live file is the previous good one,
    /// untouched; the manifest lists it as failed, the save's acknowledgement says so, and the next
    /// boot refuses to load the world without it until the operator decides. The degraded flag
    /// surfaces through HealthCheck (so [CoreSmoke reports it) and the fix is to restore the .bin
    /// from Backups/ and restart.
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

        /// <summary>The generation stamped on the file this boot loaded; null for a legacy or absent file.</summary>
        public int? LoadedGeneration { get; private set; }

        /// <summary>True when the file this boot loaded was in the pre-stamp format.</summary>
        public bool LoadedLegacy { get; private set; }

        /// <summary>The generation the last successful save this boot stamped; null before one.</summary>
        public int? LastSavedGeneration { get; private set; }

        public long LastSavedLength { get; private set; }

        public string LastSavedSha256 { get; private set; }

        public virtual string FilePath
        {
            get { return Path.Combine("Saves", "Custom", Name, "Persistence.bin"); }
        }

        /// <summary>FilePath with forward slashes, the form the manifest records.</summary>
        public string RelativeFilePath
        {
            get { return FilePath.Replace('\\', '/'); }
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

        /// <summary>Write the payload. The version is recorded in the stamp, not by you.</summary>
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

        /// <summary>
        /// Every store, stamped with the generation this save is producing, and then the results
        /// handed to PersistenceGeneration, whose AfterWorldSave handler writes the manifest once
        /// every other WorldSave handler in the shard has written too. One handler, one order.
        /// </summary>
        private static void OnWorldSave(WorldSaveEventArgs e)
        {
            int generation = PersistenceGeneration.Pending > 0
                ? PersistenceGeneration.Pending
                : PersistenceGeneration.Current + 1;

            var results = new List<StoreSaveResult>(_stores.Count);

            foreach (CustomPersistence store in _stores)
            {
                results.Add(store.Save(generation));
            }

            PersistenceGeneration.RecordStores(results);
        }

        private void Load()
        {
            string path = Path.Combine(Core.BaseDirectory, FilePath);

            try
            {
                Reset();

                StampInfo info = StampedFile.Inspect(path);

                switch (info.Kind)
                {
                    case StampKind.Stamped:
                        LoadStamped(path, info);
                        break;

                    case StampKind.HalfWritten:
                    case StampKind.Corrupt:
                        // PersistenceGeneration refuses the boot before this for any store the manifest
                        // lists, so reaching here means the operator accepted the tree, or the store is
                        // one the manifest never saw. Either way the file cannot be read, and the
                        // degraded path below is what protects it.
                        throw new Exception(
                            (info.Kind == StampKind.HalfWritten ? "half-written: " : "corrupt: ") + info.Detail);

                    default:
                        // Missing, Empty or Legacy: the path this class always took. Persistence.Deserialize
                        // creates the directory and a zero-length file on a first boot, and End() keeps
                        // that out of the exception path.
                        LoadLegacy(path);
                        LoadedLegacy = info.Kind == StampKind.Legacy;
                        break;
                }

                LastLoadedUtc = DateTime.UtcNow;

                Log.Debug(
                    "[{0}] Loaded ({1}).",
                    Name,
                    LoadedGeneration.HasValue
                        ? "generation " + LoadedGeneration.Value
                        : (LoadedLegacy ? "legacy format" : "no file"));
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

        private void LoadStamped(string path, StampInfo info)
        {
            StampInfo again;
            byte[] payload;
            string error;

            if (!StampedFile.TryReadPayload(path, out again, out payload, out error))
            {
                throw new Exception(error);
            }

            if (payload.Length > 0)
            {
                using (var ms = new MemoryStream(payload, false))
                {
                    var reader = new BinaryFileReader(new BinaryReader(ms));
                    DeserializeCore(reader, info.StoreVersion);
                }
            }

            LoadedGeneration = info.Generation;
            LoadedLegacy = false;
        }

        private void LoadLegacy(string path)
        {
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

            LoadedGeneration = null;
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

        /// <summary>
        /// Writes this store stamped with <paramref name="generation"/>, and reports what happened
        /// for the manifest. The live file changes only by an atomic rename of a complete file, so
        /// whatever this returns, the file on disk is whole.
        /// </summary>
        private StoreSaveResult Save(int generation)
        {
            var result = new StoreSaveResult
            {
                Name = Name,
                Path = RelativeFilePath,
                Version = Version,
                Generation = LastSavedGeneration ?? LoadedGeneration ?? -1,
                Length = LastSavedLength,
                Sha256 = LastSavedSha256
            };

            if (IsDegraded)
            {
                result.Ok = false;
                result.Error = "degraded, save skipped: " + DegradedReason;

                Log.Error(
                    "[{0}] DEGRADED - skipping save to avoid overwriting {1}. Reason: {2}",
                    Name,
                    FilePath,
                    DegradedReason);

                return result;
            }

            try
            {
                // Serialize into memory. This is the F6 fix in one line: a SerializeCore that throws
                // has touched nothing, and the live file is exactly what it was.
                byte[] payload;

                using (var ms = new MemoryStream())
                {
                    // The Stream ctor uses Utility.UTF8 exactly as the file-backed write always did
                    // (Server/Serialization.cs:211-217); strings never carry a preamble, so the bytes
                    // are the ones a legacy file would have held after its version int.
                    var writer = new BinaryFileWriter(ms, true);
                    SerializeCore(writer);
                    writer.Flush();
                    payload = ms.ToArray();
                }

                DateTime now = DateTime.UtcNow;
                string sha;
                byte[] bytes = StampedFile.Build(generation, Version, now, payload, out sha);

                string error;

                if (!AtomicFile.Write(RelativeFilePath, bytes, ".prev", out error))
                {
                    throw new IOException(error);
                }

                LastSavedUtc = now;
                LastSavedGeneration = generation;
                LastSavedLength = bytes.Length;
                LastSavedSha256 = sha;

                result.Ok = true;
                result.Generation = generation;
                result.Length = bytes.Length;
                result.Sha256 = sha;
            }
            catch (Exception ex)
            {
                // An exception escaping here is rethrown by World.Save as
                // "FATAL: Exception in EventSink.WorldSave" and terminates the shard, so it
                // must not propagate. Degrade instead; the file on disk is the previous good one.
                MarkDegraded("save failed: " + ex.Message);
                Log.Error(ex, "[{0}] Save failed; the live file is untouched and the store is marked degraded.", Name);

                result.Ok = false;
                result.Error = ex.Message;
            }

            return result;
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

            string loadedGeneration = LoadedGeneration.HasValue
                ? "generation " + LoadedGeneration.Value.ToString(CultureInfo.InvariantCulture)
                : (LoadedLegacy ? "legacy file" : "no file");

            string savedGeneration = LastSavedGeneration.HasValue
                ? "generation " + LastSavedGeneration.Value.ToString(CultureInfo.InvariantCulture)
                : "none this boot";

            return HealthResult.Ok(String.Format(
                "v{0}, loaded {1} ({2}), saved {3} ({4})",
                Version, loaded, loadedGeneration, saved, savedGeneration));
        }
    }
}
