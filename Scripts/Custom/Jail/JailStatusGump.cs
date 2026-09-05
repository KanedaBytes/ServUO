using System;

using Server.Gumps;

namespace Server.Custom
{
    /// <summary>
    /// The "JAILED - Xh Ym remaining" window shown to a prisoner.
    ///
    /// Deliberately closable, unlike the restricted-zone countdown: a twelve-hour sentence should
    /// not come with an undismissable window bolted to the screen. The minute sweep puts it back,
    /// which is enough to keep the remaining time visible without being oppressive.
    /// </summary>
    public class JailStatusGump : Gump
    {
        private const int GumpWidth = 220;
        private const int GumpHeight = 60;

        private JailStatusGump(string remaining)
            : base((640 - GumpWidth) / 2, 20)
        {
            Closable = true;
            Disposable = true;
            Dragable = false;
            Resizable = false;

            AddPage(0);

            AddBackground(0, 0, GumpWidth, GumpHeight, 9200);
            AddImageTiled(10, 10, GumpWidth - 20, GumpHeight - 20, 2624);
            AddAlphaRegion(10, 10, GumpWidth - 20, GumpHeight - 20);

            AddHtml(15, 12, GumpWidth - 30, 20, "<BASEFONT COLOR=#FF6600><CENTER>JAILED</CENTER>", false, false);
            AddHtml(
                15,
                32,
                GumpWidth - 30,
                20,
                String.Format("<BASEFONT COLOR=#FFFFFF><CENTER>{0}</CENTER>", remaining),
                false,
                false);
        }

        /// <summary>
        /// Sends the gump, replacing any copy already open.
        ///
        /// The CloseGump call is not optional: NetState.AddGump disconnects the client once 512
        /// gumps accumulate, and BaseGump.SendGump would not help here because its reuse check is
        /// reference equality and we build a fresh instance every sweep.
        /// </summary>
        public static void DisplayTo(Mobile player)
        {
            // Validate before constructing - a gump built and then abandoned is still sent.
            if (player == null || player.Deleted || player.NetState == null)
            {
                return;
            }

            if (!JailService.Provider.IsPlayerJailed(player))
            {
                return;
            }

            player.CloseGump(typeof(JailStatusGump));
            player.SendGump(new JailStatusGump(FormatRemaining(JailStatusSystem.GetRemaining(player))));
        }

        /// <summary>
        /// Deliberately coarse. The gump only refreshes once a minute, so showing seconds would
        /// be a lie for up to fifty-nine of them.
        /// </summary>
        public static string FormatRemaining(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero)
            {
                return "Release is imminent";
            }

            int hours = (int)remaining.TotalHours;
            int minutes = remaining.Minutes;

            if (hours > 0)
            {
                return String.Format("{0}h {1}m remaining", hours, minutes);
            }

            return minutes > 0
                ? String.Format("{0}m remaining", minutes)
                : "Less than a minute remaining";
        }
    }
}
