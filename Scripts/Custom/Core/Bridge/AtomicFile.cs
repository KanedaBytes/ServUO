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
