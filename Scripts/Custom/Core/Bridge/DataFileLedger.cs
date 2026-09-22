using System;
using System.Collections.Generic;
using System.IO;

namespace Server.Custom
{
    /// <summary>
    /// What the shard itself last wrote to each of the editor's data files, so a refused commit
    /// can say WHO changed the file and WHEN rather than only that it changed.
    ///
    /// Two processes write navigation.json: the bridge (an editor save, through the `commit`
    /// token) and the shard ([NavMark, [NavRecord and the rest of NavigationCommands.cs through
    /// NavigationSystem.Save, and NavResampleZ.Apply). The same is true of GG_BotPop.xml
    /// (BotPopulation.Write) and, in principle, restricted-zones.json. When an editor save arrives
    /// based on a version the file is no longer at, the only party that can name the other
    /// writer is the shard, because the shard is that writer - and a message that says "changed
    /// by the shard (NavigationSystem.Save) at 02:14:03Z" tells the person at the editor to reload,
    /// where "changed on disk" told them to guess.
    ///
    /// In memory only, by design: after a boot nothing is known, and the honest description of a
    /// file whose current hash matches no note is "outside the shard (a hand edit, git, or before
    /// this boot)" with the file's own last-write time. Hashes are the same sha256/16 the bridge
    /// computes, so a note made from the bytes a writer just produced compares directly with the
    /// hash of the file on disk.
    /// </summary>
    public static class DataFileLedger
    {
        private sealed class Entry
        {
            public string Hash;
            public DateTime Utc;
            public string By;
        }

        private static readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The label a writer that names nobody gets.</summary>
        public const string DefaultActor = "the shard";

        public const string OutsideActor = "outside the shard (a hand edit, git, or before this boot)";

        /// <summary>Records that <paramref name="by"/> just wrote the file, leaving it at <paramref name="hash"/>.</summary>
        public static void Note(string path, string hash, string by)
        {
            if (String.IsNullOrEmpty(path) || String.IsNullOrEmpty(hash))
            {
                return;
            }

            _entries[Normalize(path)] = new Entry
            {
                Hash = hash,
                Utc = DateTime.UtcNow,
                By = String.IsNullOrEmpty(by) ? DefaultActor : by
            };
        }

        /// <summary>
        /// Who wrote the file that is now at <paramref name="currentHash"/>: the noted writer when
        /// the hash matches the last note, otherwise the outside-the-shard label. Never null.
        /// </summary>
        public static string ChangedBy(string path, string currentHash)
        {
            Entry entry;

            if (currentHash != null && _entries.TryGetValue(Normalize(path), out entry)
                && String.Equals(entry.Hash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                return entry.By;
            }

            return OutsideActor;
        }

        /// <summary>
        /// When the file was last written, as an ISO-8601 UTC string: the note's time when the note
        /// matches, else the file's last-write time, else null when there is no file.
        /// </summary>
        public static string ChangedAt(string fullPath, string currentHash)
        {
            Entry entry;

            if (currentHash != null && _entries.TryGetValue(Normalize(fullPath), out entry)
                && String.Equals(entry.Hash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Utc.ToString("o");
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    return File.GetLastWriteTimeUtc(fullPath).ToString("o");
                }
            }
            catch
            {
                // A file that cannot be stat'ed has no time to report; the hash is still in the message.
            }

            return null;
        }

        /// <summary>
        /// The clause a refusal ends with: "last written 2026-09-22T02:14:03.1234567Z by the shard
        /// (NavigationSystem.Save)". Everything it says is true of the file as it is NOW.
        /// </summary>
        public static string Describe(string fullPath, string currentHash)
        {
            string at = ChangedAt(fullPath, currentHash);
            string by = ChangedBy(fullPath, currentHash);

            if (at == null)
            {
                return "there is no file to have been written";
            }

            return "last written " + at + " by " + by;
        }

        /// <summary>Test seam: forgets every note.</summary>
        public static void Clear()
        {
            _entries.Clear();
        }

        /// <summary>
        /// One spelling per file. Writers pass the relative path they were given
        /// ("Data/Custom/navigation.json", or with backslashes from Path.Combine); the commit passes
        /// a full path. Both land on the same key: relative to Core.BaseDirectory, forward slashes.
        /// </summary>
        private static string Normalize(string path)
        {
            string text = path.Replace('\\', '/');
            string root = Core.BaseDirectory.Replace('\\', '/').TrimEnd('/') + "/";

            if (text.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(root.Length);
            }

            return text.TrimStart('/');
        }
    }
}
