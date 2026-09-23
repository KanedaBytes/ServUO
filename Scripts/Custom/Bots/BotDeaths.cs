// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotDeaths.cs - how many bots have died since boot, to what, and where.
//
// There was no count at all. The graveyard was found by watching it in game: bots died at the
// Britain graveyard, their mounts went wild, and the pack killed every bot arriving next - and not
// one health line moved, because nothing counted a death. Reported on Bots.Population.
//
// Until 7g gives bots combat this should sit near zero: a bot that dies is a bot that walked into
// something it cannot fight, and BotHostileSites is what keeps them out of the known places.

using System;
using System.Collections.Generic;
using System.Text;

namespace Server.Custom
{
    public static class BotDeaths
    {
        /// <summary>Bot deaths since boot.</summary>
        public static int Total { get; private set; }

        // "Spectre at britain-graveyard" -> count. Bounded by killer types times destinations.
        private static readonly Dictionary<string, int> _byCause = new Dictionary<string, int>();

        /// <summary>Called from PlayerBot.OnDeath, once per death.</summary>
        public static void Note(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }

            Total++;

            Mobile killer = bot.LastKiller;
            string what = killer == null ? "unknown" : killer.GetType().Name;

            NavDestination near = bot.Map == null || bot.Map == Map.Internal
                ? null
                : Nav.NearestDestination(bot.Location, bot.Map, null, 0);

            string key = what + " at " + (near == null ? "nowhere" : near.Id);

            int count;
            _byCause.TryGetValue(key, out count);
            _byCause[key] = count + 1;
        }

        /// <summary>"N death(s) since boot (top: 2 Spectre at britain-graveyard, ...)".</summary>
        public static string Describe()
        {
            var text = new StringBuilder(160);

            text.AppendFormat("{0} bot death(s) since boot", Total);

            if (_byCause.Count > 0)
            {
                var causes = new List<KeyValuePair<string, int>>(_byCause);

                causes.Sort((a, b) => b.Value != a.Value
                    ? b.Value.CompareTo(a.Value)
                    : String.CompareOrdinal(a.Key, b.Key));

                var top = new List<string>();

                for (int i = 0; i < causes.Count && i < 3; i++)
                {
                    top.Add(causes[i].Value + " " + causes[i].Key);
                }

                text.Append(" (top: ").Append(String.Join(", ", top.ToArray())).Append(')');
            }

            return text.ToString();
        }
    }
}
