using System;
using System.Collections.Generic;

using Server.Commands;
using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>Staff and player commands for the jail.</summary>
    public static class JailCommands
    {
        private static readonly TimeSpan RecordCooldown = TimeSpan.FromSeconds(30.0);

        private static readonly Dictionary<Mobile, DateTime> _recordCooldowns = new Dictionary<Mobile, DateTime>();

        public static void Initialize()
        {
            CommandSystem.Register("Jail", AccessLevel.GameMaster, Jail_OnCommand);
            CommandSystem.Register("Unjail", AccessLevel.GameMaster, Unjail_OnCommand);
            CommandSystem.Register("JailInfo", AccessLevel.Counselor, JailInfo_OnCommand);
            CommandSystem.Register("JailRecord", AccessLevel.Player, JailRecord_OnCommand);
        }

        [Usage("Jail <player> [reason]")]
        [Description("Jails a player. The sentence escalates with each offence.")]
        private static void Jail_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length < 1)
            {
                from.SendMessage("Usage: [Jail <player> [reason]");
                return;
            }

            string name = e.GetString(0);
            Mobile player;
            string error;

            if (!TryFindPlayer(name, out player, out error))
            {
                from.SendMessage(0x35, error);
                return;
            }

            // Online-only. Jailing offline is possible (offline characters keep settable
            // LogoutLocation/LogoutMap) but is deliberately not built - the sentence should start
            // when it is actually served.
            if (player.NetState == null)
            {
                from.SendMessage(0x35, String.Format("{0} is not online. They must be online to be jailed.", player.Name));
                return;
            }

            string refusal = JailSystem.Instance.GetRefusalReason(player);

            if (refusal != null)
            {
                from.SendMessage(0x35, refusal);
                return;
            }

            // The whole remainder of the line, not just the second token.
            string reason = ExtractReason(e, name);

            JailSystem.Instance.JailPlayer(from, player, reason);

            if (!JailSystem.Instance.IsPlayerJailed(player))
            {
                from.SendMessage(0x35, String.Format("Failed to jail {0}.", player.Name));
                return;
            }

            from.SendMessage(
                String.Format(
                    "{0} is being jailed for {1}. Reason: {2}",
                    player.Name,
                    JailSystem.FormatDuration(JailSystem.Instance.GetJailEndTime(player) - DateTime.UtcNow),
                    reason));
        }

        [Usage("Unjail <player>")]
        [Description("Releases a player from jail immediately.")]
        private static void Unjail_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length < 1)
            {
                from.SendMessage("Usage: [Unjail <player>");
                return;
            }

            Mobile player;
            string error;

            if (!TryFindPlayer(e.GetString(0), out player, out error))
            {
                from.SendMessage(0x35, error);
                return;
            }

            if (!JailSystem.Instance.Unjail(from, player))
            {
                from.SendMessage(0x35, String.Format("{0} is not currently jailed.", player.Name));
                return;
            }

            from.SendMessage(String.Format("{0} has been released from jail.", player.Name));
        }

        [Usage("JailInfo <player>")]
        [Description("Shows a player's jail record.")]
        private static void JailInfo_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length < 1)
            {
                from.SendMessage("Usage: [JailInfo <player>");
                return;
            }

            Mobile player;
            string error;

            if (!TryFindPlayer(e.GetString(0), out player, out error))
            {
                from.SendMessage(0x35, error);
                return;
            }

            Report(from, player, JailSystem.Instance.GetRecord(player), true);
        }

        [Usage("JailRecord")]
        [Description("Shows your own jail record.")]
        private static void JailRecord_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            DateTime next;

            if (_recordCooldowns.TryGetValue(from, out next) && next > DateTime.UtcNow)
            {
                from.SendMessage(
                    0x35,
                    String.Format("You must wait {0:F0} seconds.", (next - DateTime.UtcNow).TotalSeconds));
                return;
            }

            _recordCooldowns[from] = DateTime.UtcNow + RecordCooldown;

            Report(from, from, JailSystem.Instance.GetRecord(from), false);
        }

        private static void Report(Mobile to, Mobile subject, JailRecord record, bool staffView)
        {
            string who = staffView ? subject.Name : "You";

            if (record == null || record.JailCount == 0)
            {
                to.SendMessage(
                    staffView
                        ? String.Format("{0} has no jail record.", who)
                        : "You have no jail record.");
                return;
            }

            to.SendMessage(0x35, String.Format("--- Jail record: {0} ---", subject.Name));
            to.SendMessage(String.Format("Offences: {0}", record.JailCount));
            to.SendMessage(String.Format("Last jailed: {0:yyyy-MM-dd HH:mm} UTC", record.LastJailedUtc));
            to.SendMessage(String.Format("Last reason: {0}", record.LastReason));

            if (record.IsCurrentlyJailed)
            {
                to.SendMessage(0x25, String.Format("Currently jailed - {0} remaining.", JailSystem.FormatDuration(record.Remaining)));
            }
            else
            {
                to.SendMessage(0x40, "Not currently jailed.");
            }

            if (staffView)
            {
                to.SendMessage(
                    String.Format(
                        "Jailed by: {0}. Origin facet: {1}.",
                        record.JailedBy != null ? record.JailedBy.Name : "the server",
                        record.OriginMap != null ? record.OriginMap.Name : "unknown"));

                to.SendMessage(
                    String.Format("Next sentence would be {0}.", JailSystem.FormatDuration(JailSystem.CalculateJailTime(record.JailCount + 1))));
            }
        }

        /// <summary>
        /// Everything after the player name, so a multi-word reason survives. The stock
        /// CommandEventArgs accessors only give one token at a time.
        /// </summary>
        private static string ExtractReason(CommandEventArgs e, string name)
        {
            string args = e.ArgString;

            if (String.IsNullOrWhiteSpace(args))
            {
                return "No reason given.";
            }

            args = args.Trim();

            if (args.Length > name.Length && args.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                string reason = args.Substring(name.Length).Trim();

                if (reason.Length > 0)
                {
                    return reason;
                }
            }

            return "No reason given.";
        }

        /// <summary>
        /// Finds a player character by name. There is no such helper in the tree.
        ///
        /// Character names are NOT unique across accounts, so multiple matches are reported and
        /// refused rather than silently taking the first - jailing the wrong person because two
        /// characters share a name would be hard to notice and worse to undo.
        /// </summary>
        public static bool TryFindPlayer(string name, out Mobile player, out string error)
        {
            player = null;
            error = null;

            if (String.IsNullOrWhiteSpace(name))
            {
                error = "No player name given.";
                return false;
            }

            var matches = new List<Mobile>();

            // Offline characters are still in World.Mobiles (internalized), so this finds them
            // too - which is what lets [JailInfo and [Unjail work on someone who is logged out.
            foreach (Mobile m in World.Mobiles.Values)
            {
                // NOT a bot. This walks World.Mobiles to find offline characters, and a bot
                // is both a PlayerMobile and in that dictionary - so `[JailInfo Elowen` would
                // resolve to whichever bot happened to have rolled that name, in preference to
                // nobody at all. NamePool draws from the same kind of name list a player would
                // pick from, so the collision is likely rather than theoretical.
                if (m is PlayerMobile && !(m is IBotActor)
                    && !m.Deleted && Insensitive.Equals(m.Name, name))
                {
                    matches.Add(m);
                }
            }

            if (matches.Count == 0)
            {
                error = String.Format("No player named '{0}' was found.", name);
                return false;
            }

            if (matches.Count > 1)
            {
                error = String.Format(
                    "{0} characters are named '{1}'. Be more specific - this command cannot tell them apart.",
                    matches.Count,
                    name);
                return false;
            }

            player = matches[0];
            return true;
        }
    }
}
