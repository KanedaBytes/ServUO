using System;
using System.Collections.Generic;

using Server.Commands;
using Server.Engines.Quests;
using Server.Gumps;
using Server.Mobiles;
using Server.Targeting;

namespace Server.Custom
{
    /// <summary>
    /// Staff tooling for the Mondain's Legacy quest engine.
    ///
    /// A player's quest state lives in two unrelated stores. The active instances are in
    /// MondainQuestData (Saves/Quests/MLQuests.bin), reached through PlayerMobile.Quests. The
    /// "already did this" record is a QuestRestartInfo in PlayerMobile.DoneQuests, inside the
    /// PlayerMobile save. Once a DoneOnce quest is recorded, nothing in the game will offer it
    /// again - these commands are the way back.
    ///
    /// Note that BaseQuest.RemoveQuest only writes that record when
    /// Owner.AccessLevel == AccessLevel.Player, so a staff character never accumulates one and
    /// [ResetQuest on one has nothing to clear.
    /// </summary>
    public static class ResetQuestCommands
    {
        public static void Initialize()
        {
            CommandSystem.Register("ResetQuest", AccessLevel.GameMaster, ResetQuest_OnCommand);
            CommandSystem.Register("ResetAllQuests", AccessLevel.GameMaster, ResetAllQuests_OnCommand);
        }

        [Usage("ResetQuest <QuestTypeName>")]
        [Description("Targets a player and clears one quest, so it can be offered to them again.")]
        private static void ResetQuest_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length != 1)
            {
                from.SendMessage("Usage: [ResetQuest <QuestTypeName>");
                return;
            }

            string name = e.GetString(0);

            // FindTypeByName matches short names only and does not fall back, so try both forms.
            Type type = ScriptCompiler.FindTypeByName(name) ?? ScriptCompiler.FindTypeByFullName(name);

            // ServUO has no quest registry to validate against - the binding is MondainQuester.Quests
            // on each NPC. Assignability is therefore the guard, and it is also what stops a short
            // name that collided with some unrelated type from acting on the wrong thing.
            if (type == null || !typeof(BaseQuest).IsAssignableFrom(type) || type.IsAbstract)
            {
                from.SendMessage(0x35, String.Format("'{0}' is not the name of a quest type.", name));
                return;
            }

            from.SendMessage(String.Format("Select the player whose '{0}' progress should be reset.", type.Name));
            from.Target = new ResetQuestTarget(type);
        }

        [Usage("ResetAllQuests")]
        [Description("Targets a player and erases all of their quest progress, after confirmation.")]
        private static void ResetAllQuests_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage("Select the player whose quest progress should be erased.");
            e.Mobile.Target = new ResetAllQuestsTarget();
        }

        /// <summary>
        /// Cancels an in-progress instance. OnResign(true) is the engine's own cancel path: it
        /// un-flags obtain items through QuestHelper.RemoveStatus, deletes deliver items, drops the
        /// chain entry and removes the quest from the player's list.
        ///
        /// It is not called for a player with no backpack: RemoveStatus dereferences
        /// from.Backpack unchecked.
        /// </summary>
        private static bool CancelInstance(Mobile staff, PlayerMobile player, Type type)
        {
            BaseQuest quest = QuestHelper.GetQuest(player, type);

            if (quest == null)
            {
                return false;
            }

            if (player.Backpack == null)
            {
                staff.SendMessage(
                    0x35,
                    String.Format("{0} has no backpack; '{1}' cannot be cancelled safely.", player.Name, type.Name));
                return false;
            }

            quest.OnResign(true);

            return true;
        }

        private static bool ClearRecord(PlayerMobile player, Type type)
        {
            QuestRestartInfo info = QuestHelper.GetRestartInfo(player, type);

            if (info == null)
            {
                return false;
            }

            player.DoneQuests.Remove(info);

            return true;
        }

        private class ResetQuestTarget : Target
        {
            private readonly Type m_Quest;

            public ResetQuestTarget(Type quest)
                : base(-1, false, TargetFlags.None)
            {
                m_Quest = quest;
            }

            protected override void OnTarget(Mobile from, object targeted)
            {
                var player = targeted as PlayerMobile;

                if (player == null)
                {
                    from.SendMessage(0x35, "That is not a player.");
                    return;
                }

                bool wasInProgress = CancelInstance(from, player, m_Quest);
                bool wasCompleted = ClearRecord(player, m_Quest);

                if (!wasInProgress && !wasCompleted)
                {
                    from.SendMessage(
                        String.Format(
                            "{0} has no progress on '{1}'.{2}",
                            player.Name,
                            m_Quest.Name,
                            player.AccessLevel > AccessLevel.Player
                                ? " Staff characters never get a completion record, so there is nothing to clear."
                                : ""));
                    return;
                }

                from.SendMessage(
                    String.Format(
                        "Reset '{0}' for {1}. In progress: {2}. Completed: {3}.",
                        m_Quest.Name,
                        player.Name,
                        wasInProgress,
                        wasCompleted));

                CommandLogging.WriteLine(
                    from,
                    String.Format(
                        "{0} {1} resetting quest '{2}' for {3}",
                        from.AccessLevel,
                        CommandLogging.Format(from),
                        m_Quest.Name,
                        CommandLogging.Format(player)));
            }
        }

        private class ResetAllQuestsTarget : Target
        {
            public ResetAllQuestsTarget()
                : base(-1, false, TargetFlags.None)
            {
            }

            protected override void OnTarget(Mobile from, object targeted)
            {
                var player = targeted as PlayerMobile;

                if (player == null)
                {
                    from.SendMessage(0x35, "That is not a player.");
                    return;
                }

                int active = player.Quests.Count;
                int done = player.DoneQuests.Count;
                int chains = player.Chains.Count;

                if (active == 0 && done == 0 && chains == 0)
                {
                    from.SendMessage(String.Format("{0} has no quest progress to erase.", player.Name));
                    return;
                }

                string content = String.Format(
                    "You are about to erase <b>all</b> quest progress for {0}.<br><br>" +
                    "This cancels {1} quest(s) currently in progress, clears {2} completed-quest " +
                    "record(s) and discards {3} chain entr(y/ies). Quest-granted abilities such as " +
                    "Spellweaving and Bedlam access are not affected.<br><br>" +
                    "This cannot be undone without a server revert. Continue?",
                    player.Name,
                    active,
                    done,
                    chains);

                from.CloseGump(typeof(WarningGump));
                from.SendGump(
                    new WarningGump(
                        1060635, // <CENTER>WARNING</CENTER>
                        30720,
                        content,
                        0xFFC000,
                        420,
                        280,
                        new WarningGumpCallback(OnConfirm),
                        player));
            }

            private static void OnConfirm(Mobile from, bool okay, object state)
            {
                if (!okay)
                {
                    from.SendMessage("Quest reset aborted.");
                    return;
                }

                var player = state as PlayerMobile;

                if (player == null || player.Deleted)
                {
                    from.SendMessage(0x35, "That player no longer exists.");
                    return;
                }

                // Re-fetch: the staff member may have sat on the gump for a while.
                List<BaseQuest> quests = player.Quests;

                int cancelled = 0;

                // Reverse index: OnResign -> RemoveQuest removes the quest from this very list.
                for (int i = quests.Count - 1; i >= 0; i--)
                {
                    BaseQuest quest = quests[i];

                    if (quest == null)
                    {
                        continue;
                    }

                    if (player.Backpack == null)
                    {
                        // RemoveStatus would throw. Drop the instance without the tidy-up rather
                        // than abandoning the whole reset.
                        quests.RemoveAt(i);
                        cancelled++;
                        continue;
                    }

                    quest.OnResign(true);
                    cancelled++;
                }

                int records = player.DoneQuests.Count;

                player.DoneQuests.Clear();
                player.Chains.Clear();

                // Quest-granted abilities (PlayerFlag.Spellweaving, PlayerFlag.Bedlam) are
                // deliberately left intact - revoking a castable skill is not what
                // "reset quests" means.

                from.SendMessage(
                    String.Format(
                        "Erased all quest progress for {0}. Cancelled {1} active quest(s), cleared {2} record(s).",
                        player.Name,
                        cancelled,
                        records));

                CommandLogging.WriteLine(
                    from,
                    String.Format(
                        "{0} {1} erasing ALL quest progress for {2}",
                        from.AccessLevel,
                        CommandLogging.Format(from),
                        CommandLogging.Format(player)));
            }
        }
    }
}
