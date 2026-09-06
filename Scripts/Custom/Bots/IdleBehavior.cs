// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// IdleBehavior.cs — stands there.
//
// The default brain, and in this session the only one. Upstream it also mutters
// small talk on a cooldown; that arrives with the chat corpus in the speech
// session, at which point this class gains its ChatCategories and a Tick body.
//
// Until then it is genuinely empty, which is the correct shape for it: a bot
// with no behaviour still has a class, a tier, skills, stats, a name, a speech
// colour and an outfit, and those are what this session is proving.

namespace Server.Custom
{
    public class IdleBehavior : PlayerBotBehavior
    {
        public override string SerializableName
        {
            get { return "Idle"; }
        }

        public override string GetStatusLine(PlayerBot bot)
        {
            return "standing idle";
        }
    }
}
