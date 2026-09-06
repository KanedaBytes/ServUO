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

        public virtual void OnAttached(PlayerBot bot)
        {
        }

        public virtual void OnDetached(PlayerBot bot)
        {
        }
    }
}
