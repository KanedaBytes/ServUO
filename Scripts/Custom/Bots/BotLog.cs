// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotLog.cs — the last fifty things each bot did.
//
// Written because a real fault was invisible. A Gatherer that cannot get inside
// its site gives up after seventy-five seconds and walks away
// (GathererBehavior.TickWalkIn), and the ONLY trace of that was a Log.Debug
// line - dropped entirely unless the shard is running with -debug. Nothing
// counted it, no health check saw it, and from outside it looked exactly like a
// bot that had simply decided to go somewhere else. Diagnosing it meant
// attaching a debugger to a behaviour that only misbehaves two minutes' walk
// out of town.
//
// So: a ring buffer per bot, a console line for the things worth saying out
// loud, and a file the editor can read.
//
// THREE DELIBERATE CHOICES
//
//  1. NO LOCKING, because there is nothing to lock against. Every writer is a
//     behaviour tick, a walker callback or a staff command, and all three run on
//     the game thread - see CLAUDE.md section 4. A background thread must reach
//     this through LoopQueue.Post like anything else that touches world state.
//
//  2. A DELETED BOT KEEPS ITS LOG. Deletion is usually the last and most
//     interesting event in it: "gave up, went Traveler, got rolled away, was
//     reaped". Dropping the ring in OnDelete would throw away the tail of every
//     failure worth reading. The ring is keyed by Serial rather than by the
//     mobile precisely so it can outlive it without pinning it, and the size of
//     the table is bounded by recency (MaxBots) rather than by liveness.
//
//  3. THE RING IS NOT THE CONSOLE. Everything goes in the ring; only behaviour
//     changes go to the console at Info, and everything goes to the console for
//     a bot under [BotTrace. A shard running twenty bots produces a few hundred
//     events a minute, which is a useful file and an unreadable console.

using System;
using System.Collections.Generic;
using System.Text;

namespace Server.Custom
{
    /// <summary>
    /// What kind of thing happened. A small closed set on purpose: the editor filters on these,
    /// and a free-text category would be a different string in every call site within a month.
    /// </summary>
    public enum BotLogKind
    {
        /// <summary>The brain changed, or refused to.</summary>
        Behavior,

        /// <summary>A route was asked for, and granted or refused.</summary>
        Route,

        /// <summary>A walker finished, or was abandoned.</summary>
        Arrive,

        /// <summary>A stuck-recovery rung fired.</summary>
        Rung,

        /// <summary>Clocked in or out of a work site.</summary>
        Clock,

        /// <summary>A swing, a craft, a delivery.</summary>
        Work,

        /// <summary>The bot said something.</summary>
        Speech
    }

    /// <summary>One entry. A struct so a fifty-deep ring is one allocation, not fifty.</summary>
    public struct BotLogEntry
    {
        public DateTime Utc;
        public BotLogKind Kind;
        public string Text;
    }

    public static class BotLog
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>Events kept per bot. Upstream has no equivalent; fifty is about one shift.</summary>
        public const int RingSize = 50;

        /// <summary>
        /// How many bots' logs are kept at once.
        ///
        /// The bound is recency, not liveness (see the header): a bot that was deleted a minute
        /// ago is exactly the one being asked about. Sixty-four is comfortably more than any
        /// fleet this shard runs and caps the file at a few hundred kilobytes.
        /// </summary>
        public const int MaxBots = 64;

        private sealed class Ring
        {
            public string Name;
            public Serial Serial;
            public bool Traced;
            public long LastNoted;

            private readonly BotLogEntry[] _entries = new BotLogEntry[RingSize];
            private int _next;
            private int _count;

            public int Count
            {
                get { return _count; }
            }

            public void Add(BotLogEntry entry)
            {
                _entries[_next] = entry;
                _next = (_next + 1) % RingSize;

                if (_count < RingSize)
                {
                    _count++;
                }
            }

            /// <summary>Oldest first, which is the order a person reads a log in.</summary>
            public List<BotLogEntry> Snapshot()
            {
                var list = new List<BotLogEntry>(_count);

                int start = _count < RingSize ? 0 : _next;

                for (int i = 0; i < _count; i++)
                {
                    list.Add(_entries[(start + i) % RingSize]);
                }

                return list;
            }
        }

        private static readonly Dictionary<Serial, Ring> _rings = new Dictionary<Serial, Ring>();

        /// <summary>Bumped by every Note. The snapshot writer skips a write when nothing moved.</summary>
        private static bool _dirty;

        public static bool Dirty
        {
            get { return _dirty; }
        }

        public static int TrackedBots
        {
            get { return _rings.Count; }
        }

        // ---- writing ----

        public static void Note(PlayerBot bot, BotLogKind kind, string format, params object[] args)
        {
            if (bot == null)
            {
                return;
            }

            string text = Safe(format, args);

            Ring ring = RingFor(bot);

            ring.Add(new BotLogEntry { Utc = DateTime.UtcNow, Kind = kind, Text = text });
            ring.LastNoted = Core.TickCount;

            _dirty = true;

            // A traced bot says everything out loud. Untraced, only SetBehavior speaks - see
            // PlayerBot.SetBehavior, which logs its own line at Info rather than going through
            // here, because its format is fixed and public.
            if (ring.Traced)
            {
                Log.Info("[trace] {0} {1}: {2}", ring.Name, Tag(kind), text);
            }
        }

        // ---- tracing ----

        public static bool IsTraced(PlayerBot bot)
        {
            if (bot == null)
            {
                return false;
            }

            Ring ring;

            return _rings.TryGetValue(bot.Serial, out ring) && ring.Traced;
        }

        public static void Trace(PlayerBot bot, bool on)
        {
            if (bot == null)
            {
                return;
            }

            RingFor(bot).Traced = on;
        }

        /// <summary>Turns tracing off everywhere. Returns how many bots were being traced.</summary>
        public static int TraceNone()
        {
            int cleared = 0;

            foreach (Ring ring in _rings.Values)
            {
                if (ring.Traced)
                {
                    ring.Traced = false;
                    cleared++;
                }
            }

            return cleared;
        }

        public static List<string> TracedNames()
        {
            var names = new List<string>();

            foreach (Ring ring in _rings.Values)
            {
                if (ring.Traced)
                {
                    names.Add(ring.Name);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);

            return names;
        }

        // ---- reading ----

        /// <summary>This bot's events, oldest first. Empty if nothing has been logged for it.</summary>
        public static List<BotLogEntry> Entries(PlayerBot bot)
        {
            Ring ring;

            if (bot == null || !_rings.TryGetValue(bot.Serial, out ring))
            {
                return new List<BotLogEntry>();
            }

            return ring.Snapshot();
        }

        public static string Tag(BotLogKind kind)
        {
            switch (kind)
            {
                case BotLogKind.Behavior: return "behavior";
                case BotLogKind.Route: return "route";
                case BotLogKind.Arrive: return "arrive";
                case BotLogKind.Rung: return "rung";
                case BotLogKind.Clock: return "clock";
                case BotLogKind.Work: return "work";
                case BotLogKind.Speech: return "speech";
            }

            return "other";
        }

        /// <summary>
        /// The whole table as JSON, for Data/Live/botlog.json.
        ///
        /// Hand-built like every other snapshot in Core/Bridge - see Json.cs for why - and
        /// newest bot first, because the one that just did something is the one being looked for.
        /// </summary>
        public static string BuildJson(long sequence)
        {
            var builder = new StringBuilder(8192);

            builder.Append("{\n");
            builder.Append("  \"sequence\": ").Append(sequence).Append(",\n");
            builder.Append("  \"utc\": \"").Append(DateTime.UtcNow.ToString("o")).Append("\",\n");
            builder.Append("  \"bots\": [\n");

            List<Ring> rings = ByRecency();

            for (int i = 0; i < rings.Count; i++)
            {
                Ring ring = rings[i];

                builder.Append("    {");
                builder.Append("\"serial\":").Append(ring.Serial.Value);
                builder.Append(",\"name\":").Append(Json.Quote(ring.Name));
                builder.Append(",\"traced\":").Append(ring.Traced ? "true" : "false");
                builder.Append(",\"events\":[");

                List<BotLogEntry> entries = ring.Snapshot();

                for (int j = 0; j < entries.Count; j++)
                {
                    if (j > 0)
                    {
                        builder.Append(",");
                    }

                    BotLogEntry entry = entries[j];

                    builder.Append("{\"utc\":\"").Append(entry.Utc.ToString("o")).Append("\"");
                    builder.Append(",\"kind\":").Append(Json.Quote(Tag(entry.Kind)));
                    builder.Append(",\"text\":").Append(Json.Quote(entry.Text));
                    builder.Append("}");
                }

                builder.Append("]}");
                builder.Append(i < rings.Count - 1 ? ",\n" : "\n");
            }

            builder.Append("  ]\n}\n");

            return builder.ToString();
        }

        /// <summary>
        /// The log has been written out. Called by the writer only after a SUCCESSFUL write.
        ///
        /// Separate from BuildJson on purpose: clearing the flag as a side effect of building the
        /// string would drop everything logged since the last write whenever the write itself
        /// failed, which is precisely the moment the log matters.
        /// </summary>
        public static void MarkClean()
        {
            _dirty = false;
        }

        // ---- internals ----

        private static Ring RingFor(PlayerBot bot)
        {
            Ring ring;

            if (_rings.TryGetValue(bot.Serial, out ring))
            {
                // Names are rolled once and never change, but a ring can outlive its bot and a
                // serial could in principle be handed out again. Keeping it current costs a
                // reference assignment and stops a stale name being reported as a live one.
                ring.Name = bot.Name ?? "(unnamed)";
                return ring;
            }

            ring = new Ring
            {
                Name = bot.Name ?? "(unnamed)",
                Serial = bot.Serial,
                LastNoted = Core.TickCount
            };

            _rings[bot.Serial] = ring;

            Evict();

            return ring;
        }

        /// <summary>
        /// Drop the least recently noted ring once the table is over its cap.
        ///
        /// A loop rather than a single drop because MaxBots could be lowered between builds, and
        /// a one-at-a-time eviction would then take one new bot per excess ring to converge.
        /// </summary>
        private static void Evict()
        {
            while (_rings.Count > MaxBots)
            {
                Serial oldest = Serial.MinusOne;
                long oldestAt = 0;
                bool found = false;

                foreach (KeyValuePair<Serial, Ring> pair in _rings)
                {
                    // Wraparound-safe comparison by subtraction, per CLAUDE.md section 15.
                    if (!found || oldestAt - pair.Value.LastNoted > 0)
                    {
                        oldest = pair.Key;
                        oldestAt = pair.Value.LastNoted;
                        found = true;
                    }
                }

                if (!found)
                {
                    return;
                }

                _rings.Remove(oldest);
            }
        }

        private static List<Ring> ByRecency()
        {
            var rings = new List<Ring>(_rings.Values);

            rings.Sort((a, b) => Math.Sign(b.LastNoted - a.LastNoted));

            return rings;
        }

        /// <summary>
        /// Format without ever throwing.
        ///
        /// The same contract CustomLogger.Safe keeps, and for the same reason: a log line is not
        /// allowed to be the thing that kills a behaviour tick. A stray brace in a bot name or a
        /// destination id would otherwise be a FormatException inside BotTickManager's per-bot
        /// try/catch - survivable, but it would eat the event it was trying to record.
        /// </summary>
        private static string Safe(string format, object[] args)
        {
            if (format == null)
            {
                return String.Empty;
            }

            if (args == null || args.Length == 0)
            {
                return format;
            }

            try
            {
                return String.Format(format, args);
            }
            catch
            {
                return format;
            }
        }
    }
}
