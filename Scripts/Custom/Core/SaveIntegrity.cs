// SaveIntegrity.cs - a world save must not change the world.
//
// WHAT THIS CATCHES, AND WHY IT WAS NOT CAUGHT
// --------------------------------------------
// World.Save is synchronous on the core thread and fires EventSink.WorldSave once the strategy has
// finished serializing (Server/World.cs:1170). Anything a handler does at that moment happens to a
// world that has already been written down. The engine knows this and says so - World.AddItem and
// World.AddMobile refuse to add during a save, print
//
//     Warning: Attempted to add 0x... "BankBox" during world save.
//
// and defer the entity onto _addQueue, which ProcessSafetyQueues drains at Server/World.cs:1187
// (Server/World.cs:1247-1290, the message at :1001-1029).
//
// BotOrphans.WriteCensus did exactly that on every bot at every save for three days - 1,796 times
// across 11 and 12 September 2026 - because Mobile.BankBox is a CREATING getter
// (Server/Mobile.cs:10498-10516) and the census read it. Nothing failed. The console said so 1,796
// times and nobody's build went red, which is the whole reason this file exists: the engine's
// warning is a print, not an assertion, and a print scrolls past.
//
// THE THREE SIGNALS, AND WHY IT TAKES THREE
// -----------------------------------------
// 1. Serial.LastItem / Serial.LastMobile (Server/Serial.cs:14-15). These are monotonic allocation
//    counters, moved only by Serial.NewItem / NewMobile, which are read only from the Item and
//    Mobile constructors. Nothing else can advance them and deletion never does, so a non-zero
//    delta means an entity was constructed - a clean detector with no confound. This is the
//    primary assertion.
//
//    The SIZE of that delta is an upper bound rather than a count, and the report says so. Nothing
//    resets these counters at World.Load, so they start at 0x40000000 each boot and NewItem walks
//    forward past every serial already in use (Server/Serial.cs:33-42) - the delta therefore counts
//    the skips as well as the allocations. Measured on the defect this file was written for: 57
//    bank boxes constructed, counter advanced 74. The detector is exact; the tally is not, and
//    signal 3 is the one that counts.
//
// 2. The length of world-save-errors.log (Server/World.cs:1020). Append-only, and it contains
//    nothing but save-safety violations, so any growth across the window is one. It catches the
//    delete side too, which the counters cannot see.
//
// 3. The console tap, scanned for the warning's own words. This is the one that NAMES the entity,
//    which is what a reader actually needs, and it is scoped exactly by sampling
//    ConsoleTap.Sequence either side of the window.
//
// WHY NOT SIMPLY "World.Items.Count IS THE SAME BEFORE AND AFTER"
// --------------------------------------------------------------
// Because it legitimately is not. strategy.ProcessDecay() runs at Server/World.cs:1189 - after the
// deferred-add queue drains and before AfterWorldSave - and deletes every item the strategy queued
// as decayed. So the count can fall across the window on a perfectly healthy shard. It can never
// RISE, though: the game thread is frozen for the whole bracket (NetState.Pause at
// Server/World.cs:1112, Resume at :1198) and the Timer thread idles through it, so the only code
// that can add anything is a save handler. The count is therefore reported both ways and asserted
// in one: an increase fails, a decrease is named as decay.
//
// THE BRACKET is BeforeWorldSave to AfterWorldSave, the same pair ProcessHealth.cs:45-46 uses, and
// for the same reason - it is the only pair whose ends are unambiguous. The WorldSave event itself
// is no good here: handler order across reflection-invoked Configure() methods is undefined, so a
// sample taken there would be measuring some of its peers and not others.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Custom
{
    public static class SaveIntegrity
    {
        /// <summary>
        /// The engine's own words, trimmed to the part both messages share -
        /// "Attempted to add" and "Attempted to delete" (Server/World.cs:1001-1012).
        /// </summary>
        private const string SafetyNeedle = "Attempted to ";

        /// <summary>Written from Core.BaseDirectory by World.AppendSafetyLog (Server/World.cs:1020).</summary>
        private const string SafetyLogFile = "world-save-errors.log";

        /// <summary>
        /// A ceiling on how much of the ring we will ask for. The tap holds Custom.ConsoleTapLines
        /// (2000 by default); a window larger than that cannot be scoped, and saying so is better
        /// than silently scanning the wrong lines.
        /// </summary>
        private const int MaxWindowLines = 100000;

        private static bool _armed;

        private static int _itemsBefore;
        private static int _mobilesBefore;
        private static int _lastItemBefore;
        private static int _lastMobileBefore;
        private static long _safetyBytesBefore;
        private static long _tapSequenceBefore;

        private static HealthResult _last;

        /// <summary>How many saves this check has bracketed end to end this boot.</summary>
        public static int Saves { get; private set; }

        /// <summary>
        /// How far the entity serial counters moved across the last observed save. Zero is the
        /// contract; a non-zero value is an upper bound on how many entities were constructed.
        /// </summary>
        public static int LastCreated { get; private set; }

        /// <summary>Save-safety warnings printed during the last observed save. Zero is the contract.</summary>
        public static int LastWarnings { get; private set; }

        public static void Configure()
        {
            EventSink.BeforeWorldSave += OnBefore;
            EventSink.AfterWorldSave += OnAfter;

            HealthCheck.Register("Core.SaveIntegrity", BuildHealthResult);
        }

        private static void OnBefore(BeforeWorldSaveEventArgs e)
        {
            _itemsBefore = World.Items.Count;
            _mobilesBefore = World.Mobiles.Count;
            _lastItemBefore = Serial.LastItem.Value;
            _lastMobileBefore = Serial.LastMobile.Value;
            _safetyBytesBefore = SafetyLogBytes();
            _tapSequenceBefore = ConsoleTap.Sequence;

            _armed = true;
        }

        private static void OnAfter(AfterWorldSaveEventArgs e)
        {
            if (!_armed)
            {
                // A save that began before this handler was wired up. Nothing to compare against,
                // and inventing a baseline would report a number nobody measured.
                return;
            }

            _armed = false;
            Saves++;

            int itemsMade = Serial.LastItem.Value - _lastItemBefore;
            int mobilesMade = Serial.LastMobile.Value - _lastMobileBefore;
            int itemDelta = World.Items.Count - _itemsBefore;
            int mobileDelta = World.Mobiles.Count - _mobilesBefore;

            // Both ends or neither. SafetyLogBytes returns -1 for "could not read", and subtracting
            // that from a real length either way invents a number - an unreadable BEFORE against a
            // readable AFTER would report the whole log as this save's growth and fail a clean save.
            // An axis that could not be measured makes no claim; the serial counters carry the test.
            long safetyAfter = SafetyLogBytes();
            long safetyGrew = _safetyBytesBefore >= 0 && safetyAfter >= 0
                ? safetyAfter - _safetyBytesBefore
                : 0L;

            string firstOffender;
            int warned;
            bool scanned = TryScanTap(out warned, out firstOffender);

            LastCreated = itemsMade + mobilesMade;
            LastWarnings = warned;

            _last = Judge(
                itemsMade, mobilesMade, itemDelta, mobileDelta,
                safetyGrew, warned, scanned, firstOffender);
        }

        /// <summary>
        /// The verdict. Fail is reserved for a contract that is broken NOW - something was created,
        /// the engine logged a safety violation, or the world grew. Warn is for an axis that could
        /// not be measured, which is a different thing from one that came out clean.
        /// </summary>
        private static HealthResult Judge(
            int itemsMade, int mobilesMade, int itemDelta, int mobileDelta,
            long safetyGrew, int warned, bool scanned, string firstOffender)
        {
            var faults = new List<string>();

            if (itemsMade != 0 || mobilesMade != 0)
            {
                faults.Add(String.Format(
                    CultureInfo.InvariantCulture,
                    "an entity was constructed during the save - the serial counter advanced by "
                    + "{0} item(s) and {1} mobile(s), which is an upper bound (NewItem skips "
                    + "serials already in use)",
                    itemsMade, mobilesMade));
            }

            if (safetyGrew > 0)
            {
                faults.Add(String.Format(
                    CultureInfo.InvariantCulture,
                    "{0} byte(s) appended to {1}", safetyGrew, SafetyLogFile));
            }

            if (warned > 0)
            {
                faults.Add(String.Format(
                    CultureInfo.InvariantCulture,
                    "{0} save-safety warning(s) on the console; first: {1}",
                    warned, firstOffender ?? "(not captured)"));
            }

            if (itemDelta > 0 || mobileDelta > 0)
            {
                faults.Add(String.Format(
                    CultureInfo.InvariantCulture,
                    "the world grew across the save (+{0} item(s), +{1} mobile(s))",
                    Math.Max(0, itemDelta), Math.Max(0, mobileDelta)));
            }

            if (faults.Count > 0)
            {
                return HealthResult.Fail(String.Join("; ", faults.ToArray())
                    + ". A save handler is mutating the world - see Scripts/Custom/Core/SaveIntegrity.cs.");
            }

            string detail = String.Format(
                CultureInfo.InvariantCulture,
                "save {0}: nothing constructed, no safety warning, {1}",
                Saves, DescribeDelta(itemDelta, mobileDelta));

            if (!scanned)
            {
                return HealthResult.Warn(detail
                    + "; the console tap could not scope this save, so only the serial counters and "
                    + SafetyLogFile + " were read");
            }

            return HealthResult.Ok(detail);
        }

        private static string DescribeDelta(int itemDelta, int mobileDelta)
        {
            if (itemDelta == 0 && mobileDelta == 0)
            {
                return "world unchanged";
            }

            // Only a fall reaches here; a rise is a fault above. ProcessDecay is the one thing in
            // the bracket entitled to delete, and it runs at Server/World.cs:1189.
            return String.Format(
                CultureInfo.InvariantCulture,
                "{0} item(s) and {1} mobile(s) decayed away (ProcessDecay)",
                -itemDelta, -mobileDelta);
        }

        /// <summary>
        /// The console lines this save produced, counted for the engine's warning.
        ///
        /// Exact rather than approximate: AfterWorldSave runs on the game thread immediately after
        /// the save, so nothing has written since, and the newest (Sequence - before) lines ARE the
        /// window. False when the tap is off or the window outran the ring.
        /// </summary>
        private static bool TryScanTap(out int warned, out string first)
        {
            warned = 0;
            first = null;

            if (!ConsoleTap.Installed)
            {
                return false;
            }

            long produced = ConsoleTap.Sequence - _tapSequenceBefore;

            if (produced <= 0)
            {
                return true;
            }

            if (produced > MaxWindowLines)
            {
                return false;
            }

            IList<string> window = ConsoleTap.Tail((int)produced);

            if (window.Count < produced)
            {
                // The ring dropped some of the window. Whatever is left may still name the fault,
                // but the count would be a floor rather than a count, so it is not reported as one.
                return false;
            }

            for (int i = 0; i < window.Count; i++)
            {
                string line = window[i];

                if (line == null || line.IndexOf(SafetyNeedle, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                warned++;

                if (first == null)
                {
                    first = FirstLine(line);
                }
            }

            return true;
        }

        /// <summary>
        /// The safety banner is one WriteLine carrying three lines of text, so a report that quotes
        /// it whole is unreadable. The first line is the one with the serial and the type on it.
        /// </summary>
        private static string FirstLine(string text)
        {
            int cut = text.IndexOfAny(new[] { '\r', '\n' });

            return cut < 0 ? text : text.Substring(0, cut);
        }

        private static long SafetyLogBytes()
        {
            try
            {
                var info = new FileInfo(Path.Combine(Core.BaseDirectory, SafetyLogFile));

                return info.Exists ? info.Length : 0L;
            }
            catch
            {
                // Unreadable is not the same as absent, but neither gives a number to compare, and
                // a save must not be judged on one this check could not take.
                return -1L;
            }
        }

        private static HealthResult BuildHealthResult()
        {
            if (_last == null)
            {
                // Deliberately not Ok. A check that has observed nothing has nothing to stand on,
                // and saying "Ok" would let a clean [CoreSmoke] certify a contract never tested.
                return HealthResult.Warn("no world save observed yet this boot");
            }

            return _last;
        }

        // ---- fixtures ---------------------------------------------------------------------------
        //
        // THE OTHER HALF OF SAVE INTEGRITY, from 21 September 2026: not "did the save leave the world
        // alone" but "did the save leave the DISK consistent", and does the boot check refuse when it
        // did not. Each fixture builds a small Saves/ tree under the OS temp directory with the real
        // writers - StampedFile.Build, PersistenceGeneration.BuildManifestJson - breaks it in one
        // specific way, and asks PersistenceGeneration.Verify, the same function the boot runs. The
        // assertion is always the same shape: a refusal (or an unknown) rather than a silent load, and
        // the message names the file and both numbers. The scratch tree is removed afterwards, and
        // the fixture proves it left no handle open, because an open handle under the real Saves/
        // would turn AutoSave.Backup()'s directory move off with a warning nobody reads.

        /// <summary>Runs every fixture, appending to the [CoreSmoke report. True when all passed.</summary>
        public static bool RunFixtures(List<string> report)
        {
            report.Add("-- save integrity fixtures --");

            string scratch = Path.Combine(
                Path.GetTempPath(), "gg-fixtures-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            bool passed = true;

            try
            {
                passed &= FixtureHalfWritten(report, Path.Combine(scratch, "half"));
                passed &= FixtureGenerationMismatch(report, Path.Combine(scratch, "mismatch"));
                passed &= FixtureListedButMissing(report, Path.Combine(scratch, "missing"));
                passed &= FixtureListedButEmpty(report, Path.Combine(scratch, "empty"));
                passed &= FixtureWorldFingerprint(report, Path.Combine(scratch, "world"));
                passed &= FixtureFailedStoreEntry(report, Path.Combine(scratch, "failedstore"));
                passed &= FixtureIncompleteLastSave(report, Path.Combine(scratch, "incomplete"));
                passed &= FixtureConsistentTree(report, Path.Combine(scratch, "consistent"));
                passed &= FixtureRoundTrip(report, Path.Combine(scratch, "roundtrip"));
                passed &= FixtureInterruptedJob(report);
                passed &= FixtureGenerationAgreement(report);
            }
            catch (Exception ex)
            {
                report.Add("  FAIL: a fixture threw " + ex.GetType().Name + ": " + ex.Message);
                passed = false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(scratch))
                    {
                        Directory.Delete(scratch, true);
                    }
                }
                catch (Exception ex)
                {
                    report.Add("  WARN: the scratch tree " + scratch + " could not be removed: " + ex.Message);
                }
            }

            return passed;
        }

        /// <summary>A tree whose Saves/ holds fake world files, one stamped store and a manifest that matches, at the given generation.</summary>
        private static string BuildTree(string root, int generation, byte[] payload, out string storeRel, out string manifestPath)
        {
            string saves = Path.Combine(root, "Saves");
            string storeDir = Path.Combine(saves, "Custom", "Fixture");
            Directory.CreateDirectory(storeDir);
            Directory.CreateDirectory(Path.Combine(saves, "Items"));
            Directory.CreateDirectory(Path.Combine(saves, "Mobiles"));

            File.WriteAllBytes(Path.Combine(saves, "Items", "Items.bin"), new byte[] { 1, 2, 3, 4, 5 });
            File.WriteAllBytes(Path.Combine(saves, "Mobiles", "Mobiles.bin"), new byte[] { 9, 8, 7 });

            string storePath = Path.Combine(storeDir, "Persistence.bin");
            string sha;
            byte[] bytes = StampedFile.Build(generation, 0, DateTime.UtcNow, payload, out sha);
            File.WriteAllBytes(storePath, bytes);

            storeRel = "Saves/Custom/Fixture/Persistence.bin";

            var stores = new List<StoreSaveResult>
            {
                new StoreSaveResult
                {
                    Name = "Fixture", Path = storeRel, Ok = true, Generation = generation,
                    Version = 0, Length = bytes.Length, Sha256 = sha
                }
            };

            manifestPath = Path.Combine(saves, "Custom", PersistenceGeneration.ManifestFileName);
            WriteManifest(manifestPath, generation, stores, PersistenceGeneration.FingerprintWorld(saves));

            return saves;
        }

        private static void WriteManifest(string manifestPath, int generation, IList<StoreSaveResult> stores, IList<WorldFileFingerprint> world)
        {
            string json = PersistenceGeneration.BuildManifestJson(
                generation, "fixture", DateTime.UtcNow, 0.01, stores, world);
            File.WriteAllText(manifestPath, json, new System.Text.UTF8Encoding(false));
        }

        private static byte[] SamplePayload()
        {
            using (var ms = new MemoryStream())
            {
                var writer = new BinaryFileWriter(ms, true);
                writer.Write(12345);
                writer.Write("a payload the fixture can recognise");
                writer.Flush();
                return ms.ToArray();
            }
        }

        private static bool Expect(List<string> report, bool condition, string ok, string fail)
        {
            report.Add(condition ? "  ok: " + ok : "  FAIL: " + fail);
            return condition;
        }

        private static bool FixtureHalfWritten(List<string> report, string root)
        {
            string storeRel, manifestPath;
            string saves = BuildTree(root, 42, SamplePayload(), out storeRel, out manifestPath);
            string storePath = Path.Combine(saves, "Custom", "Fixture", "Persistence.bin");

            // The manifest recorded the whole file; now cut the file short, the way a crash between
            // the first byte and the last would - except that the atomic write makes this state
            // impossible for the live file, which is exactly why it has to be simulated.
            byte[] whole = File.ReadAllBytes(storePath);
            var cut = new byte[whole.Length - 10];
            Array.Copy(whole, cut, cut.Length);
            File.WriteAllBytes(storePath, cut);

            StampInfo info = StampedFile.Inspect(storePath);

            bool passed = Expect(report,
                info.Kind == StampKind.HalfWritten && info.ActualLength == cut.Length && info.DeclaredLength == whole.Length,
                String.Format(CultureInfo.InvariantCulture,
                    "a truncated store inspects as HalfWritten: {0} bytes on disk, {1} declared", info.ActualLength, info.DeclaredLength),
                "a truncated store inspected as " + info.Kind + " (" + info.Detail + ")");

            Verdict verdict = PersistenceGeneration.Verify(saves, null);

            passed &= Expect(report,
                !verdict.Ok && verdict.Message.Contains(storeRel) && verdict.Message.Contains("half-written")
                    && verdict.Message.Contains(cut.Length.ToString(CultureInfo.InvariantCulture))
                    && verdict.Message.Contains(whole.Length.ToString(CultureInfo.InvariantCulture)),
                "Verify refuses the tree and names the file with both lengths",
                "Verify did not refuse a half-written store, or the message lacks the file or the lengths: "
                    + (verdict.Ok ? verdict.Summary : verdict.Message));

            return passed;
        }

        private static bool FixtureGenerationMismatch(List<string> report, string root)
        {
            string storeRel, manifestPath;
            string saves = BuildTree(root, 42, SamplePayload(), out storeRel, out manifestPath);
            string storePath = Path.Combine(saves, "Custom", "Fixture", "Persistence.bin");

            // The manifest says 42; the store on disk says 41 - a single file restored from an older
            // backup, or a store that was written by the save before the one the world is from.
            string sha;
            File.WriteAllBytes(storePath, StampedFile.Build(41, 0, DateTime.UtcNow, SamplePayload(), out sha));

            Verdict verdict = PersistenceGeneration.Verify(saves, null);

            return Expect(report,
                !verdict.Ok && verdict.Message.Contains(storeRel)
                    && verdict.Message.Contains("generation 41") && verdict.Message.Contains("expected 42")
                    && verdict.Generation == 42,
                "a store stamped 41 under a manifest at 42 is refused, naming the file and both generations",
                "the generation mismatch was not refused as expected: " + (verdict.Ok ? verdict.Summary : verdict.Message));
        }

        private static bool FixtureListedButMissing(List<string> report, string root)
        {
            string storeRel, manifestPath;
            string saves = BuildTree(root, 42, SamplePayload(), out storeRel, out manifestPath);

            File.Delete(Path.Combine(saves, "Custom", "Fixture", "Persistence.bin"));

            Verdict verdict = PersistenceGeneration.Verify(saves, null);

            return Expect(report,
                !verdict.Ok && verdict.Message.Contains(storeRel) && verdict.Message.Contains("missing"),
                "a store the manifest lists but the disk lacks is refused",
                "a missing listed store was not refused: " + (verdict.Ok ? verdict.Summary : verdict.Message));
        }

        private static bool FixtureListedButEmpty(List<string> report, string root)
        {
            string storeRel, manifestPath;
            string saves = BuildTree(root, 42, SamplePayload(), out storeRel, out manifestPath);

            File.WriteAllBytes(Path.Combine(saves, "Custom", "Fixture", "Persistence.bin"), new byte[0]);

            Verdict verdict = PersistenceGeneration.Verify(saves, null);

            return Expect(report,
                !verdict.Ok && verdict.Message.Contains(storeRel) && verdict.Message.Contains("zero bytes"),
                "a listed store that is zero bytes is refused (a first-boot file is only fine for a store the manifest never saw)",
                "an empty listed store was not refused: " + (verdict.Ok ? verdict.Summary : verdict.Message));
        }

        private static bool FixtureWorldFingerprint(List<string> report, string root)
        {
            string storeRel, manifestPath;
            string saves = BuildTree(root, 42, SamplePayload(), out storeRel, out manifestPath);

            // The world file grows by a byte after the manifest recorded it: a world from a later or
            // earlier save than the one the custom files belong to.
            string items = Path.Combine(saves, "Items", "Items.bin");
            File.WriteAllBytes(items, new byte[] { 1, 2, 3, 4, 5, 6 });

            Verdict verdict = PersistenceGeneration.Verify(saves, null);

            return Expect(report,
                !verdict.Ok && verdict.Message.Contains("Saves/Items/Items.bin")
                    && verdict.Message.Contains("6 bytes") && verdict.Message.Contains("recorded 5 bytes"),
                "a world file whose length differs from the manifest is refused, both sizes named",
                "the world fingerprint mismatch was not refused as expected: " + (verdict.Ok ? verdict.Summary : verdict.Message));
        }

        private static bool FixtureFailedStoreEntry(List<string> report, string root)
        {
            string storeRel, manifestPath;
            string saves = BuildTree(root, 42, SamplePayload(), out storeRel, out manifestPath);

            // What a save leaves when a store's SerializeCore threw: the file is the previous good
            // generation, untouched, and the manifest says so.
            string sha;
            byte[] old = StampedFile.Build(41, 0, DateTime.UtcNow, SamplePayload(), out sha);
            File.WriteAllBytes(Path.Combine(saves, "Custom", "Fixture", "Persistence.bin"), old);

            var stores = new List<StoreSaveResult>
            {
                new StoreSaveResult
                {
                    Name = "Fixture", Path = storeRel, Ok = false, Error = "save failed: fixture exception",
                    Generation = 41, Version = 0, Length = old.Length, Sha256 = sha
                }
            };

            WriteManifest(manifestPath, 42, stores, PersistenceGeneration.FingerprintWorld(saves));

            Verdict verdict = PersistenceGeneration.Verify(saves, null);

            return Expect(report,
                !verdict.Ok && verdict.Message.Contains(storeRel) && verdict.Message.Contains("FAILED")
                    && verdict.Message.Contains("fixture exception") && verdict.Message.Contains("generation 41"),
                "a manifest entry for a store that failed during the save is refused, quoting the save's reason",
                "the failed-store entry was not refused as expected: " + (verdict.Ok ? verdict.Summary : verdict.Message));
        }

        private static bool FixtureIncompleteLastSave(List<string> report, string root)
        {
            // Saves/ has world files and no manifest, but the last backup has one: the save after it
            // began (the rotation moved the old tree away) and never wrote its manifest.
            string storeRel, manifestPath;
            string previousSaves = BuildTree(Path.Combine(root, "backup"), 41, SamplePayload(), out storeRel, out manifestPath);
            string previousDir = Path.GetDirectoryName(previousSaves);

            string saves = Path.Combine(root, "Saves");
            Directory.CreateDirectory(Path.Combine(saves, "Items"));
            File.WriteAllBytes(Path.Combine(saves, "Items", "Items.bin"), new byte[] { 1 });

            Verdict verdict = PersistenceGeneration.Verify(saves, previousDir);

            return Expect(report,
                !verdict.Ok && verdict.Message.Contains("did not complete") && verdict.Message.Contains("generation 41")
                    && verdict.Generation == 42,
                "a Saves/ with no manifest beside a backup at 41 is refused as an incomplete save, proposing 42",
                "the incomplete last save was not refused as expected: " + (verdict.Ok ? verdict.Summary : verdict.Message));
        }

        private static bool FixtureConsistentTree(List<string> report, string root)
        {
            string storeRel, manifestPath;
            string saves = BuildTree(root, 42, SamplePayload(), out storeRel, out manifestPath);

            // A new store this build added since the manifest (zero bytes, unlisted), a leftover
            // .tmp and a .prev beside the live file: all fine, one warning.
            Directory.CreateDirectory(Path.Combine(saves, "Custom", "Newcomer"));
            File.WriteAllBytes(Path.Combine(saves, "Custom", "Newcomer", "Persistence.bin"), new byte[0]);
            File.WriteAllBytes(Path.Combine(saves, "Custom", "Fixture", "Persistence.bin.tmp"), new byte[] { 1, 2 });
            File.WriteAllBytes(Path.Combine(saves, "Custom", "Fixture", "Persistence.bin.prev"), new byte[] { 3, 4 });

            Verdict verdict = PersistenceGeneration.Verify(saves, null);

            bool passed = Expect(report,
                verdict.Ok && verdict.Kind == VerdictKind.Verified && verdict.Generation == 42 && verdict.Stores == 1,
                "a consistent tree verifies at its generation with a new empty store, a .tmp and a .prev beside it",
                "a consistent tree was not verified: " + (verdict.Ok ? verdict.Summary : verdict.Message));

            passed &= Expect(report,
                verdict.Warnings.Count == 1 && verdict.Warnings[0].Contains(".tmp"),
                "and the leftover .tmp is a warning, not a refusal",
                "expected exactly one warning about the .tmp, got " + verdict.Warnings.Count);

            // No handle left open: the real Saves/ has to be movable by AutoSave.Backup() after the
            // boot check has looked at it.
            string moved = root + ".moved";
            bool movable;

            try
            {
                Directory.Move(root, moved);
                movable = true;
                Directory.Move(moved, root);
            }
            catch (Exception ex)
            {
                movable = false;
                report.Add("  (move failed: " + ex.Message + ")");
            }

            passed &= Expect(report, movable,
                "Verify left no handle open - the tree could be moved afterwards, as AutoSave.Backup() must",
                "the tree could not be moved after Verify: a handle was left open");

            return passed;
        }

        private static bool FixtureRoundTrip(List<string> report, string root)
        {
            Directory.CreateDirectory(root);

            string path = Path.Combine(root, "Persistence.bin");
            byte[] payload = SamplePayload();
            string sha;

            File.WriteAllBytes(path, StampedFile.Build(7, 3, DateTime.UtcNow, payload, out sha));

            StampInfo info;
            byte[] back;
            string error;

            bool read = StampedFile.TryReadPayload(path, out info, out back, out error);

            bool same = read && back.Length == payload.Length;

            for (int i = 0; same && i < back.Length; i++)
            {
                same = back[i] == payload[i];
            }

            int number = 0;
            string text = null;

            if (same)
            {
                using (var ms = new MemoryStream(back, false))
                {
                    var reader = new BinaryFileReader(new BinaryReader(ms));
                    number = reader.ReadInt();
                    text = reader.ReadString();
                }
            }

            return Expect(report,
                same && info.Generation == 7 && info.StoreVersion == 3 && info.Sha256 == sha
                    && number == 12345 && text == "a payload the fixture can recognise",
                "a stamped write at generation 7, store version 3, reads back byte for byte with both numbers",
                "the round trip failed: " + (read ? "payload or header differs" : error));
        }

        private static bool FixtureInterruptedJob(List<string> report)
        {
            bool passed = true;

            // 1. The real path: a background thread posts work and waits 100 ms. This method runs ON
            //    the game thread, so the drain cannot run until [CoreSmoke returns, the job is still
            //    queued when the wait expires, and the waiter withdraws it: NotRun, never ran.
            LoopOutcome real = LoopOutcome.Completed;
            bool realOk = true;
            string realError = null;
            bool ran = false;

            var thread = new Thread(() =>
            {
                int value;
                realOk = LoopQueue.TryPostAndWait(
                    () => { ran = true; return 1; },
                    TimeSpan.FromMilliseconds(100), out value, out realError, out real);
            });

            thread.IsBackground = true;
            thread.Start();

            bool joined = thread.Join(TimeSpan.FromSeconds(5));

            passed &= Expect(report,
                joined && !realOk && real == LoopOutcome.NotRun && !ran,
                "a waiter that times out while its job is still queued gets NotRun, and the job never runs: "
                    + (realError ?? ""),
                String.Format("expected NotRun from a timed-out waiter; got ok={0} outcome={1} ran={2} joined={3} ({4})",
                    realOk, real, ran, joined, realError));

            // 2. The waiter gave up while the drain had the job: Unknown. Built by hand, no threads.
            var running = new TaskCompletionSource<LoopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            LoopJob job = LoopQueue.CreateDetached(() => { }, running);
            bool began = job.TryBeginRun();
            LoopOutcome judged = LoopQueue.JudgeTimeout(job, running.Task);

            passed &= Expect(report,
                began && judged == LoopOutcome.Unknown && !job.TryAbandon(),
                "a waiter that times out while its job is running gets Unknown, and cannot withdraw it",
                "expected Unknown for a running job; got " + judged);

            // 3. The job finished in the instant between the wait expiring and the judgement: the
            //    task is complete, so the real outcome is reported, not Unknown.
            var finished = new TaskCompletionSource<LoopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            LoopJob done = LoopQueue.CreateDetached(() => { }, finished);
            done.TryBeginRun();
            finished.TrySetResult(LoopOutcome.Completed);
            done.Finish(false);

            passed &= Expect(report,
                LoopQueue.JudgeTimeout(done, finished.Task) == LoopOutcome.Completed && done.Outcome == LoopOutcome.Completed,
                "a job that completed as the wait expired is reported Completed, not Unknown",
                "a completed job was judged " + LoopQueue.JudgeTimeout(done, finished.Task));

            // 4. A queued job the shard shuts down under: never started, so NotRun, and its handle says so.
            LoopJob orphan = LoopQueue.CreateDetached(() => { }, null);

            passed &= Expect(report,
                orphan.TryOrphan() && orphan.Outcome == LoopOutcome.NotRun && !orphan.TryBeginRun(),
                "a job orphaned by a shutdown reports NotRun and can no longer be started",
                "an orphaned job did not report NotRun");

            return passed;
        }

        /// <summary>
        /// The generation on both sides of the round trip that matters: what the boot check verified
        /// against what every store loaded, and after a save this boot, what the manifest says
        /// against what every store wrote.
        /// </summary>
        private static bool FixtureGenerationAgreement(List<string> report)
        {
            Verdict boot = PersistenceGeneration.BootVerdict;

            if (boot == null)
            {
                report.Add("  FAIL: PersistenceGeneration has no boot verdict.");
                return false;
            }

            var stores = new List<string>();
            bool agree = true;

            foreach (CustomPersistence store in CustomPersistence.Stores)
            {
                string state;

                if (store.LoadedGeneration.HasValue)
                {
                    state = store.LoadedGeneration.Value.ToString(CultureInfo.InvariantCulture);
                    agree &= boot.Kind != VerdictKind.Verified || store.LoadedGeneration.Value == boot.Generation;
                }
                else if (store.LoadedLegacy)
                {
                    state = "legacy";
                    agree &= boot.Kind != VerdictKind.Verified;
                }
                else
                {
                    state = "no file";
                }

                stores.Add(store.Name + "=" + state);
            }

            string bootWord = boot.Accepted ? "accepted" : boot.Kind.ToString().ToLowerInvariant();

            bool passed = Expect(report, agree,
                String.Format(CultureInfo.InvariantCulture,
                    "boot {0} generation {1}; stores loaded {2}", bootWord, boot.Generation, String.Join(" ", stores.ToArray())),
                String.Format(CultureInfo.InvariantCulture,
                    "boot {0} generation {1} but the stores loaded {2}", bootWord, boot.Generation, String.Join(" ", stores.ToArray())));

            SaveRecord last = PersistenceGeneration.LastSave;

            if (last == null)
            {
                report.Add("  no world save yet this boot; run [save and [CoreSmoke again for the save-side agreement.");
                return passed;
            }

            bool saveAgrees = last.Ok && last.Generation == PersistenceGeneration.Current;
            var written = new List<string>();

            foreach (CustomPersistence store in CustomPersistence.Stores)
            {
                written.Add(store.Name + "=" + (store.LastSavedGeneration.HasValue
                    ? store.LastSavedGeneration.Value.ToString(CultureInfo.InvariantCulture)
                    : "not saved"));
                saveAgrees &= store.IsDegraded || (store.LastSavedGeneration.HasValue && store.LastSavedGeneration.Value == last.Generation);
            }

            passed &= Expect(report, saveAgrees,
                String.Format(CultureInfo.InvariantCulture,
                    "last save {0}; now at generation {1}; stores wrote {2}",
                    last.Describe(), PersistenceGeneration.Current, String.Join(" ", written.ToArray())),
                String.Format(CultureInfo.InvariantCulture,
                    "last save {0} but current is {1} and the stores wrote {2}",
                    last.Describe(), PersistenceGeneration.Current, String.Join(" ", written.ToArray())));

            return passed;
        }
    }
}
