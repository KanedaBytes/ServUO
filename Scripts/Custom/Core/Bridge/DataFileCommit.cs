using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Server.Custom
{
    /// <summary>The reload that follows a commit: the same call the plain reload token makes.</summary>
    public delegate bool DataFileReload(
        string relative, out string message, out IList<string> errors, out IList<string> warnings);

    /// <summary>One file the editor may write, resolved from its key.</summary>
    public sealed class DataFileTarget
    {
        /// <summary>The key as the bridge spells it: navigation, dailyLife, restrictedZones, spawn:&lt;facet&gt;/GG_x.xml.</summary>
        public string Key;

        /// <summary>Relative to the shard root, forward slashes - the ledger's spelling.</summary>
        public string RelativePath;

        public string FullPath;

        /// <summary>The name a message calls it: navigation.json, GG_OldMarta.xml.</summary>
        public string Display;

        /// <summary>The argument the reload takes (the spawn file's facet/name), or null.</summary>
        public string ReloadArgument;

        /// <summary>Null means "do not reload" - the fixtures, and reload=no.</summary>
        public DataFileReload Reload;
    }

    /// <summary>What an editor commit asks: replace the file it was based on, then reload.</summary>
    public sealed class CommitSpec
    {
        public string Id;

        /// <summary>The hash the caller's copy was read at, or "none" for a file that must not exist yet.</summary>
        public string Base;

        /// <summary>The hash of the staged bytes, so a half-staged file is never committed.</summary>
        public string Wrote;

        public bool Reload = true;
    }

    /// <summary>What a restore asks: put the .bak back over a file that is still where the caller left it.</summary>
    public sealed class RestoreSpec
    {
        public string Id;

        /// <summary>The hash the live file must be at.</summary>
        public string Base;

        /// <summary>The hash the .bak must be at - the backup the caller believes it is restoring.</summary>
        public string Backup;

        public bool Reload = true;
    }

    /// <summary>The ack's nested `commit` object, plus the message and details the ack carries at top level.</summary>
    public sealed class CommitResult
    {
        /// <summary>The live file was replaced.</summary>
        public bool Written;

        /// <summary>A version check said no. Nothing was touched.</summary>
        public bool Refused;

        /// <summary>The live file's hash now.</summary>
        public string Hash;

        /// <summary>The .bak's hash now: what the live file was before this replace, or "none".</summary>
        public string BackupHash;

        /// <summary>On a refusal: the hash the file was actually at.</summary>
        public string Actual;

        /// <summary>On a refusal: the hash the caller expected.</summary>
        public string Expected;

        public string ChangedAt;

        public string ChangedBy;

        public bool Reloaded;

        public string Message;

        public IList<string> Errors = new string[0];

        public IList<string> Warnings = new string[0];

        /// <summary>Completed only when the file was written AND reloaded (or no reload was asked for).</summary>
        public bool Ok;

        /// <summary>The JSON object the ack embeds under "commit".</summary>
        public string ToJson()
        {
            var b = new StringBuilder(256);

            b.Append("{");
            b.Append("\"written\": ").Append(Written ? "true" : "false");
            b.Append(", \"refused\": ").Append(Refused ? "true" : "false");
            b.Append(", \"reloaded\": ").Append(Reloaded ? "true" : "false");
            b.Append(", \"hash\": ").Append(Json.Quote(Hash));
            b.Append(", \"backupHash\": ").Append(Json.Quote(BackupHash));
            b.Append(", \"actual\": ").Append(Json.Quote(Actual));
            b.Append(", \"expected\": ").Append(Json.Quote(Expected));
            b.Append(", \"changedAt\": ").Append(Json.Quote(ChangedAt));
            b.Append(", \"changedBy\": ").Append(Json.Quote(ChangedBy));
            b.Append("}");

            return b.ToString();
        }
    }

    /// <summary>
    /// The shard's side of an editor save: compare the live file with the version the save was
    /// based on, and only then replace it.
    ///
    /// WHY THE SHARD AND NOT THE BRIDGE. Until 22 September 2026 the bridge wrote the data files
    /// itself, checked an OPTIONAL base hash first, and then asked the shard to reload; restore
    /// checked nothing (REVIEW.md, "Restore has a lost-update hole"). The check and the rename were
    /// two steps in one process while a second writer - [NavRecord through NavigationSystem.Save,
    /// on the game thread - could land between them, and the bridge could never say who had. This
    /// class runs on the game thread, where every other writer of these files runs, so the compare
    /// and the replace are one uninterrupted step; and it reads DataFileLedger, so a refusal names
    /// the writer and the time rather than only the fact.
    ///
    /// THE VERSION IS THE HASH: sha256 of the file's bytes, first sixteen hex digits, computed the
    /// same way as tools/editor/bridge.js hashOf (which hashes the UTF-8 string - identical bytes
    /// for the BOM-less UTF-8 both writers produce). A file that does not exist has the version
    /// "none", which is what a save creating a new spawn file is based on.
    ///
    /// THE STAGED FILE sits beside its target - &lt;live&gt;.&lt;id&gt;.staged - because that is what
    /// AtomicFile does and why: a rename across volumes is a copy. Its name is derived here from
    /// the key and the id; nothing in the token is a path.
    ///
    /// EVERY REPLACE KEEPS THE PREVIOUS LIVE FILE AS .bak, restore included. GGSpawnCommands
    /// .TryReloadFile unloads the spawners named in the .bak before loading the file, so the .bak
    /// has to be what the world is running - and after a restore that is the file just undone, not
    /// the older backup. (A restore that kept the old .bak orphaned any spawner the undone save had
    /// created. That hole existed before this class and closes with it.)
    /// </summary>
    public static class DataFileCommit
    {
        /// <summary>The version of a file that is not there.</summary>
        public const string NoFile = "none";

        /// <summary>sha256 of zero bytes, first sixteen: what the bridge's hashOf('') gives.</summary>
        public const string EmptyHash = "e3b0c44298fc1c14";

        private static readonly Regex SpawnKey = new Regex(
            @"^spawn:([a-z][a-z0-9_-]{0,31})/(GG_[A-Za-z0-9_-]{1,48}\.xml)$", RegexOptions.Compiled);

        private static readonly Regex IdPattern = new Regex(@"^[a-z0-9]{1,32}$", RegexOptions.Compiled);

        private static readonly Regex HashPattern = new Regex(@"^[0-9a-f]{16}$", RegexOptions.Compiled);

        // ---- keys ------------------------------------------------------------------------------

        /// <summary>
        /// Resolves a key the way tools/editor/whitelist.js does: the three fixed files by name,
        /// a spawn file by a strict pattern, and anything else refused. Nothing here takes a path.
        /// </summary>
        public static bool TryResolveKey(string key, out DataFileTarget target, out string error)
        {
            target = null;
            error = null;

            switch (key)
            {
                case "navigation":
                    target = Fixed(key, NavigationSystem.ConfigPath, ReloadNavigation);
                    return true;

                case "dailyLife":
                    target = Fixed(key, DailyLifeSystem.ConfigPath, ReloadDailyLife);
                    return true;

                case "restrictedZones":
                    target = Fixed(key, RestrictedZoneSystem.ConfigPath, ReloadZones);
                    return true;
            }

            Match match = key == null ? null : SpawnKey.Match(key);

            if (match == null || !match.Success)
            {
                error = "no such data file '" + key + "'; the keys are navigation, dailyLife, restrictedZones "
                    + "and spawn:<facet>/GG_<Name>.xml";
                return false;
            }

            string relative = match.Groups[1].Value + "/" + match.Groups[2].Value;
            string relativePath = GGSpawnCommands.SpawnRoot + "/" + relative;
            string full = Path.GetFullPath(Path.Combine(Core.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string root = Path.GetFullPath(Path.Combine(Core.BaseDirectory, GGSpawnCommands.SpawnRoot))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // The pattern cannot produce a path that escapes, but the check that the bridge's
            // whitelist and GGSpawnCommands.TryResolve both make is made here too.
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                error = "'" + relative + "' is outside " + GGSpawnCommands.SpawnRoot;
                return false;
            }

            target = new DataFileTarget
            {
                Key = key,
                RelativePath = relativePath,
                FullPath = full,
                Display = match.Groups[2].Value,
                ReloadArgument = relative,
                Reload = ReloadSpawnFile
            };

            return true;
        }

        private static DataFileTarget Fixed(string key, string relativePath, DataFileReload reload)
        {
            return new DataFileTarget
            {
                Key = key,
                RelativePath = relativePath,
                FullPath = Path.Combine(Core.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                Display = Path.GetFileName(relativePath),
                ReloadArgument = null,
                Reload = reload
            };
        }

        /// <summary>&lt;live&gt;.&lt;id&gt;.staged, beside the file it will replace.</summary>
        public static string StagedPath(DataFileTarget target, string id)
        {
            return target.FullPath + "." + id + ".staged";
        }

        public static bool IsValidId(string id)
        {
            return id != null && IdPattern.IsMatch(id);
        }

        // ---- the token body --------------------------------------------------------------------

        /// <summary>
        /// "file=navigation base=1a2b... wrote=3c4d... reload=yes" into a dictionary. A word without
        /// '=' is ignored, so the parsers' habit of skipping '#' words costs nothing here.
        /// </summary>
        public static Dictionary<string, string> ParseFields(string body)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string word in (body ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = word.IndexOf('=');

                if (eq > 0)
                {
                    fields[word.Substring(0, eq)] = word.Substring(eq + 1);
                }
            }

            return fields;
        }

        /// <summary>A hash field: sixteen lowercase hex digits, or "none".</summary>
        public static bool IsVersion(string value)
        {
            return value == NoFile || (value != null && HashPattern.IsMatch(value));
        }

        // ---- hashing ---------------------------------------------------------------------------

        /// <summary>The file's version: sha256 of its bytes, first sixteen hex, or "none" when absent.</summary>
        public static string Hash16(string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                return NoFile;
            }

            return Hash16(File.ReadAllBytes(fullPath));
        }

        public static string Hash16(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                var hex = new StringBuilder(16);

                for (int i = 0; i < 8; i++)
                {
                    hex.Append(digest[i].ToString("x2"));
                }

                return hex.ToString();
            }
        }

        /// <summary>The version a writer's text will have on disk: its UTF-8 bytes, no BOM (AtomicFile writes none).</summary>
        public static string Hash16Text(string text)
        {
            return Hash16(new UTF8Encoding(false).GetBytes(text ?? ""));
        }

        // ---- commit ----------------------------------------------------------------------------

        /// <summary>
        /// Replaces the target with its staged file if - and only if - the target is still at the
        /// version the save was based on. Returns the result's Ok. Never throws.
        /// </summary>
        public static bool TryCommit(DataFileTarget target, CommitSpec spec, out CommitResult result)
        {
            result = new CommitResult { Expected = spec.Base };

            string staged = StagedPath(target, spec.Id);

            try
            {
                string actual = Hash16(target.FullPath);

                result.Actual = actual;
                result.ChangedAt = DataFileLedger.ChangedAt(target.FullPath, actual);
                result.ChangedBy = DataFileLedger.ChangedBy(target.FullPath, actual);

                if (!String.Equals(actual, spec.Base, StringComparison.OrdinalIgnoreCase))
                {
                    result.Refused = true;
                    result.Message = StaleMessage(target, actual, spec.Base, "save");
                    Discard(staged);
                    return false;
                }

                if (!File.Exists(staged))
                {
                    result.Refused = true;
                    result.Message = String.Format(
                        "nothing is staged for {0} under commit {1}; the bridge did not write {2}, or a boot swept it",
                        target.Display, spec.Id, Path.GetFileName(staged));
                    return false;
                }

                string stagedHash = Hash16(staged);

                if (!String.Equals(stagedHash, spec.Wrote, StringComparison.OrdinalIgnoreCase))
                {
                    result.Refused = true;
                    result.Message = String.Format(
                        "the staged file for {0} hashes to {1}, not the {2} the bridge said it wrote; nothing was committed",
                        target.Display, stagedHash, spec.Wrote);
                    Discard(staged);
                    return false;
                }

                string replaceError;

                if (!AtomicFile.Replace(staged, target.FullPath, ".bak", out replaceError))
                {
                    result.Message = "could not replace " + target.Display + ": " + replaceError;
                    Discard(staged);
                    return false;
                }

                result.Written = true;
                result.Hash = stagedHash;
                result.BackupHash = actual;
                result.ChangedAt = DateTime.UtcNow.ToString("o");
                result.ChangedBy = "editor commit " + spec.Id;

                DataFileLedger.Note(target.FullPath, stagedHash, result.ChangedBy);

                return RunReload(target, spec.Reload, result,
                    String.Format("{0} committed at {1} (was {2})", target.Display, stagedHash, actual));
            }
            catch (Exception ex)
            {
                result.Message = "commit of " + target.Display + " threw " + ex.GetType().Name + ": " + ex.Message;
                Discard(staged);
                return false;
            }
        }

        // ---- restore ---------------------------------------------------------------------------

        /// <summary>
        /// Puts the .bak back over the live file if the live file is still at the version the
        /// caller saw AND the .bak is the backup the caller meant. The replaced live file becomes
        /// the new .bak. Returns the result's Ok. Never throws.
        /// </summary>
        public static bool TryRestore(DataFileTarget target, RestoreSpec spec, out CommitResult result)
        {
            result = new CommitResult { Expected = spec.Base };

            string backup = target.FullPath + ".bak";
            string staged = StagedPath(target, spec.Id);

            try
            {
                string actual = Hash16(target.FullPath);

                result.Actual = actual;
                result.ChangedAt = DataFileLedger.ChangedAt(target.FullPath, actual);
                result.ChangedBy = DataFileLedger.ChangedBy(target.FullPath, actual);

                if (!String.Equals(actual, spec.Base, StringComparison.OrdinalIgnoreCase))
                {
                    result.Refused = true;
                    result.Message = StaleMessage(target, actual, spec.Base, "restore");
                    return false;
                }

                if (!File.Exists(backup))
                {
                    result.Refused = true;
                    result.Expected = spec.Backup;
                    result.Actual = NoFile;
                    result.Message = "there is no " + target.Display + ".bak to restore";
                    return false;
                }

                string backupHash = Hash16(backup);

                if (!String.Equals(backupHash, spec.Backup, StringComparison.OrdinalIgnoreCase))
                {
                    result.Refused = true;
                    result.Expected = spec.Backup;
                    result.Actual = backupHash;
                    result.ChangedAt = DataFileLedger.ChangedAt(backup, backupHash);
                    result.ChangedBy = DataFileLedger.ChangedBy(backup, backupHash);
                    result.Message = String.Format(
                        "{0}.bak is at {1}, not the {2} this restore expected: the backup on disk is not the one "
                        + "you would be restoring ({3}). Reload and look at what is there.",
                        target.Display, backupHash, spec.Backup, DataFileLedger.Describe(backup, backupHash));
                    return false;
                }

                File.Copy(backup, staged, true);

                string replaceError;

                if (!AtomicFile.Replace(staged, target.FullPath, ".bak", out replaceError))
                {
                    result.Message = "could not restore " + target.Display + ": " + replaceError;
                    Discard(staged);
                    return false;
                }

                result.Written = true;
                result.Hash = backupHash;
                result.BackupHash = actual;
                result.ChangedAt = DateTime.UtcNow.ToString("o");
                result.ChangedBy = "editor restore " + spec.Id;

                DataFileLedger.Note(target.FullPath, backupHash, result.ChangedBy);
                DataFileLedger.Note(backup, actual, result.ChangedBy);

                return RunReload(target, spec.Reload, result,
                    String.Format("{0} restored to {1}; the file it replaced ({2}) is now the .bak",
                        target.Display, backupHash, actual));
            }
            catch (Exception ex)
            {
                result.Message = "restore of " + target.Display + " threw " + ex.GetType().Name + ": " + ex.Message;
                Discard(staged);
                return false;
            }
        }

        // ---- helpers ---------------------------------------------------------------------------

        /// <summary>
        /// The refusal, in one sentence a person can act on: which file, the hash it is at, the hash
        /// the caller had, and - from the ledger - who wrote it and when.
        /// </summary>
        private static string StaleMessage(DataFileTarget target, string actual, string expected, string verb)
        {
            if (actual == NoFile)
            {
                return String.Format(
                    "{0} does not exist, but this {1} was based on version {2}; it was deleted or moved since you loaded it",
                    target.Display, verb, expected);
            }

            if (expected == NoFile)
            {
                return String.Format(
                    "{0} already exists (at {1}), but this {2} expected to create it; {3}. Reload and edit the file that is there",
                    target.Display, actual, verb, DataFileLedger.Describe(target.FullPath, actual));
            }

            return String.Format(
                "{0} is at {1}, not the {2} this {3} was based on; {4}. Reload before writing it again",
                target.Display, actual, expected, verb, DataFileLedger.Describe(target.FullPath, actual));
        }

        private static bool RunReload(DataFileTarget target, bool wanted, CommitResult result, string written)
        {
            if (!wanted || target.Reload == null)
            {
                result.Ok = true;
                result.Message = written + "; not reloaded";
                return true;
            }

            string message;
            IList<string> errors;
            IList<string> warnings;

            bool reloaded;

            try
            {
                reloaded = target.Reload(target.ReloadArgument, out message, out errors, out warnings);
            }
            catch (Exception ex)
            {
                reloaded = false;
                message = "the reload threw " + ex.GetType().Name + ": " + ex.Message;
                errors = new string[0];
                warnings = new string[0];
            }

            result.Reloaded = reloaded;
            result.Ok = reloaded;
            result.Message = message;
            result.Errors = errors ?? new string[0];
            result.Warnings = warnings ?? new string[0];

            return reloaded;
        }

        private static void Discard(string staged)
        {
            try
            {
                if (File.Exists(staged))
                {
                    File.Delete(staged);
                }
            }
            catch
            {
                // A leftover staged file is swept at the next boot; the refusal above is the real answer.
            }
        }

        // ---- the four reloads, shared with the plain reload tokens ------------------------------

        public static bool ReloadNavigation(
            string relative, out string message, out IList<string> errors, out IList<string> warnings)
        {
            string error;

            warnings = new string[0];

            if (!NavigationSystem.TryReload(out error, out errors))
            {
                message = error;
                return false;
            }

            warnings = NavigationSystem.DataWarnings;

            message = String.Format("{0} waypoint(s), {1} destination(s), {2} warning(s)",
                NavigationSystem.Graph.NodeCount,
                NavigationSystem.Destinations.Count,
                NavigationSystem.DataWarnings.Count);
            return true;
        }

        public static bool ReloadDailyLife(
            string relative, out string message, out IList<string> errors, out IList<string> warnings)
        {
            string error;

            warnings = new string[0];

            if (!DailyLifeCommands.TryReload(out error, out errors))
            {
                message = error;
                return false;
            }

            warnings = DailyLifeSystem.ConfigWarnings;

            message = String.Format("{0} config warning(s)", DailyLifeSystem.ConfigWarnings.Count);
            return true;
        }

        public static bool ReloadZones(
            string relative, out string message, out IList<string> errors, out IList<string> warnings)
        {
            string error;

            warnings = new string[0];

            if (!RestrictedZoneSystem.TryReload(out error, out errors))
            {
                message = error;
                return false;
            }

            message = String.Format("{0} restricted zone(s)", RestrictedZoneSystem.Zones.Count);
            return true;
        }

        public static bool ReloadSpawnFile(
            string relative, out string message, out IList<string> errors, out IList<string> warnings)
        {
            string error;
            string summary;

            errors = new string[0];
            warnings = new string[0];

            if (!GGSpawnCommands.TryReloadFile(relative, out summary, out error))
            {
                message = error;
                return false;
            }

            message = summary;
            return true;
        }
    }
}
