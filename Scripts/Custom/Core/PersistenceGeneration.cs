// PersistenceGeneration.cs - the world save and the custom files must have been written together.
//
// THE PROBLEM THIS SOLVES
// -----------------------
// ServUO has no save generation. World.m_Saves is internal and starts at zero every boot
// (Server/World.cs:1095); the strategy writes Saves/Mobiles, Items, Guilds and Customs with no stamp
// in them; and the custom stores under Saves/Custom used to write "int version, payload" with no
// record of which world save they belonged to. So after a crash, a hand-restored backup, or a store
// that failed to save, the tree on disk could be a MIX - a world from one save and a Jail file from
// another - and the shard would load it without a word. REVIEW.md F6 and the save-acknowledgement
// finding are both instances of that: nothing on disk said what belonged together.
//
// THE MECHANISM
// -------------
// A generation is an integer that goes up by one per completed world save and is persisted in
// Saves/Custom/Manifest.json, which is written LAST, from EventSink.AfterWorldSave, once the
// strategy and every WorldSave handler - stock and custom - have written their files. Every custom
// store is stamped with the generation as it is written (StampedFile.cs); the manifest records each
// store's generation, length and hash, and for every other file under Saves/ its length and last
// write time. The manifest therefore IS the world save's generation stamp, and its fingerprints are
// what prove the files beside it are the ones it stamped.
//
// AfterWorldSave rather than WorldSave, because ~40 stock systems write their own files under Saves/
// from WorldSave handlers in an order that is undefined relative to ours (ScriptCompiler.Invoke
// sorts Configure() by priority and ties arbitrarily). A manifest written from WorldSave could only
// fingerprint the four strategy directories; from AfterWorldSave it can see the whole save. It is
// still on the game thread, still inside World.Save, and still before AutoSave.Save() returns, so
// the bridge's `save` handler reads a finished record.
//
// Length and last-write ticks rather than a hash of the world files, deliberately: Items.bin is 17 MB
// and Mobiles.bin 31 MB here, and hashing them would add 100-200 ms to a 0.3-0.4 s freeze on the core
// thread. The manifest is the stamp; the fingerprint only has to prove the files are the ones it saw.
//
// AT BOOT the check runs from a Configure() at Int32.MinValue + 1 - before Region.Load and
// World.Load, before anything has been loaded or could be written - and REFUSES, with a plain message
// and Environment.Exit(3), when the tree disagrees with itself. Nothing is repaired automatically.
// The message names the file, the generations on both sides, the last complete backup, and the
// three things the operator can do about it; one of them is an explicit marker file
// (Saves/Custom/ACCEPT) that says "load it as it is", and that is the only override.
//
// WHY EXIT AND NOT THROW: an exception out of Configure reaches CurrentDomain_UnhandledException
// (Server/Main.cs:183-233), which prints a stack trace and blocks on Console.ReadLine. A shard
// blocked there keeps ServUO.exe alive, so tools/restart-shard.ps1 refuses to start another and the
// bridge's process poll says the shard is up. Exiting is what leaves the tree untouched AND the
// process gone; the -NoExit window keeps the message on screen, build.ps1 prints the exit code, and
// Data/Live/boot-refusal.json carries the same text to the bridge, which cannot read a console.
//
// THE ONE HOLE THAT REMAINS: a crash inside the very first stamped save leaves a tree with no
// manifest anywhere and only legacy files, which is indistinguishable from the pre-upgrade tree and
// loads. Once one stamped save has completed, Backups/Automatic/Most Recent carries a manifest and
// the "last save did not complete" rule catches every later one.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

using Newtonsoft.Json.Linq;

namespace Server.Custom
{
    /// <summary>What one store reported when the save asked it to write itself.</summary>
    public sealed class StoreSaveResult
    {
        public string Name;

        /// <summary>Relative to the shard root, forward slashes: Saves/Custom/Jail/Persistence.bin.</summary>
        public string Path;

        public bool Ok;

        public string Error;

        /// <summary>The generation now on disk for this store: the one just stamped, or the last good one when !Ok.</summary>
        public int Generation;

        public int Version;

        public long Length;

        public string Sha256;
    }

    /// <summary>The last world save this boot, as the manifest saw it.</summary>
    public sealed class SaveRecord
    {
        public int Generation;

        /// <summary>Every store wrote and the manifest wrote. This is what an ack may call "saved".</summary>
        public bool Ok;

        public bool ManifestWritten;

        public string Error;

        public DateTime Utc;

        public double Seconds;

        public int StoresOk;

        public IList<string> FailedStores = new List<string>();

        public int WorldFiles;

        public string Describe()
        {
            if (Ok)
            {
                return String.Format(
                    CultureInfo.InvariantCulture,
                    "generation {0} complete in {1:F2}s ({2} store(s), {3} world file(s))",
                    Generation, Seconds, StoresOk, WorldFiles);
            }

            return String.Format(
                CultureInfo.InvariantCulture,
                "generation {0} FAILED: {1}", Generation, Error ?? "unknown");
        }
    }

    public sealed class ManifestStore
    {
        public string Name;
        public string Path;
        public int Generation;
        public int Version;
        public long Length;
        public string Sha256;
        public bool Ok;
        public string Error;
    }

    public sealed class ManifestWorldFile
    {
        public string Path;
        public long Length;
        public long LastWriteTicks;
    }

    /// <summary>Saves/Custom/Manifest.json, parsed.</summary>
    public sealed class Manifest
    {
        public int Format;
        public int Generation;
        public string SavedUtc;
        public string BootId;
        public double Seconds;
        public List<ManifestStore> Stores = new List<ManifestStore>();
        public List<ManifestWorldFile> World = new List<ManifestWorldFile>();

        public bool AllStoresOk
        {
            get
            {
                for (int i = 0; i < Stores.Count; i++)
                {
                    if (!Stores[i].Ok)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }

    public sealed class WorldFileFingerprint
    {
        public string Path;
        public long Length;
        public long LastWriteTicks;
    }

    public enum VerdictKind
    {
        /// <summary>No world files, no manifest, no stores: a brand new shard.</summary>
        Fresh,

        /// <summary>World files and legacy stores, no manifest anywhere: the tree from before 21 September 2026.</summary>
        PreUpgrade,

        /// <summary>A manifest whose every claim the files on disk bear out.</summary>
        Verified,

        Refused
    }

    /// <summary>What Verify concluded about a Saves/ tree.</summary>
    public sealed class Verdict
    {
        public VerdictKind Kind;

        public bool Ok { get { return Kind != VerdictKind.Refused; } }

        /// <summary>
        /// The generation the tree is loaded as when Ok, and the one it WOULD be loaded as if the
        /// operator accepts it when Refused - the number the message tells them to write.
        /// </summary>
        public int Generation;

        /// <summary>One line, for the console and the health check, when Ok.</summary>
        public string Summary;

        /// <summary>The whole refusal text, when Refused.</summary>
        public string Message;

        /// <summary>The per-file findings the message was built from.</summary>
        public List<string> Lines = new List<string>();

        public List<string> Warnings = new List<string>();

        public int Stores;

        public int WorldFiles;

        public Manifest Manifest;

        public Manifest Previous;

        /// <summary>Set by Configure when a Refused verdict was loaded anyway through Saves/Custom/ACCEPT.</summary>
        public bool Accepted;
    }

    public static class PersistenceGeneration
    {
        public const string ManifestFileName = "Manifest.json";

        public const string AcceptFileName = "ACCEPT";

        public const string ManifestRelativePath = "Saves/Custom/" + ManifestFileName;

        public const string RefusalRelativePath = "Data/Live/boot-refusal.json";

        public const string PreviousBackupRelativePath = "Backups/Automatic/Most Recent";

        /// <summary>What ServUO.exe exits with when it refuses the tree. build.ps1 prints it.</summary>
        public const int RefusedExitCode = 3;

        public const int ManifestFormat = 1;

        private static readonly CustomLogger Log = CustomLogger.For("Persistence");

        private static Stopwatch _saving;

        private static IList<StoreSaveResult> _pendingStores;

        /// <summary>The generation the tree on disk is at. Zero for a fresh or pre-upgrade tree.</summary>
        public static int Current { get; private set; }

        /// <summary>The generation being written by the save in progress; zero outside a save.</summary>
        public static int Pending { get; private set; }

        /// <summary>This process. Acks and health.json carry it so a caller can tell one boot from the next.</summary>
        public static string BootId { get; private set; }

        public static Verdict BootVerdict { get; private set; }

        public static SaveRecord LastSave { get; private set; }

        static PersistenceGeneration()
        {
            BootId = Guid.NewGuid().ToString("N").Substring(0, 16);
        }

        /// <summary>
        /// Before every other Configure() in the tree except CurrentExpansion's Int32.MinValue, which
        /// only sets statics. Nothing in any Configure() writes under Saves/, so running first buys
        /// determinism rather than safety; but a refusal that fires before anything else has printed
        /// is a refusal somebody reads.
        /// </summary>
        [CallPriority(Int32.MinValue + 1)]
        public static void Configure()
        {
            string savesDir = System.IO.Path.Combine(Core.BaseDirectory, "Saves");
            string previousDir = System.IO.Path.Combine(Core.BaseDirectory, PreviousBackupRelativePath);
            string acceptPath = System.IO.Path.Combine(savesDir, "Custom", AcceptFileName);

            Verdict verdict = Verify(savesDir, previousDir);

            string acceptRaw;
            int accepted;
            bool hasAccept = TryReadAccept(acceptPath, out accepted, out acceptRaw);

            if (verdict.Ok)
            {
                if (hasAccept)
                {
                    Log.Warn(
                        "Saves/Custom/ACCEPT was present (\"{0}\") on a tree that needed no acceptance; deleted.",
                        acceptRaw);
                    TryDelete(acceptPath);
                }

                TryDelete(System.IO.Path.Combine(Core.BaseDirectory, RefusalRelativePath));
            }
            else if (hasAccept && accepted == verdict.Generation)
            {
                // The operator's call, made in the file the refusal told them to write. Logged whole,
                // because the next reader of the console deserves to know the tree was a mix.
                Log.Warn(verdict.Message);
                Log.Warn(
                    "ACCEPTED by operator: Saves/Custom/ACCEPT said {0}. Loading the tree as it is, as " +
                    "generation {0}; the next save is stamped {1}. The marker has been deleted.",
                    accepted, accepted + 1);

                TryDelete(acceptPath);
                TryDelete(System.IO.Path.Combine(Core.BaseDirectory, RefusalRelativePath));
                verdict.Accepted = true;
            }
            else
            {
                string text = verdict.Message;

                if (hasAccept)
                {
                    text += String.Format(
                        CultureInfo.InvariantCulture,
                        "\n  Saves/Custom/ACCEPT said \"{0}\" but this tree would load as generation {1}; " +
                        "not accepted, and the marker has been deleted.",
                        acceptRaw, verdict.Generation);
                    TryDelete(acceptPath);
                }

                Refuse(text, verdict);
                return;
            }

            Current = verdict.Generation;
            BootVerdict = verdict;

            Log.Info(verdict.Summary);

            for (int i = 0; i < verdict.Warnings.Count; i++)
            {
                Log.Warn(verdict.Warnings[i]);
            }

            EventSink.BeforeWorldSave += OnBeforeWorldSave;
            EventSink.AfterWorldSave += OnAfterWorldSave;

            HealthCheck.Register("Core.Persistence", BuildHealthResult);
        }

        private static void Refuse(string text, Verdict verdict)
        {
            Utility.PushColor(ConsoleColor.Red);
            Console.WriteLine();
            Console.WriteLine(text);
            Console.WriteLine();
            Utility.PopColor();

            // The bridge cannot read a console. Its restart handshake reads this file and fails fast
            // with the shard's own words instead of asking a dead process for two to six minutes.
            var builder = new StringBuilder(text.Length + 256);
            builder.Append("{\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"generation\": ").Append(verdict.Generation).Append(",\n");
            builder.Append("  \"manifestGeneration\": ")
                .Append(verdict.Manifest == null ? "null" : verdict.Manifest.Generation.ToString(CultureInfo.InvariantCulture))
                .Append(",\n");
            builder.Append("  \"exitCode\": ").Append(RefusedExitCode).Append(",\n");
            builder.Append("  \"message\": ").Append(Json.Quote(text)).Append("\n");
            builder.Append("}\n");

            string error;

            if (!AtomicFile.Write(RefusalRelativePath, builder.ToString(), out error))
            {
                Console.WriteLine("(the refusal could not be written to {0}: {1})", RefusalRelativePath, error);
            }

            Environment.Exit(RefusedExitCode);
        }

        // ---- the save ------------------------------------------------------------------------------

        private static void OnBeforeWorldSave(BeforeWorldSaveEventArgs e)
        {
            _saving = Stopwatch.StartNew();
            Pending = Current + 1;
            _pendingStores = null;
        }

        /// <summary>
        /// CustomPersistence.OnWorldSave hands over what every store did, so the manifest written a
        /// moment later from AfterWorldSave lists them. The generation they were stamped with is Pending.
        /// </summary>
        internal static void RecordStores(IList<StoreSaveResult> results)
        {
            _pendingStores = results;
        }

        private static void OnAfterWorldSave(AfterWorldSaveEventArgs e)
        {
            // Everything in here is guarded: an exception escaping an AfterWorldSave handler is
            // rethrown by World.Save as FATAL and ends the shard (Server/World.cs:1204-1207).
            try
            {
                CommitManifest();
            }
            catch (Exception ex)
            {
                LastSave = new SaveRecord
                {
                    Generation = Pending,
                    Ok = false,
                    ManifestWritten = false,
                    Error = "manifest step threw: " + ex.Message,
                    Utc = DateTime.UtcNow
                };

                Log.Error(ex, "The manifest for generation {0} was not written.", Pending);
            }
            finally
            {
                Pending = 0;
                _pendingStores = null;
                _saving = null;
            }
        }

        private static void CommitManifest()
        {
            int generation = Pending > 0 ? Pending : Current + 1;
            double seconds = _saving == null ? 0.0 : _saving.Elapsed.TotalSeconds;
            IList<StoreSaveResult> stores = _pendingStores ?? new List<StoreSaveResult>();
            DateTime utc = DateTime.UtcNow;

            string savesDir = System.IO.Path.Combine(Core.BaseDirectory, "Saves");
            IList<WorldFileFingerprint> world = FingerprintWorld(savesDir);

            string json = BuildManifestJson(generation, BootId, utc, seconds, stores, world);

            string error;
            bool written = AtomicFile.Write(ManifestRelativePath, json, out error);

            var record = new SaveRecord
            {
                Generation = generation,
                ManifestWritten = written,
                Utc = utc,
                Seconds = seconds,
                WorldFiles = world.Count
            };

            for (int i = 0; i < stores.Count; i++)
            {
                if (stores[i].Ok)
                {
                    record.StoresOk++;
                }
                else
                {
                    record.FailedStores.Add(stores[i].Name + ": " + (stores[i].Error ?? "failed"));
                }
            }

            if (_pendingStores == null)
            {
                record.FailedStores.Add("no store results were recorded - CustomPersistence.OnWorldSave did not run");
            }

            if (!written)
            {
                record.Error = "stores written but the manifest was not: " + error;
            }
            else if (record.FailedStores.Count > 0)
            {
                record.Error = String.Join("; ", ToArray(record.FailedStores));
            }

            record.Ok = written && record.FailedStores.Count == 0;

            if (written)
            {
                // The world on disk is now at this generation whether or not every store made it;
                // the manifest says which did not, and the next boot refuses on that entry.
                Current = generation;
            }

            LastSave = record;

            if (record.Ok)
            {
                Log.Info("Persistence: {0}.", record.Describe());
            }
            else
            {
                Log.Error("Persistence: {0}.", record.Describe());
            }
        }

        /// <summary>
        /// Every file under Saves/ except the custom stores (the manifest lists those with their
        /// hashes), the manifest itself, the ACCEPT marker, and the .tmp/.prev leftovers of atomic
        /// writes. Paths relative to the directory that holds Saves/, forward slashes, sorted.
        /// </summary>
        public static IList<WorldFileFingerprint> FingerprintWorld(string savesDir)
        {
            var list = new List<WorldFileFingerprint>();

            if (!Directory.Exists(savesDir))
            {
                return list;
            }

            string root = System.IO.Path.GetDirectoryName(savesDir) ?? savesDir;
            string customDir = System.IO.Path.Combine(savesDir, "Custom");

            string[] files = Directory.GetFiles(savesDir, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];

                if (file.StartsWith(customDir, StringComparison.OrdinalIgnoreCase)
                    && (file.Length == customDir.Length
                        || file[customDir.Length] == System.IO.Path.DirectorySeparatorChar
                        || file[customDir.Length] == System.IO.Path.AltDirectorySeparatorChar))
                {
                    continue;
                }

                if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".prev", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = new FileInfo(file);

                list.Add(new WorldFileFingerprint
                {
                    Path = Relative(root, file),
                    Length = info.Length,
                    LastWriteTicks = info.LastWriteTimeUtc.Ticks
                });
            }

            return list;
        }

        public static string BuildManifestJson(
            int generation, string bootId, DateTime utc, double seconds,
            IList<StoreSaveResult> stores, IList<WorldFileFingerprint> world)
        {
            var b = new StringBuilder(4096);

            b.Append("{\n");
            b.Append("  \"format\": ").Append(ManifestFormat).Append(",\n");
            b.Append("  \"generation\": ").Append(generation).Append(",\n");
            b.Append("  \"savedUtc\": ").Append(Json.Quote(utc.ToString("o"))).Append(",\n");
            b.Append("  \"bootId\": ").Append(Json.Quote(bootId)).Append(",\n");
            b.Append("  \"seconds\": ").Append(seconds.ToString("F3", CultureInfo.InvariantCulture)).Append(",\n");
            b.Append("  \"stores\": [");

            for (int i = 0; i < stores.Count; i++)
            {
                StoreSaveResult s = stores[i];

                b.Append(i == 0 ? "\n" : ",\n");
                b.Append("    {\"name\":").Append(Json.Quote(s.Name));
                b.Append(",\"path\":").Append(Json.Quote(s.Path));
                b.Append(",\"generation\":").Append(s.Generation);
                b.Append(",\"version\":").Append(s.Version);
                b.Append(",\"length\":").Append(s.Length);
                b.Append(",\"sha256\":").Append(Json.Quote(s.Sha256));
                b.Append(",\"ok\":").Append(s.Ok ? "true" : "false");
                b.Append(",\"error\":").Append(Json.Quote(s.Error));
                b.Append("}");
            }

            b.Append(stores.Count == 0 ? "],\n" : "\n  ],\n");
            b.Append("  \"world\": [");

            for (int i = 0; i < world.Count; i++)
            {
                WorldFileFingerprint w = world[i];

                b.Append(i == 0 ? "\n" : ",\n");
                b.Append("    {\"path\":").Append(Json.Quote(w.Path));
                b.Append(",\"length\":").Append(w.Length);
                b.Append(",\"lastWriteTicks\":").Append(w.LastWriteTicks);
                b.Append("}");
            }

            b.Append(world.Count == 0 ? "]\n" : "\n  ]\n");
            b.Append("}\n");

            return b.ToString();
        }

        /// <summary>Null when the file is missing; null with an error when it exists and cannot be read.</summary>
        public static Manifest ReadManifest(string fullPath, out string error)
        {
            error = null;

            if (!File.Exists(fullPath))
            {
                return null;
            }

            try
            {
                string text;

                using (var reader = new StreamReader(fullPath, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }

                JObject root;

                // DateParseHandling.None: Newtonsoft otherwise turns the ISO "savedUtc" string into a
                // DateTime and hands it back reformatted in the current culture, which is how a
                // refusal once printed "09/22/2026 02:38:52" for a time written as "...T02:38:52Z".
                using (var stringReader = new StringReader(text))
                using (var jsonReader = new Newtonsoft.Json.JsonTextReader(stringReader))
                {
                    jsonReader.DateParseHandling = Newtonsoft.Json.DateParseHandling.None;
                    root = JObject.Load(jsonReader);
                }

                var manifest = new Manifest
                {
                    Format = (int?)root["format"] ?? 0,
                    Generation = (int?)root["generation"] ?? 0,
                    SavedUtc = (string)root["savedUtc"],
                    BootId = (string)root["bootId"],
                    Seconds = (double?)root["seconds"] ?? 0.0
                };

                var stores = root["stores"] as JArray;

                if (stores != null)
                {
                    foreach (JToken token in stores)
                    {
                        manifest.Stores.Add(new ManifestStore
                        {
                            Name = (string)token["name"],
                            Path = (string)token["path"],
                            Generation = (int?)token["generation"] ?? -1,
                            Version = (int?)token["version"] ?? -1,
                            Length = (long?)token["length"] ?? -1,
                            Sha256 = (string)token["sha256"],
                            Ok = (bool?)token["ok"] ?? false,
                            Error = (string)token["error"]
                        });
                    }
                }

                var world = root["world"] as JArray;

                if (world != null)
                {
                    foreach (JToken token in world)
                    {
                        manifest.World.Add(new ManifestWorldFile
                        {
                            Path = (string)token["path"],
                            Length = (long?)token["length"] ?? -1,
                            LastWriteTicks = (long?)token["lastWriteTicks"] ?? -1
                        });
                    }
                }

                if (manifest.Format != ManifestFormat)
                {
                    error = String.Format(
                        CultureInfo.InvariantCulture,
                        "manifest format {0}; this build reads {1}", manifest.Format, ManifestFormat);
                    return null;
                }

                return manifest;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        // ---- the boot check ------------------------------------------------------------------------

        /// <summary>
        /// Judges a Saves/ tree against its own manifest and against the last backup's. Pure over the
        /// two directories - it opens nothing for longer than one read and writes nothing - so
        /// SaveIntegrity's fixtures run it on a scratch tree, and so AutoSave.Backup() can still move
        /// the real one afterwards.
        /// </summary>
        /// <param name="savesDir">The Saves directory, full path. Its parent is the root manifest paths are relative to.</param>
        /// <param name="previousDir">Backups/Automatic/Most Recent, full path, or null.</param>
        public static Verdict Verify(string savesDir, string previousDir)
        {
            var verdict = new Verdict();
            var refusals = new List<string>();

            string root = System.IO.Path.GetDirectoryName(savesDir) ?? savesDir;
            string customDir = System.IO.Path.Combine(savesDir, "Custom");
            string manifestPath = System.IO.Path.Combine(customDir, ManifestFileName);

            string manifestError;
            Manifest manifest = ReadManifest(manifestPath, out manifestError);

            string previousError = null;
            Manifest previous = null;

            if (!String.IsNullOrEmpty(previousDir))
            {
                previous = ReadManifest(
                    System.IO.Path.Combine(previousDir, "Custom", ManifestFileName), out previousError);
            }

            verdict.Manifest = manifest;
            verdict.Previous = previous;

            // Every store directory on disk, whatever the manifest says.
            var onDisk = new Dictionary<string, StampInfo>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(customDir))
            {
                string[] dirs = Directory.GetDirectories(customDir);
                Array.Sort(dirs, StringComparer.Ordinal);

                for (int i = 0; i < dirs.Length; i++)
                {
                    string file = System.IO.Path.Combine(dirs[i], "Persistence.bin");
                    onDisk[Relative(root, file)] = StampedFile.Inspect(file);
                }

                string[] leftovers = Directory.GetFiles(customDir, "*.tmp", SearchOption.AllDirectories);

                for (int i = 0; i < leftovers.Length; i++)
                {
                    verdict.Warnings.Add(String.Format(
                        CultureInfo.InvariantCulture,
                        "{0} is a leftover temporary file from an interrupted write; harmless, and " +
                        "overwritten by the next save.", Relative(root, leftovers[i])));
                }
            }

            bool worldFilesExist = File.Exists(System.IO.Path.Combine(savesDir, "Mobiles", "Mobiles.bin"))
                || File.Exists(System.IO.Path.Combine(savesDir, "Items", "Items.bin"));

            if (manifest == null && manifestError != null)
            {
                refusals.Add(Line(Relative(root, manifestPath), "present but unreadable: " + manifestError));
            }

            if (manifest == null)
            {
                JudgeWithoutManifest(verdict, refusals, root, onDisk, previous, previousError, worldFilesExist);
            }
            else
            {
                JudgeAgainstManifest(verdict, refusals, root, savesDir, manifest, onDisk);
            }

            if (refusals.Count > 0)
            {
                verdict.Kind = VerdictKind.Refused;
                verdict.Lines = refusals;
                verdict.Message = BuildRefusal(verdict, root, manifestPath, previous, previousError, refusals);
            }

            return verdict;
        }

        private static void JudgeWithoutManifest(
            Verdict verdict, List<string> refusals, string root,
            Dictionary<string, StampInfo> onDisk, Manifest previous, string previousError, bool worldFilesExist)
        {
            int stamped = 0;
            int legacy = 0;
            int maxGeneration = 0;

            foreach (KeyValuePair<string, StampInfo> pair in onDisk)
            {
                StampInfo info = pair.Value;

                switch (info.Kind)
                {
                    case StampKind.Stamped:
                        stamped++;
                        maxGeneration = Math.Max(maxGeneration, info.Generation);
                        refusals.Add(Line(pair.Key, String.Format(
                            CultureInfo.InvariantCulture,
                            "stamped generation {0}, but there is no manifest to say which save it belongs to",
                            info.Generation)));
                        break;

                    case StampKind.Legacy:
                        legacy++;
                        break;

                    case StampKind.HalfWritten:
                    case StampKind.Corrupt:
                        refusals.Add(Line(pair.Key, info.Kind + ": " + info.Detail));
                        break;
                }
            }

            if (stamped == 0 && previous != null)
            {
                refusals.Add(Line(ManifestRelativePath, "missing"));
                refusals.Add(Line(PreviousBackupRelativePath, String.Format(
                    CultureInfo.InvariantCulture,
                    "generation {0}, {1} - the save after it did not complete, so the world files in " +
                    "Saves/ are from a save that never wrote its manifest",
                    previous.Generation, previous.AllStoresOk ? "complete" : "incomplete")));
                verdict.Generation = previous.Generation + 1;
                return;
            }

            if (stamped > 0)
            {
                verdict.Generation = maxGeneration;
                return;
            }

            if (refusals.Count > 0)
            {
                verdict.Generation = 0;
                return;
            }

            if (previousError != null)
            {
                verdict.Warnings.Add(String.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/Custom/{1} exists but could not be read ({2}); the boot check could not use it.",
                    PreviousBackupRelativePath, ManifestFileName, previousError));
            }

            verdict.Generation = 0;
            verdict.Stores = legacy;

            if (!worldFilesExist && legacy == 0)
            {
                verdict.Kind = VerdictKind.Fresh;
                verdict.Summary = "Persistence: fresh world - nothing under Saves/ to verify; generation 0, the first save stamps 1.";
            }
            else
            {
                verdict.Kind = VerdictKind.PreUpgrade;
                verdict.Summary = String.Format(
                    CultureInfo.InvariantCulture,
                    "Persistence: pre-upgrade tree - {0} legacy store file(s) and no manifest anywhere; " +
                    "adopting generation 0, the first save stamps 1.", legacy);
            }
        }

        private static void JudgeAgainstManifest(
            Verdict verdict, List<string> refusals, string root, string savesDir,
            Manifest manifest, Dictionary<string, StampInfo> onDisk)
        {
            int m = manifest.Generation;
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < manifest.Stores.Count; i++)
            {
                ManifestStore entry = manifest.Stores[i];
                string display = entry.Path ?? ("Saves/Custom/" + entry.Name + "/Persistence.bin");
                listed.Add(display);

                StampInfo info;

                if (!onDisk.TryGetValue(display, out info))
                {
                    info = StampedFile.Inspect(System.IO.Path.Combine(root, display.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                }

                if (!entry.Ok)
                {
                    refusals.Add(Line(display, String.Format(
                        CultureInfo.InvariantCulture,
                        "generation {0} - this store FAILED during the save that produced generation {1} ({2}); " +
                        "the world is at {1}, the store is not",
                        entry.Generation, m, entry.Error ?? "no reason recorded")));
                    continue;
                }

                switch (info.Kind)
                {
                    case StampKind.Missing:
                        refusals.Add(Line(display, String.Format(
                            CultureInfo.InvariantCulture,
                            "missing (the manifest lists it at generation {0}, {1} bytes)", m, entry.Length)));
                        break;

                    case StampKind.Empty:
                        refusals.Add(Line(display, String.Format(
                            CultureInfo.InvariantCulture,
                            "zero bytes (the manifest lists it at generation {0}, {1} bytes)", m, entry.Length)));
                        break;

                    case StampKind.Legacy:
                        refusals.Add(Line(display, String.Format(
                            CultureInfo.InvariantCulture,
                            "legacy format with no stamp (the manifest expected generation {0})", m)));
                        break;

                    case StampKind.HalfWritten:
                    case StampKind.Corrupt:
                        refusals.Add(Line(display, String.Format(
                            CultureInfo.InvariantCulture,
                            "{0}: {1} (the manifest expected generation {2}, {3} bytes)",
                            info.Kind == StampKind.HalfWritten ? "half-written" : "corrupt",
                            info.Detail, m, entry.Length)));
                        break;

                    case StampKind.Stamped:
                        if (info.Generation != m)
                        {
                            refusals.Add(Line(display, String.Format(
                                CultureInfo.InvariantCulture,
                                "generation {0} (the manifest expected {1})", info.Generation, m)));
                        }
                        else if (info.ActualLength != entry.Length
                            || !String.Equals(info.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            refusals.Add(Line(display, String.Format(
                                CultureInfo.InvariantCulture,
                                "generation {0} but {1} bytes, hash {2}... (the manifest recorded {3} bytes, " +
                                "hash {4}...) - rewritten after the save",
                                m, info.ActualLength, Short(info.Sha256), entry.Length, Short(entry.Sha256))));
                        }
                        else
                        {
                            verdict.Stores++;
                        }

                        break;
                }
            }

            foreach (KeyValuePair<string, StampInfo> pair in onDisk)
            {
                if (listed.Contains(pair.Key))
                {
                    continue;
                }

                StampInfo info = pair.Value;

                switch (info.Kind)
                {
                    case StampKind.Stamped:
                        if (info.Generation != m)
                        {
                            refusals.Add(Line(pair.Key, String.Format(
                                CultureInfo.InvariantCulture,
                                "generation {0}, and the manifest (generation {1}) does not list it", info.Generation, m)));
                        }
                        else
                        {
                            verdict.Warnings.Add(String.Format(
                                CultureInfo.InvariantCulture,
                                "{0} is stamped generation {1} but the manifest does not list it.", pair.Key, m));
                        }

                        break;

                    case StampKind.Legacy:
                        refusals.Add(Line(pair.Key, String.Format(
                            CultureInfo.InvariantCulture,
                            "legacy format with no stamp beside a manifest at generation {0}", m)));
                        break;

                    case StampKind.HalfWritten:
                    case StampKind.Corrupt:
                        refusals.Add(Line(pair.Key, info.Kind + ": " + info.Detail));
                        break;

                    // Missing and Empty are a store this build added since the manifest was written:
                    // its first save will list it.
                }
            }

            for (int i = 0; i < manifest.World.Count; i++)
            {
                ManifestWorldFile entry = manifest.World[i];
                string full = System.IO.Path.Combine(root, (entry.Path ?? "").Replace('/', System.IO.Path.DirectorySeparatorChar));
                var file = new FileInfo(full);

                if (!file.Exists)
                {
                    refusals.Add(Line(entry.Path, String.Format(
                        CultureInfo.InvariantCulture,
                        "missing (generation {0} recorded {1} bytes written {2})",
                        m, entry.Length, Stamp(entry.LastWriteTicks))));
                    continue;
                }

                if (file.Length != entry.Length || file.LastWriteTimeUtc.Ticks != entry.LastWriteTicks)
                {
                    refusals.Add(Line(entry.Path, String.Format(
                        CultureInfo.InvariantCulture,
                        "{0} bytes written {1} (generation {2} recorded {3} bytes written {4})",
                        file.Length, Stamp(file.LastWriteTimeUtc.Ticks), m, entry.Length, Stamp(entry.LastWriteTicks))));
                    continue;
                }

                verdict.WorldFiles++;
            }

            IList<WorldFileFingerprint> now = FingerprintWorld(savesDir);
            var recorded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < manifest.World.Count; i++)
            {
                recorded.Add(manifest.World[i].Path ?? "");
            }

            for (int i = 0; i < now.Count; i++)
            {
                if (!recorded.Contains(now[i].Path))
                {
                    verdict.Warnings.Add(String.Format(
                        CultureInfo.InvariantCulture,
                        "{0} is under Saves/ but generation {1} did not record it.", now[i].Path, m));
                }
            }

            verdict.Generation = m;

            if (refusals.Count == 0)
            {
                verdict.Kind = VerdictKind.Verified;
                verdict.Summary = String.Format(
                    CultureInfo.InvariantCulture,
                    "Persistence: Saves/ verified at generation {0} - {1} store(s) and {2} world file(s) match the manifest written {3}.",
                    m, verdict.Stores, verdict.WorldFiles, manifest.SavedUtc ?? "(no time recorded)");
            }
        }

        private static string BuildRefusal(
            Verdict verdict, string root, string manifestPath, Manifest previous, string previousError, List<string> refusals)
        {
            var b = new StringBuilder(1024);

            b.Append("PERSISTENCE: refusing to load Saves/ - the world save and the custom files disagree.\n");

            if (verdict.Manifest != null)
            {
                b.Append(Line(Relative(root, manifestPath), String.Format(
                    CultureInfo.InvariantCulture,
                    "generation {0}, written {1}", verdict.Manifest.Generation, verdict.Manifest.SavedUtc ?? "(no time recorded)")));
                b.Append('\n');
            }

            for (int i = 0; i < refusals.Count; i++)
            {
                b.Append(refusals[i]).Append('\n');
            }

            if (previous != null)
            {
                b.Append(Line(PreviousBackupRelativePath, String.Format(
                    CultureInfo.InvariantCulture,
                    "generation {0}, {1}", previous.Generation, previous.AllStoresOk ? "complete" : "incomplete")));
            }
            else if (previousError != null)
            {
                b.Append(Line(PreviousBackupRelativePath, "has a manifest that could not be read: " + previousError));
            }
            else
            {
                b.Append(Line(PreviousBackupRelativePath, "no manifest (a pre-upgrade backup, or none)"));
            }

            b.Append('\n');
            b.Append("Nothing has been loaded or written. With the shard stopped, choose one:\n");
            b.Append("  1. Restore the last complete save: move Saves/ aside, copy ").Append(PreviousBackupRelativePath)
                .Append(" to Saves/, boot.\n");
            b.Append(String.Format(
                CultureInfo.InvariantCulture,
                "  2. Restore only the file(s) named above from a backup whose manifest says generation {0}.\n",
                verdict.Generation));
            b.Append(String.Format(
                CultureInfo.InvariantCulture,
                "  3. Load the tree exactly as it is: create Saves/Custom/{0} containing the single line {1} and boot.\n" +
                "     The tree loads as generation {1} and the next save is stamped {2}.\n",
                AcceptFileName, verdict.Generation, verdict.Generation + 1));
            b.Append("  (World.Load's own \"Delete the object?\" prompt rewrites Saves/*/*.idx and then exits; if that\n");
            b.Append("   ran last boot, a world-file line above is expected and choice 3 is the usual answer.)");

            return b.ToString();
        }

        // ---- health ------------------------------------------------------------------------------

        private static HealthResult BuildHealthResult()
        {
            var b = new StringBuilder(256);

            Verdict v = BootVerdict;

            if (v == null)
            {
                b.Append("boot: not verified");
            }
            else if (v.Accepted)
            {
                b.Append("boot: a refused tree was loaded through Saves/Custom/ACCEPT as generation ").Append(v.Generation);
            }
            else
            {
                switch (v.Kind)
                {
                    case VerdictKind.Fresh:
                        b.Append("boot: fresh world, generation 0");
                        break;
                    case VerdictKind.PreUpgrade:
                        b.Append("boot: pre-upgrade tree adopted at generation 0");
                        break;
                    default:
                        b.Append(String.Format(
                            CultureInfo.InvariantCulture,
                            "boot: verified generation {0} ({1} store(s), {2} world file(s))",
                            v.Generation, v.Stores, v.WorldFiles));
                        break;
                }
            }

            b.Append("; now at generation ").Append(Current);
            b.Append("; boot ").Append(BootId);

            SaveRecord last = LastSave;

            if (last == null)
            {
                b.Append("; no world save yet this boot");
                return HealthResult.Ok(b.ToString());
            }

            b.Append("; last save: ").Append(last.Describe());

            if (!last.Ok)
            {
                return HealthResult.Fail(b.ToString());
            }

            return HealthResult.Ok(b.ToString());
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static bool TryReadAccept(string path, out int generation, out string raw)
        {
            generation = -1;
            raw = null;

            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                using (var reader = new StreamReader(path, Encoding.UTF8))
                {
                    raw = (reader.ReadToEnd() ?? "").Trim();
                }

                Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out generation);
                return true;
            }
            catch
            {
                return raw != null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not delete {0}: {1}", path, ex.Message);
            }
        }

        /// <summary>"  path ......... status", aligned so the eye can run down the column.</summary>
        private static string Line(string path, string status)
        {
            const int width = 46;
            string dots = path.Length + 1 >= width ? " " : " " + new string('.', width - path.Length - 1);
            return "  " + path + dots + " " + status;
        }

        private static string Relative(string root, string full)
        {
            string result = full;

            if (!String.IsNullOrEmpty(root) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                result = full.Substring(root.Length).TrimStart('\\', '/');
            }

            return result.Replace('\\', '/');
        }

        private static string Stamp(long ticks)
        {
            if (ticks <= 0)
            {
                return "(unknown)";
            }

            return new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
        }

        private static string Short(string sha)
        {
            return String.IsNullOrEmpty(sha) ? "(none)" : (sha.Length > 8 ? sha.Substring(0, 8) : sha);
        }

        private static string[] ToArray(IList<string> list)
        {
            var array = new string[list.Count];
            list.CopyTo(array, 0);
            return array;
        }
    }
}
