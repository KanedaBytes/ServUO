using System;

using Server.Gumps;
using Server.Network;

namespace Server.Custom
{
    /// <summary>
    /// The undismissable 30-second countdown shown while a player stands in a restricted zone.
    ///
    /// ServUO has no StaticGump, no cached layout and no string placeholders, so unlike the
    /// ModernUO original this is rebuilt and re-sent on every tick. That makes closing the
    /// previous instance mandatory - see DisplayTo.
    /// </summary>
    public class RestrictedZoneCountdownGump : Gump
    {
        private const int GumpWidth = 220;
        private const int GumpHeight = 60;

        private readonly string _zoneName;
        private readonly int _secondsLeft;

        private RestrictedZoneCountdownGump(string zoneName, int secondsLeft)
            : base((640 - GumpWidth) / 2, 20)
        {
            _zoneName = zoneName;
            _secondsLeft = secondsLeft;

            // Closable=false blocks right-click. Disposable=false is what blocks Escape - both
            // are needed, and both are only client-side hints (see OnResponse).
            Closable = false;
            Disposable = false;
            Dragable = false;
            Resizable = false;

            AddPage(0);

            // The background is load-bearing, not decoration: a gump with no visual elements is
            // an invisible, undismissable window the player cannot get rid of.
            AddBackground(0, 0, GumpWidth, GumpHeight, 9200);
            AddImageTiled(10, 10, GumpWidth - 20, GumpHeight - 20, 2624);
            AddAlphaRegion(10, 10, GumpWidth - 20, GumpHeight - 20);

            AddHtml(
                15,
                12,
                GumpWidth - 30,
                20,
                String.Format("<BASEFONT COLOR=#FF6600><CENTER>RESTRICTED: {0}</CENTER>", zoneName),
                false,
                false);

            AddHtml(
                15,
                32,
                GumpWidth - 30,
                20,
                String.Format(
                    "<BASEFONT COLOR=#FFFFFF><CENTER>{0}</CENTER>",
                    secondsLeft == 1 ? "Leave now - 1 second" : String.Format("Leave now - {0} seconds", secondsLeft)),
                false,
                false);
        }

        /// <summary>
        /// Sends the gump, replacing any copy already open.
        ///
        /// The CloseGump call emulates ModernUO's Singleton and is NOT optional. NetState.AddGump
        /// disconnects the client once 512 gumps accumulate, and this is re-sent once a second,
        /// so without it a player standing in a zone is kicked in under nine minutes.
        /// </summary>
        public static void DisplayTo(Mobile from, string zoneName, int secondsLeft)
        {
            // Validate before constructing. A gump that short-circuits after construction is
            // sent empty, and an empty gump carrying the no-close flags is stuck on the client
            // permanently.
            if (from == null || from.Deleted || from.NetState == null || zoneName == null)
            {
                return;
            }

            from.CloseGump(typeof(RestrictedZoneCountdownGump));
            from.SendGump(new RestrictedZoneCountdownGump(zoneName, secondsLeft));
        }

        public override void OnResponse(NetState sender, RelayInfo info)
        {
            // The no-close flags are client-side hints that a modified client can ignore. If one
            // sends a close anyway, put the gump straight back. By the time this runs the gump is
            // already out of server-side tracking, so a plain send is correct here.
            if (info.ButtonID != 0 || sender == null)
            {
                return;
            }

            Mobile from = sender.Mobile;

            if (RestrictedZoneSystem.HasCountdown(from))
            {
                DisplayTo(from, _zoneName, _secondsLeft);
            }
        }
    }
}
