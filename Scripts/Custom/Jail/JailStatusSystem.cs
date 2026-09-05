using System;
using System.Collections.Generic;

using Server.Network;

namespace Server.Custom
{
    /// <summary>
    /// Keeps the jail status gump in front of prisoners.
    ///
    /// One shard-wide sweep rather than a timer per prisoner: jailing raises no event anything
    /// can subscribe to (a staff member typing [Jail is just a command), so a sweep is the only
    /// general discovery mechanism. It also costs the same whether one player is jailed or fifty.
    /// </summary>
    public static class JailStatusSystem
    {
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1.0);

        /// <summary>Who was shown the gump on the previous sweep, so it can be closed on release.</summary>
        private static readonly HashSet<Mobile> _showing = new HashSet<Mobile>();

        public static void Initialize()
        {
            Timer.DelayCall(RefreshInterval, RefreshInterval, 0, Sweep);
        }

        public static bool IsJailed(Mobile player)
        {
            return player != null && !player.Deleted && JailService.Provider.IsPlayerJailed(player);
        }

        /// <summary>Remaining sentence, floored at zero so a player with no record reads as zero.</summary>
        public static TimeSpan GetRemaining(Mobile player)
        {
            TimeSpan remaining = JailService.Provider.GetJailEndTime(player) - DateTime.UtcNow;

            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        /// <summary>
        /// Shows the gump immediately rather than waiting up to a minute for the next sweep.
        /// Called on login and at the end of the jail sequence.
        /// </summary>
        public static void ShowNow(Mobile player)
        {
            if (!IsJailed(player))
            {
                return;
            }

            _showing.Add(player);
            JailStatusGump.DisplayTo(player);
        }

        private static void Sweep()
        {
            var stillJailed = new HashSet<Mobile>();

            // Online clients only - there is no point rendering a gump for someone who is not
            // connected, and this is far cheaper than walking World.Mobiles.
            foreach (NetState ns in NetState.Instances)
            {
                Mobile player = ns.Mobile;

                if (player == null || !IsJailed(player))
                {
                    continue;
                }

                stillJailed.Add(player);

                // Replaces the previous copy, and puts the gump back if the player dismissed it.
                JailStatusGump.DisplayTo(player);
            }

            // Anyone shown last time but not this time has been released, logged out, or deleted.
            foreach (Mobile player in _showing)
            {
                if (!stillJailed.Contains(player) && player != null && !player.Deleted)
                {
                    player.CloseGump(typeof(JailStatusGump));
                }
            }

            _showing.Clear();
            _showing.UnionWith(stillJailed);
        }
    }
}
