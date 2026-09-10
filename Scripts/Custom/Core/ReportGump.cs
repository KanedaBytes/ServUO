using System;
using System.Collections.Generic;
using System.Text;

using Server.Gumps;
using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// A staff command's output, when there is too much of it to say out loud.
    ///
    /// The diagnostic commands all had the same fault: they answered in overhead messages. A
    /// [NavAudit reporting thirty findings, or [BotInfo listing twelve skills, scrolled its own
    /// first line off the top of the journal before the last one arrived - and the first line is
    /// usually the summary. Reading the answer meant opening the journal and scrolling up, or
    /// running the command again and reading faster.
    ///
    /// One HTML area with `scrollbar: true` is the whole mechanism. The client draws and scrolls
    /// it; nothing here paginates, because a gump that pages a report is a gump somebody has to
    /// operate to read a list.
    ///
    /// A ONE-LINE ANSWER STAYS A MESSAGE. `[NavReload` saying "121 waypoints, 0 warnings" in a
    /// window you have to close is worse than the problem this fixes: the point of a gump is that
    /// the output does not fit, and most of the time it does.
    /// </summary>
    public class ReportGump : BaseGump
    {
        private const int Width = 620;
        private const int Height = 460;

        private readonly string _title;
        private readonly string _body;

        /// <summary>
        /// The same report as one selectable line. See the copy row in AddGumpLayout.
        ///
        /// NOT escaped, unlike the body: a text entry carries a string rather than markup, so an
        /// angle bracket in a mobile's name is a character here and a half-open tag there. Passing
        /// the escaped version would put a literal "&amp;lt;" into whatever somebody pasted.
        /// </summary>
        private readonly string _copy;

        public ReportGump(PlayerMobile user, string title, IList<string> lines)
            : base(user, 100, 100)
        {
            _title = title ?? "Report";
            _body = ToHtml(lines);
            _copy = ToCopyLine(lines);
        }

        public override void AddGumpLayout()
        {
            AddPage(0);

            AddBackground(0, 0, Width, Height, 5054);
            AddImageTiled(10, 10, Width - 20, Height - 20, 2624);
            AddAlphaRegion(10, 10, Width - 20, Height - 20);

            AddHtml(14, 14, Width - 28, 20,
                String.Format("<basefont color=#FFD479><b>{0}</b></basefont>", Escape(_title)),
                false, false);

            // The scrollbar is the feature. Everything else on this gump is a frame around it.
            AddHtml(14, 38, Width - 28, Height - 118, _body, true, true);

            // THE COPY ROW - an experiment, kept only if it works in the client.
            //
            // An AddHtml area cannot be selected: you can read a report and you cannot get it out
            // of the client, which is why every report worth quoting has been retyped or
            // screenshotted. A gump TEXT ENTRY is the only widget in the protocol that takes focus
            // and a caret, so it is the only place Ctrl+A and Ctrl+C have anything to act on.
            //
            // IT IS ADDED BESIDE THE HTML RATHER THAN INSTEAD OF IT, and that is a trade rather
            // than timidity. There is no scrollable container for text entries - AddHtml's
            // scrollbar is the only one the protocol has - so replacing the body with a stack of
            // entries would page a forty-line [NavAudit into something you operate rather than
            // read, which is the exact fault this gump was built to remove. One entry holding the
            // whole report costs one row and loses nothing.
            //
            // The lines are joined with ' | ' because a gump text entry is a single-line widget:
            // a newline in the string is not rendered, so the alternative to a separator is a
            // report whose line breaks silently become nothing at all.
            AddHtml(14, Height - 76, 120, 20,
                "<basefont color=#9A8C98>Copy:</basefont>", false, false);

            AddImageTiled(58, Height - 78, Width - 158, 22, 2624);
            AddTextEntry(60, Height - 76, Width - 162, 20, 0, 0, _copy);

            AddButton(Width - 100, Height - 40, 4017, 4019, 0, GumpButtonType.Reply, 0);
            AddHtml(Width - 68, Height - 38, 60, 20,
                "<basefont color=#FFFFFF>Close</basefont>", false, false);
        }

        /// <summary>
        /// The lines as one HTML block.
        ///
        /// Escaped first, then joined - a finding that quotes a mobile's name or a tag can carry
        /// an angle bracket, and an unescaped one silently eats the rest of the report.
        /// </summary>
        private static string ToHtml(IList<string> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                return "<basefont color=#9A8C98>Nothing to report.</basefont>";
            }

            var builder = new StringBuilder(lines.Count * 64);

            builder.Append("<basefont color=#FFFFFF>");

            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("<br>");
                }

                builder.Append(Escape(lines[i]));
            }

            builder.Append("</basefont>");

            return builder.ToString();
        }

        /// <summary>
        /// The report as one line, for the copy row.
        ///
        /// Trimmed per line and joined with ' | ' - a text entry is single-line, so the indentation
        /// [BotInfo uses to align its skill column would arrive as a run of spaces separating
        /// nothing. The separator is what carries the structure once the line breaks cannot.
        /// </summary>
        private static string ToCopyLine(IList<string> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                return "";
            }

            var builder = new StringBuilder(lines.Count * 48);

            for (int i = 0; i < lines.Count; i++)
            {
                string line = (lines[i] ?? "").Trim();

                if (line.Length == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(" | ");
                }

                builder.Append(line);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Neutralises the one character that can eat a report, and nothing else.
        ///
        /// THE CLIENT'S GUMP HTML DOES NOT DECODE ENTITIES. It parses tags, so an unescaped '&lt;'
        /// opens one and swallows whatever follows - but `&amp;gt;` is not turned back into '&gt;', it is
        /// drawn as the four characters `&amp;gt;`. The first version escaped '&gt;' and '&amp;' as well, and
        /// every NavAudit finding came out reading
        ///
        ///     BLOCKED 'brit-plaza-9' -&amp;gt; 'brit-bake-1'
        ///
        /// because every one of those arrows is a '&gt;'. Escaping '&amp;' has the same fault one level
        /// down: it would render the literal text `&amp;amp;`, which is the bug it was meant to avoid.
        ///
        /// So: '&lt;' only. '&gt;' is not special to a parser that is looking for a tag OPENER, and an
        /// ampersand that is never decoded cannot be misdecoded.
        ///
        /// reportgump.test.js reads this method and asserts the set of replacements is exactly
        /// this one - the failure was invisible from outside the client, and a test that only
        /// checked "is it escaped" would have passed on the broken version.
        /// </summary>
        private static string Escape(string text)
        {
            if (String.IsNullOrEmpty(text))
            {
                return "";
            }

            return text.Replace("<", "&lt;");
        }
    }

    /// <summary>
    /// Where a command's output goes: a message when it is short, a scrollable gump when it is
    /// not. The decision is here rather than at each call site so the four commands cannot drift
    /// apart about what "too long" means.
    /// </summary>
    public static class CommandReport
    {
        /// <summary>
        /// Above this many lines the answer opens in a gump. Two, not one: a summary plus a
        /// single finding is still something you can read as it goes past, and the commands that
        /// answer in exactly two lines are the common ones.
        /// </summary>
        public const int GumpAbove = 2;

        public static void Send(Mobile to, string title, IList<string> lines)
        {
            if (to == null || lines == null || lines.Count == 0)
            {
                return;
            }

            var player = to as PlayerMobile;

            // Short answers, and anything that cannot be sent a gump, stay in the journal. The
            // console runs these commands too, and it has no gump to open.
            if (lines.Count <= GumpAbove || player == null || player.NetState == null)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    to.SendMessage(lines[i]);
                }

                return;
            }

            // Said out loud as well as shown, so the journal still records that the command ran
            // and what its headline was - a gump is closed and gone, and the journal is what
            // somebody scrolls back through afterwards.
            to.SendMessage(0x3B2, String.Format("{0}: {1} line(s).", title, lines.Count));

            BaseGump.SendGump(new ReportGump(player, title, lines));
        }
    }
}
