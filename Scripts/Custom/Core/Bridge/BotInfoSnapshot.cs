// BotInfoSnapshot.cs — one bot's [BotInfo report, on disk, for the editor's bot card.
//
// WHY THIS EXISTS
// ---------------
// [BotInfo is the most useful thing anybody can ask about a bot - class, tier, home, behaviour,
// status, the leg it is on, stats against the caps, notoriety, every non-zero skill in an aligned
// column, personality and the phase clock - and until now the only way to read it was to stand
// next to the bot in a client and target it. The editor draws the same bot as a dot, on a map, in
// a browser, and could tell you four of those things.
//
// The card's existing detail comes from entities.json, which is a per-frame snapshot of sixty
// bots: putting forty lines of skill table into it for every bot on every poll would be a hundred
// kilobytes twice a second to answer a question about one of them. So this is pulled, not pushed -
// a request token names one serial and the answer lands here.
//
// WHY IT IS A FILE AND NOT THE ACK
// --------------------------------
// Same reason nav-audit.json is a file. The ack carries prose and caps its arrays; this is a
// table, and a table that has been truncated to fit an ack is a table you cannot trust. A file
// also means the card can re-read the last answer without asking the shard again.
//
// AND WHY THE EMPTY ANSWER IS STILL WRITTEN
// -----------------------------------------
// A bot that has gone between the poll and the request is the normal case, not an error - they are
// ephemeral by design. Writing an empty report for that serial is what stops the card showing the
// PREVIOUS bot's forty lines under this bot's name, which is the one failure mode a cached panel
// has that a command does not.

using System;
using System.Collections.Generic;

namespace Server.Custom
{
    public static class BotInfoSnapshot
    {
        public const string Path = "Data/Live/botinfo.json";

        /// <summary>
        /// The report for one serial, or an empty one when that bot is gone.
        ///
        /// The serial is echoed back deliberately: the card asks about the bot it has selected and
        /// has to be able to tell "this is your answer" from "this is the answer to the request
        /// before yours", which a list of lines alone cannot say.
        /// </summary>
        public static string BuildJson(int serial, PlayerBot bot)
        {
            IList<string> lines = BotCommands.Describe(bot);

            var builder = new System.Text.StringBuilder(4096);

            builder.Append("{\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"serial\": ").Append(serial).Append(",\n");
            builder.Append("  \"name\": ").Append(Json.Quote(bot == null ? null : bot.Name)).Append(",\n");
            builder.Append("  \"lines\": [\n");

            for (int i = 0; i < lines.Count; i++)
            {
                builder.Append("    ").Append(Json.Quote(lines[i]));

                if (i < lines.Count - 1)
                {
                    builder.Append(",");
                }

                builder.Append("\n");
            }

            builder.Append("  ]\n}\n");

            return builder.ToString();
        }
    }
}
