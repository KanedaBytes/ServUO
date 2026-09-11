// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotPaceSnapshot.cs - [BotPace's answer, on disk, for a caller that has no Mobile.
//
// WHY THIS EXISTS
// ---------------
// [BotPace reports through CommandReport.Send, which needs somebody to send a gump to. That was
// fine while the only caller was a person targeting a bot, and it is exactly why the measurement
// could not be taken headlessly: the population scale test has to ask "does the pace hold at 400
// bots" over a run with no client attached to it at all.
//
// So the sampler's lines go to Data/Live/bot-pace.json as well, every time, whether or not anybody
// is holding a gump - the same arrangement BotStepCensus and NavWalkFailures already have, and for
// the same reason. Its own file rather than a field on health.json, for walk-failures' reason: the
// value is in the LINES, and a pace reported as one number is no answer.

using System;
using System.Collections.Generic;
using System.Text;

namespace Server.Custom
{
    public static class BotPaceSnapshot
    {
        public const string Path = "Data/Live/bot-pace.json";

        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>The last sample's lines, for the health check and for a caller that just asked.</summary>
        public static IList<string> Last { get; private set; }

        public static DateTime? LastUtc { get; private set; }

        public static string LastBot { get; private set; }

        public static void Write(PlayerBot bot, int seconds, IList<string> lines)
        {
            Last = lines;
            LastUtc = DateTime.UtcNow;
            LastBot = bot == null ? null : bot.Name;

            var builder = new StringBuilder(1024);

            builder.Append("{\n  \"utc\": \"").Append(LastUtc.Value.ToString("o")).Append("\",\n");
            builder.Append("  \"bot\": ").Append(Json.Quote(LastBot)).Append(",\n");
            builder.Append("  \"serial\": ").Append(bot == null ? 0 : bot.Serial.Value).Append(",\n");
            builder.Append("  \"seconds\": ").Append(seconds).Append(",\n");
            builder.Append("  \"lines\": [");

            for (int i = 0; lines != null && i < lines.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append("\n    ").Append(Json.Quote(lines[i]));
            }

            builder.Append("\n  ]\n}\n");

            string error;

            if (!AtomicFile.Write(Path, builder.ToString(), out error))
            {
                Log.Error("Could not write " + Path + ": " + error);
            }
        }

        /// <summary>One line for an ack or a console report.</summary>
        public static string Describe()
        {
            if (Last == null || Last.Count == 0)
            {
                return "no pace sample taken yet this boot";
            }

            return String.Format(
                "{0}: {1}", LastBot ?? "a bot", String.Join(" | ", ToArray(Last)));
        }

        private static string[] ToArray(IList<string> lines)
        {
            var copy = new string[lines.Count];

            lines.CopyTo(copy, 0);

            return copy;
        }
    }
}
