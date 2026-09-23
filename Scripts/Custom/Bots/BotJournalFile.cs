// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotJournalFile.cs - an append-only JSON-lines file under Data/Live, buffered and rotated.
//
// BotGoodsLedger's Flush/Rotate, lifted out so the gold ledger's losses and the trade journal
// write the same way rather than as two more copies of it: lines accumulate in memory, one
// append writes them all, and past MaxBytes the file moves to Path + ".1" so a boot purge's
// couple of hundred lines a restart cannot grow it without bound. Upstream's shape for the file
// itself is BotEventJournal (Data/Live/event-journal.jsonl), one JSON object per line.
//
// Game thread only, like everything that feeds it. Writing a file during a save is not a change
// to the world (BotGoodsLedger.Configure says why), so a WorldSave handler may flush it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Server.Custom
{
    public sealed class BotJournalFile
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly List<string> _pending = new List<string>();

        public BotJournalFile(string path, long maxBytes)
        {
            Path = path;
            MaxBytes = maxBytes;
        }

        /// <summary>Relative to Core.BaseDirectory, like every other Data/ path.</summary>
        public string Path { get; private set; }

        public long MaxBytes { get; private set; }

        /// <summary>The last line appended, exactly as it will be written. Fixtures assert on it.</summary>
        public string Last { get; private set; }

        /// <summary>Lines appended since boot.</summary>
        public int Appended { get; private set; }

        public void Append(string line)
        {
            if (String.IsNullOrEmpty(line))
            {
                return;
            }

            Last = line;
            Appended++;

            _pending.Add(line);
        }

        /// <summary>One append for whatever has accumulated.</summary>
        public void Flush()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var text = new StringBuilder(_pending.Count * 320);

            for (int i = 0; i < _pending.Count; i++)
            {
                text.Append(_pending[i]).Append('\n');
            }

            _pending.Clear();

            try
            {
                string full = System.IO.Path.Combine(Core.BaseDirectory, Path);
                string directory = System.IO.Path.GetDirectoryName(full);

                if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Rotate(full);

                File.AppendAllText(full, text.ToString(), Utf8);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "The journal {0} could not be written.", Path);
            }
        }

        private void Rotate(string full)
        {
            var info = new FileInfo(full);

            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            string previous = full + ".1";

            if (File.Exists(previous))
            {
                File.Delete(previous);
            }

            File.Move(full, previous);
        }
    }
}
