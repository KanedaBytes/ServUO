// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotRole.cs — is this bot furniture, or is it somebody having a session?
//
// Upstream asks that question of the SPAWNER's TYPE: PlayerBot.OnAfterSpawn
// reads `Spawner is FixedRoleBotSpawner` and stamps LifecycleExempt from it
// (uo-offline PlayerBot.cs:641-645), and FixedRoleBotSpawner exists for no
// other reason - it declares no state of its own and says so in its header.
//
// That cannot work here, and the reason is worth writing down because it is
// the hinge the whole population layer turns on. This shard spawns through
// XmlSpawner2, and XmlSpawner calls OnAfterSpawn() at XmlSpawner2.cs:9337 and
// only THEN applies the spawn string's property setters at :9344. So a bot
// placed by an XmlSpawner cannot learn anything from its spawner inside
// OnAfterSpawn - the seed has not been handed to it yet.
//
// So the role is a property on the BOT, set from the spawn string. That is
// strictly more useful than a spawner subclass: it also works from [set, from
// [props, and from [SpawnBot, none of which have a spawner at all.

namespace Server.Custom
{
    /// <summary>
    /// Whether a bot is part of the breathing population or part of the scenery.
    ///
    /// An enum rather than a bool because VocabularySnapshot exports C# enums to the editor for
    /// free, and the spawner form needs exactly this list in a dropdown.
    /// </summary>
    public enum BotRole
    {
        /// <summary>
        /// The default. Rolls behaviours on the lifecycle, counts toward the session curve, and
        /// logs out when its session ends. Somebody playing.
        /// </summary>
        Lifecycle = 0,

        /// <summary>
        /// Never re-rolled by BotLifecycle, never logged out by BotSession, and never counted
        /// toward the curve's target.
        ///
        /// Upstream's reasoning, kept: "fixtures are furniture, not sessions - the bank crowd
        /// exists at 5am like it exists at noon" (uo-offline PlayerBotSpawner.cs:70-72). It is
        /// also what makes the economy session's premise true at every hour rather than only at
        /// the evening peak: a forge is staffed by construction, so the curve must not be able to
        /// empty it at the 05:00 trough.
        /// </summary>
        Fixed = 1
    }
}
