using System;
using System.IO;
using System.Text;

namespace Server.Custom
{
    /// <summary>
    /// The one thing a long-running job's status file cannot say for itself: that the process
    /// writing it went away.
    ///
    /// nav-adopt.json and walk-audit.json are written with "working" / "running" while a job runs
    /// and "done" when it finishes. A shard that shut down, crashed, or was killed mid-run leaves
    /// the last progress write on disk, and the editor - which reads the status out of the file
    /// because the ack could only ever say "started" - polls a job that will never finish. That is
    /// the outcome-contract failure in Scripts/Custom/Core/LoopQueue.cs: a caller told "working"
    /// forever has been told neither done nor failed nor unknown.
    ///
    /// So at boot each owner asks this to look at its file, and if the status is one of the live
    /// ones, rewrite it as "unknown" with the reason. Only the status changes; every other field
    /// the editor reads is kept, because the rows a half-run produced are still evidence.
    ///
    /// The head is read first - both files put "status" on their second line and walk-audit.json
    /// runs to six megabytes - so a boot with nothing to do costs one small read, not a parse.
    /// </summary>
    public static class LiveStatus
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bridge");

        private const int HeadBytes = 512;

        /// <summary>
        /// Rewrites the file's status to "unknown" when it is one of <paramref name="staleValues"/>.
        /// Returns true when it did. Never throws.
        /// </summary>
        public static bool MarkStale(string relativePath, string[] staleValues, string error)
        {
            string fullPath = Path.Combine(Core.BaseDirectory, relativePath);

            try
            {
                if (!File.Exists(fullPath))
                {
                    return false;
                }

                string head;

                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var buffer = new byte[HeadBytes];
                    int read = fs.Read(buffer, 0, buffer.Length);
                    head = Encoding.UTF8.GetString(buffer, 0, read);
                }

                string stale = null;

                for (int i = 0; i < staleValues.Length; i++)
                {
                    if (head.IndexOf("\"status\": \"" + staleValues[i] + "\"", StringComparison.Ordinal) >= 0)
                    {
                        stale = staleValues[i];
                        break;
                    }
                }

                if (stale == null)
                {
                    return false;
                }

                string text;

                using (var reader = new StreamReader(fullPath, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }

                string from = "\"status\": \"" + stale + "\"";
                int at = text.IndexOf(from, StringComparison.Ordinal);

                if (at < 0)
                {
                    return false;
                }

                string to = "\"status\": \"unknown\",\n  \"error\": " + Json.Quote(error);
                text = text.Substring(0, at) + to + text.Substring(at + from.Length);

                string writeError;

                if (!AtomicFile.Write(relativePath, text, out writeError))
                {
                    Log.Warn("Could not mark {0} as unknown: {1}", relativePath, writeError);
                    return false;
                }

                Log.Warn("{0} said \"{1}\" from a previous boot; marked unknown: {2}", relativePath, stale, error);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not inspect {0}: {1}", relativePath, ex.Message);
                return false;
            }
        }
    }
}
