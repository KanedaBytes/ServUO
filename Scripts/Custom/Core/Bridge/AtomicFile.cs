using System;
using System.IO;
using System.Text;

namespace Server.Custom
{
    /// <summary>
    /// Writes a file so a reader never sees it half-written.
    ///
    /// The bridge polls these files on its own schedule, with no locking between the two
    /// processes. A plain File.WriteAllText truncates first, so a reader landing in that window
    /// gets an empty or partial file and a JSON parse error - which, on a snapshot written every
    /// two seconds, would happen regularly rather than rarely.
    ///
    /// Note this cannot use File.Move(source, dest, overwrite): that overload is .NET Core 3.0+
    /// and this shard targets net48. File.Replace is the net48 equivalent and is atomic on NTFS.
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>
        /// The binary, durable variant, for persistence rather than snapshots.
        ///
        /// Three differences from the text overload, each on purpose. The bytes go through a
        /// FileStream with Flush(true) - File.WriteAllBytes never fsyncs, and on ext4 a rename over
        /// an existing file is only crash-safe once the new file's data has reached the disk. The
        /// live file, when there is one, is kept as <paramref name="backupSuffix"/> beside it by
        /// File.Replace's third argument, so the previous generation survives the write in place
        /// (REVIEW.md F6: keep the last known-good, not just avoid destroying it) and the rare
        /// ERROR_UNABLE_TO_MOVE_REPLACEMENT can never leave no destination at all. And a snapshot
        /// written every two seconds could not afford either.
        /// </summary>
        public static bool Write(string relativePath, byte[] bytes, string backupSuffix, out string error)
        {
            error = null;

            string fullPath = Path.Combine(Core.BaseDirectory, relativePath);
            string temporary = fullPath + ".tmp";

            try
            {
                string directory = Path.GetDirectoryName(fullPath);

                if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (var fs = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }

                if (File.Exists(fullPath))
                {
                    File.Replace(temporary, fullPath, backupSuffix == null ? null : fullPath + backupSuffix);
                }
                else
                {
                    File.Move(temporary, fullPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;

                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch
                {
                    // A leftover .tmp is untidy, not harmful; the real error is the one above.
                }

                return false;
            }
        }

        /// <summary>
        /// Puts an already-written file over another in one step, keeping the previous file beside
        /// it as <paramref name="backupSuffix"/> (".bak") when there was one.
        ///
        /// The commit half of an editor save (DataFileCommit): the bridge stages the new bytes
        /// beside the target, and this is the rename. Full paths, because the caller has already
        /// resolved them - the fixtures run this on a scratch tree, not under Core.BaseDirectory.
        /// The backup argument is passed whenever the destination exists, for the reason the
        /// binary Write gives: with a backup named, the rare ERROR_UNABLE_TO_MOVE_REPLACEMENT can
        /// never leave no destination at all. Same volume is required and is a property of the
        /// layout (everything under the shard root); a move across volumes would be a copy.
        /// </summary>
        public static bool Replace(string sourceFullPath, string destinationFullPath, string backupSuffix, out string error)
        {
            error = null;

            try
            {
                if (File.Exists(destinationFullPath))
                {
                    File.Replace(
                        sourceFullPath, destinationFullPath,
                        backupSuffix == null ? null : destinationFullPath + backupSuffix);
                }
                else
                {
                    string directory = Path.GetDirectoryName(destinationFullPath);

                    if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.Move(sourceFullPath, destinationFullPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool Write(string relativePath, string contents, out string error)
        {
            error = null;

            string fullPath = Path.Combine(Core.BaseDirectory, relativePath);
            string temporary = fullPath + ".tmp";

            try
            {
                string directory = Path.GetDirectoryName(fullPath);

                if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // No BOM: everything downstream is a JSON parser, and a BOM is a parse error in
                // more of them than you would like.
                File.WriteAllText(temporary, contents, new UTF8Encoding(false));

                if (File.Exists(fullPath))
                {
                    File.Replace(temporary, fullPath, null);
                }
                else
                {
                    File.Move(temporary, fullPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;

                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch
                {
                    // A leftover .tmp is untidy, not harmful; the real error is the one above.
                }

                return false;
            }
        }
    }
}
