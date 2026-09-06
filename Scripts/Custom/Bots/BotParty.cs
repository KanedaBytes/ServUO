// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotParty.cs — a bot answers a party invitation.
//
// THE PROBLEM. Party.Invite does four things (Scripts/Services/Party/Party.cs:167):
// adds the target to the party's Candidates, sets target.Party to the LEADER
// MOBILE (not a Party - that is what "pending" looks like), sends the invitation
// packet, and starts a 30-second DeclineTimer. A player then types /accept, which
// reaches PartyCommands.Handler.OnAccept.
//
// A bot has no NetState, so the invitation packet goes nowhere and nobody ever
// types anything. The invite is not refused - the "Nay, I would rather stay here
// and watch a nail rust" gate is passed, because PlayerBot sets Mobile.Player -
// it simply expires, and thirty seconds later the leader is told the bot "does
// not wish to join the party."
//
// THE FIX. Notice the pending invite and walk the same path /accept walks:
// PartyCommands.Handler.OnAccept(bot, leader). Not Party.OnAccept directly - the
// handler is where the capacity check and the candidate check live, and going
// around them would let a bot join a full party.
//
// WHERE IT IS NOTICED. PlayerBot.OnThink, which BaseAI's timer calls only while a
// player is in the sector (BaseAI.cs:3072-3082). That is exactly when an invite
// can arrive - somebody has to be standing there to send one - so this costs
// nothing at all for a bot alone in the woods, and needs no sweep of its own.
//
// NOTHING IS CACHED. Party.Remove and Party.Disband both write m.Party = null
// straight onto the Mobile (Party.cs:297, :343), with no packet involved, so the
// engine already clears the reference when a leader kicks the bot or the party
// breaks up. The way to hold a stale reference here is to keep one of our own, so
// this file keeps none: every decision re-reads bot.Party.

using System;

using Server.Engines.PartySystem;
using Server.Mobiles;

namespace Server.Custom
{
    public static class BotParty
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// How long a bot takes to answer, in seconds. A person has to read the gump and click,
        /// and a bot that joins the same tick it is asked is uncanny. Well inside the 30-second
        /// DeclineTimer either way.
        /// </summary>
        public const double MinAcceptDelay = 1.5;

        public const double MaxAcceptDelay = 4.0;

        /// <summary>
        /// Answer a pending invitation, if there is one.
        ///
        /// Accepts unconditionally for now. Whether a bot *should* join - it is mid-errand, it is
        /// an outlaw, it dislikes the asker - is the social layer's decision, and it belongs with
        /// the personality and lifecycle it would read from.
        /// </summary>
        public static void CheckInvite(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.PartyAcceptPending)
            {
                return;
            }

            // Pending invite: Party holds the inviting leader. Once joined it holds a Party, and
            // a bot with no invitation holds null - so this one test distinguishes all three.
            var leader = bot.Party as Mobile;

            if (leader == null)
            {
                return;
            }

            bot.PartyAcceptPending = true;

            double delay = MinAcceptDelay + (Utility.RandomDouble() * (MaxAcceptDelay - MinAcceptDelay));

            Timer.DelayCall(TimeSpan.FromSeconds(delay), () => Accept(bot, leader));
        }

        private static void Accept(PlayerBot bot, Mobile leader)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            bot.PartyAcceptPending = false;

            // Re-read rather than trusting the captured leader. In the seconds since the invite
            // the leader may have disbanded, kicked the bot, or invited it somewhere else, and
            // any of those rewrites bot.Party underneath us.
            if (!ReferenceEquals(bot.Party, leader))
            {
                return;
            }

            if (leader.Deleted || PartyCommands.Handler == null)
            {
                return;
            }

            try
            {
                // The same call /accept makes. The handler re-derives the leader from bot.Party,
                // checks the party still exists, checks the bot is still a candidate and checks
                // capacity - all of which can have changed while the bot was "reading the gump".
                PartyCommands.Handler.OnAccept(bot, leader);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{0} failed to accept a party invite from {1}.", bot.Name, leader.Name);
            }
        }

        /// <summary>
        /// Decline a still-pending invitation on the bot's way out.
        ///
        /// Without this, a bot deleted between invite and accept stays in the party's Candidates
        /// list as a deleted Mobile, and the leader waits the full thirty seconds to be told
        /// somebody who no longer exists is not interested.
        /// </summary>
        public static void OnBotDeleted(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }

            bot.PartyAcceptPending = false;

            var leader = bot.Party as Mobile;

            if (leader == null || leader.Deleted || PartyCommands.Handler == null)
            {
                return;
            }

            try
            {
                PartyCommands.Handler.OnDecline(bot, leader);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{0} failed to decline a party invite on delete.", bot.Name);
            }
        }

        /// <summary>True when this bot is a full member of a party (not merely invited).</summary>
        public static bool InParty(PlayerBot bot)
        {
            return bot != null && Party.Get(bot) != null;
        }
    }
}
