// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// PlayerBotBehavior.cs — the brain contract.
//
// A behaviour is what a bot is currently doing. Swapping the Behavior property
// swaps the brain; OnAttached and OnDetached fire either side of the change.
//
// The upstream base carries speech scheduling, chat categories, cooldowns and a
// timed-visit contract. None of that is ported yet - there is no chat corpus in
// this session and nothing to visit - so what is here is the minimum a brain
// needs to exist and be swapped safely. The members are shaped the way the
// upstream ones are, so the speech layer drops onto this rather than replacing
// it.

using System;

namespace Server.Custom
{
    public abstract class PlayerBotBehavior
    {
        /// <summary>
        /// The name this behaviour serializes as, and the key it is constructed back from.
        ///
        /// The only abstract member, deliberately: the whole lifecycle model is "the state IS the
        /// behaviour name", so a brain that cannot name itself cannot be persisted or transitioned
        /// into.
        /// </summary>
        public abstract string SerializableName { get; }

        /// <summary>
        /// One line describing what this bot is doing right now, for [BotInfo and the live map.
        /// Null means "nothing worth saying beyond the behaviour's name".
        /// </summary>
        public virtual string GetStatusLine(PlayerBot bot)
        {
            return null;
        }

        /// <summary>
        /// Called on the game thread on the behaviour tick, once the tick manager exists.
        ///
        /// Nothing calls this yet: this session has no tick manager, because Idle is the only
        /// behaviour and it would have nothing to do. It lands with the movement session.
        /// </summary>
        public virtual void Tick(PlayerBot bot)
        {
        }

        /// <summary>
        /// May the lifecycle interrupt this behaviour right now?
        ///
        /// Default true. A behaviour says no when being swapped mid-action would look broken
        /// rather than natural - a Traveler halfway down a street, a Shopper mid-visit. The
        /// lifecycle skips it and asks again on the next pass rather than forcing it.
        /// </summary>
        public virtual bool CanTransition(PlayerBot bot)
        {
            return true;
        }

        /// <summary>
        /// When a timed visit ends, or null for an open-ended behaviour.
        ///
        /// A behaviour reached by ARRIVING somewhere - a BankSitter at a bank, a Shopper at a shop -
        /// is a visit: it runs for a while and then hands the bot back to travelling. A behaviour
        /// reached by a LIFECYCLE ROLL is open-ended and ends when its phase does.
        ///
        /// Set by whoever performs the handoff, not by the behaviour itself. Not serialized: a
        /// visit does not survive a restart, and nor does the bot.
        /// </summary>
        public DateTime? VisitExpiresAt { get; set; }

        /// <summary>
        /// Ends the visit if it has lapsed, returning true if it did.
        ///
        /// Call it as the first thing in Tick, and RETURN IMMEDIATELY when it returns true: it has
        /// already swapped the bot back to a Traveler, which detached this behaviour. Touching any
        /// more state afterwards is touching a brain the bot no longer has.
        /// </summary>
        protected bool CheckVisitExpired(PlayerBot bot)
        {
            if (VisitExpiresAt == null || CustomTime.Now < VisitExpiresAt.Value)
            {
                return false;
            }

            bot.Behavior = BotBehaviors.Create("Traveler");

            return true;
        }

        public virtual void OnAttached(PlayerBot bot)
        {
        }

        public virtual void OnDetached(PlayerBot bot)
        {
        }
    }
}
