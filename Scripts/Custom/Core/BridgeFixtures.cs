// BridgeFixtures.cs - the shard's half of the request protocol, proved on a scratch tree.
//
// tools/editor/bridge.test.js proves the bridge against a fake shard that speaks the same file
// protocol; these prove the real C# side - DataFileCommit, DataFileLedger, the poller's token-name
// parse, its boot sweep and its ack text - without a bridge, a browser or the live data files.
// Every fixture builds its own files under the OS temp directory with the real writers and reads
// back what they did. Nothing here touches Data/ or Spawns/, and no fixture calls a real reload:
// the target's Reload is null, which is what `reload=no` means in production too.
//
// Run from [CoreSmoke (CoreSmoke.Finish), after the save-integrity fixtures.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Newtonsoft.Json.Linq;

namespace Server.Custom
{
    public static class BridgeFixtures
    {
        public static bool RunFixtures(List<string> report)
        {
            report.Add("-- bridge protocol fixtures --");

            string scratch = Path.Combine(
                Path.GetTempPath(), "gg-bridge-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            bool passed = true;

            try
            {
                passed &= FixtureHashParity(report);
                passed &= FixtureCommitFresh(report, Path.Combine(scratch, "fresh"));
                passed &= FixtureCommitStale(report, Path.Combine(scratch, "stale"));
                passed &= FixtureCommitHalfStaged(report, Path.Combine(scratch, "half"));
                passed &= FixtureCommitCreate(report, Path.Combine(scratch, "create"));
                passed &= FixtureRestoreChanged(report, Path.Combine(scratch, "restore-changed"));
                passed &= FixtureRestoreWrongBackup(report, Path.Combine(scratch, "restore-backup"));
                passed &= FixtureRestoreFresh(report, Path.Combine(scratch, "restore-fresh"));
                passed &= FixtureTokenNames(report);
                passed &= FixtureSweep(report, Path.Combine(scratch, "sweep"));
                passed &= FixtureAckText(report);
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

        // ---- helpers ---------------------------------------------------------------------------

        private static bool Expect(List<string> report, bool condition, string ok, string fail)
        {
            report.Add(condition ? "  ok: " + ok : "  FAIL: " + fail);
            return condition;
        }

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>A target on the scratch tree, with no reload - the fixtures test the file half.</summary>
        private static DataFileTarget Target(string root, string fileName)
        {
            Directory.CreateDirectory(root);

            return new DataFileTarget
            {
                Key = "fixture",
                RelativePath = Path.Combine(root, fileName),
                FullPath = Path.Combine(root, fileName),
                Display = fileName,
                ReloadArgument = null,
                Reload = null
            };
        }

        private static void Put(string path, string text)
        {
            File.WriteAllText(path, text, Utf8);
        }

        private static string Read(string path)
        {
            return File.Exists(path) ? File.ReadAllText(path, Utf8) : null;
        }

        private static string Stage(DataFileTarget target, string id, string text)
        {
            string staged = DataFileCommit.StagedPath(target, id);
            Put(staged, text);
            return staged;
        }

        // ---- fixtures --------------------------------------------------------------------------

        /// <summary>The C# hash and the bridge's hashOf agree, by construction: the same bytes, the same prefix.</summary>
        private static bool FixtureHashParity(List<string> report)
        {
            bool passed = Expect(report,
                DataFileCommit.Hash16(new byte[0]) == DataFileCommit.EmptyHash,
                "the hash of zero bytes is " + DataFileCommit.EmptyHash + ", which is hashOf('') in bridge.js",
                "the hash of zero bytes is " + DataFileCommit.Hash16(new byte[0]));

            // sha256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
            passed &= Expect(report,
                DataFileCommit.Hash16Text("abc") == "ba7816bf8f01cfea",
                "Hash16Text hashes the UTF-8 bytes without a BOM (sha256 of 'abc' begins ba7816bf8f01cfea)",
                "Hash16Text('abc') gave " + DataFileCommit.Hash16Text("abc"));

            return passed;
        }

        private static bool FixtureCommitFresh(List<string> report, string root)
        {
            DataFileLedger.Clear();

            DataFileTarget target = Target(root, "navigation.json");
            Put(target.FullPath, "{\"old\": 1}\n");

            string oldHash = DataFileCommit.Hash16(target.FullPath);
            string staged = Stage(target, "abc123", "{\"new\": 2}\n");
            string newHash = DataFileCommit.Hash16(staged);

            CommitResult result;
            bool ok = DataFileCommit.TryCommit(
                target, new CommitSpec { Id = "abc123", Base = oldHash, Wrote = newHash, Reload = false }, out result);

            bool passed = Expect(report,
                ok && result.Ok && result.Written && !result.Refused,
                "a commit based on the live version is written",
                "a fresh commit was not written: " + result.Message);

            passed &= Expect(report,
                Read(target.FullPath) == "{\"new\": 2}\n" && Read(target.FullPath + ".bak") == "{\"old\": 1}\n",
                "the live file is the staged bytes and the .bak is the previous live file",
                "live=" + Read(target.FullPath) + " bak=" + Read(target.FullPath + ".bak"));

            passed &= Expect(report,
                result.Hash == newHash && result.BackupHash == oldHash && !File.Exists(staged),
                String.Format("the result says hash {0} and backupHash {1}, and the staged file is gone", newHash, oldHash),
                String.Format("hash={0} backupHash={1} staged exists={2}", result.Hash, result.BackupHash, File.Exists(staged)));

            passed &= Expect(report,
                DataFileLedger.ChangedBy(target.FullPath, newHash) == "editor commit abc123",
                "the ledger names the commit as the file's last writer",
                "the ledger says " + DataFileLedger.ChangedBy(target.FullPath, newHash));

            return passed;
        }

        private static bool FixtureCommitStale(List<string> report, string root)
        {
            DataFileLedger.Clear();

            DataFileTarget target = Target(root, "navigation.json");
            Put(target.FullPath, "{\"live\": 1}\n");

            string liveHash = DataFileCommit.Hash16(target.FullPath);

            // Somebody wrote the file since the save was based on 'deadbeefdeadbeef'; the ledger
            // knows who, the way JsonConfig.TrySaveToken tells it.
            DataFileLedger.Note(target.FullPath, liveHash, "the shard (NavigationSystem.Save)");

            string staged = Stage(target, "def456", "{\"mine\": 2}\n");

            CommitResult result;
            bool ok = DataFileCommit.TryCommit(
                target, new CommitSpec { Id = "def456", Base = "deadbeefdeadbeef", Wrote = DataFileCommit.Hash16(staged), Reload = false },
                out result);

            bool passed = Expect(report,
                !ok && result.Refused && !result.Written,
                "a commit based on an older version is refused",
                "a stale commit was not refused: " + result.Message);

            passed &= Expect(report,
                Read(target.FullPath) == "{\"live\": 1}\n" && !File.Exists(target.FullPath + ".bak") && !File.Exists(staged),
                "nothing was written, no .bak was made, and the staged file was discarded",
                String.Format("live={0} bak exists={1} staged exists={2}",
                    Read(target.FullPath), File.Exists(target.FullPath + ".bak"), File.Exists(staged)));

            passed &= Expect(report,
                result.Message.Contains(liveHash) && result.Message.Contains("deadbeefdeadbeef")
                    && result.Message.Contains("NavigationSystem.Save") && result.Message.Contains("last written "),
                "the refusal names both hashes, the writer and the time: " + result.Message,
                "the refusal is missing a hash, the writer or the time: " + result.Message);

            passed &= Expect(report,
                result.Actual == liveHash && result.Expected == "deadbeefdeadbeef"
                    && result.ChangedBy == "the shard (NavigationSystem.Save)" && result.ChangedAt != null,
                "the result carries actual, expected, changedBy and changedAt",
                String.Format("actual={0} expected={1} changedBy={2} changedAt={3}",
                    result.Actual, result.Expected, result.ChangedBy, result.ChangedAt));

            return passed;
        }

        private static bool FixtureCommitHalfStaged(List<string> report, string root)
        {
            DataFileLedger.Clear();

            DataFileTarget target = Target(root, "restricted-zones.json");
            Put(target.FullPath, "{\"live\": 1}\n");

            string liveHash = DataFileCommit.Hash16(target.FullPath);
            string staged = Stage(target, "aaa111", "{\"half");

            CommitResult result;
            bool ok = DataFileCommit.TryCommit(
                target, new CommitSpec { Id = "aaa111", Base = liveHash, Wrote = "0123456789abcdef", Reload = false }, out result);

            return Expect(report,
                !ok && result.Refused && !result.Written && Read(target.FullPath) == "{\"live\": 1}\n" && !File.Exists(staged)
                    && result.Message.Contains("0123456789abcdef"),
                "a staged file that does not hash to what the bridge said it wrote is refused and discarded",
                "a half-staged commit was not refused cleanly: " + result.Message);
        }

        private static bool FixtureCommitCreate(List<string> report, string root)
        {
            DataFileLedger.Clear();

            DataFileTarget target = Target(root, "GG_New.xml");
            string staged = Stage(target, "bbb222", "<Spawns>\n</Spawns>");

            CommitResult result;
            bool ok = DataFileCommit.TryCommit(
                target, new CommitSpec { Id = "bbb222", Base = DataFileCommit.NoFile, Wrote = DataFileCommit.Hash16(staged), Reload = false },
                out result);

            bool passed = Expect(report,
                ok && result.Written && Read(target.FullPath) == "<Spawns>\n</Spawns>" && !File.Exists(target.FullPath + ".bak")
                    && result.BackupHash == DataFileCommit.NoFile,
                "a commit based on 'none' creates the file with no .bak and reports backupHash none",
                "creating a file failed: " + result.Message);

            // And a second create onto the file that now exists is refused: 'none' was the base.
            string again = Stage(target, "bbb333", "<Spawns>\n</Spawns>");

            CommitResult second;
            bool okAgain = DataFileCommit.TryCommit(
                target, new CommitSpec { Id = "bbb333", Base = DataFileCommit.NoFile, Wrote = DataFileCommit.Hash16(again), Reload = false },
                out second);

            passed &= Expect(report,
                !okAgain && second.Refused && second.Message.Contains("already exists"),
                "a create onto a file that now exists is refused: " + second.Message,
                "a second create was not refused: " + second.Message);

            return passed;
        }

        private static bool FixtureRestoreChanged(List<string> report, string root)
        {
            DataFileLedger.Clear();

            DataFileTarget target = Target(root, "navigation.json");
            Put(target.FullPath, "{\"live\": 2}\n");
            Put(target.FullPath + ".bak", "{\"old\": 1}\n");

            string backupHash = DataFileCommit.Hash16(target.FullPath + ".bak");

            CommitResult result;
            bool ok = DataFileCommit.TryRestore(
                target, new RestoreSpec { Id = "ccc111", Base = "deadbeefdeadbeef", Backup = backupHash, Reload = false }, out result);

            return Expect(report,
                !ok && result.Refused && Read(target.FullPath) == "{\"live\": 2}\n" && Read(target.FullPath + ".bak") == "{\"old\": 1}\n"
                    && result.Message.Contains("this restore was based on"),
                "a restore over a file that changed since is refused and touches nothing: " + result.Message,
                "a restore over a changed file was not refused: " + result.Message);
        }

        private static bool FixtureRestoreWrongBackup(List<string> report, string root)
        {
            DataFileLedger.Clear();

            DataFileTarget target = Target(root, "navigation.json");
            Put(target.FullPath, "{\"live\": 2}\n");
            Put(target.FullPath + ".bak", "{\"somebody-elses\": 1}\n");

            string liveHash = DataFileCommit.Hash16(target.FullPath);

            CommitResult result;
            bool ok = DataFileCommit.TryRestore(
                target, new RestoreSpec { Id = "ccc222", Base = liveHash, Backup = "deadbeefdeadbeef", Reload = false }, out result);

            bool passed = Expect(report,
                !ok && result.Refused && Read(target.FullPath) == "{\"live\": 2}\n"
                    && result.Message.Contains(".bak is at") && result.Actual == DataFileCommit.Hash16(target.FullPath + ".bak"),
                "a restore whose .bak is not the backup the caller meant is refused: " + result.Message,
                "a restore with the wrong backup was not refused: " + result.Message);

            File.Delete(target.FullPath + ".bak");

            CommitResult none;
            bool okNone = DataFileCommit.TryRestore(
                target, new RestoreSpec { Id = "ccc333", Base = liveHash, Backup = "deadbeefdeadbeef", Reload = false }, out none);

            passed &= Expect(report,
                !okNone && none.Refused && none.Message.Contains("no navigation.json.bak"),
                "a restore with no .bak says so: " + none.Message,
                "a restore with no .bak was not refused: " + none.Message);

            return passed;
        }

        private static bool FixtureRestoreFresh(List<string> report, string root)
        {
            DataFileLedger.Clear();

            DataFileTarget target = Target(root, "GG_Town.xml");
            Put(target.FullPath, "<Spawns>new</Spawns>");
            Put(target.FullPath + ".bak", "<Spawns>old</Spawns>");

            string liveHash = DataFileCommit.Hash16(target.FullPath);
            string backupHash = DataFileCommit.Hash16(target.FullPath + ".bak");

            CommitResult result;
            bool ok = DataFileCommit.TryRestore(
                target, new RestoreSpec { Id = "ddd111", Base = liveHash, Backup = backupHash, Reload = false }, out result);

            bool passed = Expect(report,
                ok && result.Written && Read(target.FullPath) == "<Spawns>old</Spawns>",
                "a restore based on both versions puts the .bak back",
                "a fresh restore failed: " + result.Message);

            // The replaced live file is the new .bak: that is what spawn-reload has to unload.
            passed &= Expect(report,
                Read(target.FullPath + ".bak") == "<Spawns>new</Spawns>" && result.BackupHash == liveHash && result.Hash == backupHash
                    && !File.Exists(DataFileCommit.StagedPath(target, "ddd111")),
                "the file it replaced is now the .bak (backupHash " + liveHash + ") and no staged copy is left",
                String.Format("bak={0} backupHash={1} hash={2}", Read(target.FullPath + ".bak"), result.BackupHash, result.Hash));

            return passed;
        }

        private static bool FixtureTokenNames(List<string> report)
        {
            string name, id;

            bool passed = Expect(report,
                RequestPoller.TryParseTokenName("save.abc123.token", out name, out id) && name == "save" && id == "abc123",
                "save.abc123.token parses as ('save', 'abc123')",
                String.Format("save.abc123.token parsed as ('{0}', '{1}')", name, id));

            passed &= Expect(report,
                RequestPoller.TryParseTokenName("nav-reload.0123456789ab.token", out name, out id) && name == "nav-reload",
                "a hyphenated name with a twelve-hex id parses",
                "nav-reload.0123456789ab.token did not parse");

            string[] refused = { "save.token", "Save.abc.token", "save.ABC.token", "save.abc.claimed", "save..token", "save.abc.token.tmp", "9save.abc.token" };

            foreach (string bad in refused)
            {
                passed &= Expect(report,
                    !RequestPoller.TryParseTokenName(bad, out name, out id),
                    "'" + bad + "' is not a token name",
                    "'" + bad + "' was accepted as a token name");
            }

            return passed;
        }

        private static bool FixtureSweep(List<string> report, string root)
        {
            string requests = Path.Combine(root, "requests");
            string data = Path.Combine(root, "data");
            string health = Path.Combine(root, "health.json");

            Directory.CreateDirectory(requests);
            Directory.CreateDirectory(data);

            string[] stale =
            {
                Path.Combine(requests, "save.abc.token"),
                Path.Combine(requests, "commit.def.claimed"),
                Path.Combine(requests, "health.ghi.ack.json"),
                Path.Combine(requests, "nav-reload.jkl.token.xyz.tmp"),
                Path.Combine(data, "navigation.json.mno.staged")
            };

            foreach (string file in stale)
            {
                Put(file, "stale");
            }

            Put(health, "{}");
            Put(Path.Combine(data, "navigation.json"), "{\"keep\": 1}\n");
            Put(Path.Combine(requests, "notes.txt"), "keep");

            IList<string> removed = RequestPoller.Sweep(requests, new[] { data }, health);

            bool allGone = true;

            foreach (string file in stale)
            {
                allGone &= !File.Exists(file);
            }

            bool passed = Expect(report,
                allGone && !File.Exists(health) && removed.Count == 6,
                "the sweep removes a token, a claimed token, an ack, a staging file, a staged commit and health.json (6 files)",
                String.Format("the sweep left something, or counted {0}", removed.Count));

            passed &= Expect(report,
                File.Exists(Path.Combine(data, "navigation.json")) && File.Exists(Path.Combine(requests, "notes.txt")),
                "the sweep leaves the data file and an unrelated file alone",
                "the sweep removed a file it should not have");

            return passed;
        }

        private static bool FixtureAckText(List<string> report)
        {
            var result = new CommitResult
            {
                Written = true, Reloaded = true, Ok = true, Hash = "0011223344556677", BackupHash = "8899aabbccddeeff",
                ChangedAt = "2026-09-22T00:00:00.0000000Z", ChangedBy = "editor commit ab12"
            };

            string text = RequestPoller.BuildAck(
                "commit", "ab12", "file=navigation base=8899aabbccddeeff wrote=0011223344556677", true, "completed",
                "navigation.json committed", new string[0], new[] { "one warning" }, "\"commit\": " + result.ToJson());

            JObject ack;

            try
            {
                ack = JObject.Parse(text);
            }
            catch (Exception ex)
            {
                return Expect(report, false, "", "the ack is not valid JSON: " + ex.Message + "\n" + text);
            }

            bool passed = Expect(report,
                (string)ack["id"] == "ab12" && (string)ack["outcome"] == "completed" && (string)ack["request"] == "commit"
                    && (bool)ack["ok"] && (string)ack["bootId"] == PersistenceGeneration.BootId && ack["generation"] != null,
                "the ack carries id, outcome, request, ok, generation and bootId",
                "the ack is missing a field: " + text);

            JObject commit = ack["commit"] as JObject;

            passed &= Expect(report,
                commit != null && (bool)commit["written"] && (string)commit["hash"] == "0011223344556677"
                    && (string)commit["backupHash"] == "8899aabbccddeeff" && (string)commit["changedBy"] == "editor commit ab12",
                "the nested commit object carries written, hash, backupHash and changedBy",
                "the commit object is wrong: " + text);

            passed &= Expect(report,
                ack["warnings"] is JArray && ((JArray)ack["warnings"]).Count == 1 && (string)ack["warnings"][0] == "one warning",
                "warnings survive beside the commit object",
                "warnings were lost: " + text);

            return passed;
        }
    }
}
