using System;
using System.Collections.Generic;

using Server.Commands;
using Server.Targeting;

namespace Server.Custom
{
    public static class BotCommands
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        public static void Initialize()
        {
            CommandSystem.Register("SpawnBot", AccessLevel.GameMaster, SpawnBot_OnCommand);
            CommandSystem.Register("BotInfo", AccessLevel.GameMaster, BotInfo_OnCommand);
            CommandSystem.Register("BotBehavior", AccessLevel.GameMaster, BotBehavior_OnCommand);
            CommandSystem.Register("BotLifecycle", AccessLevel.GameMaster, BotLifecycle_OnCommand);
            CommandSystem.Register("BotsReload", AccessLevel.GameMaster, BotsReload_OnCommand);
            CommandSystem.Register("ReloadBots", AccessLevel.GameMaster, BotsReload_OnCommand);
            CommandSystem.Register("BotSmoke", AccessLevel.Administrator, BotSmoke_OnCommand);
        }

        [Usage("SpawnBot [class] [tier]")]
        [Aliases("SpawnTestBot")]
        [Description("Spawns a bot at your feet. Class and tier are rolled when omitted.")]
        private static void SpawnBot_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (from.Map == null || from.Map == Map.Internal)
            {
                from.SendMessage(0x35, "You are not on a facet.");
                return;
            }

            BotClass cls = BotClassHelper.RollRandom();
            BotSkillTier tier = BotSkillTierHelper.RollRandom();

            // Free-order arguments: whichever token parses as a class is the class, whichever
            // parses as a tier is the tier. [SpawnBot 6 mage reads as well as [SpawnBot mage 6.
            for (int i = 0; i < e.Length; i++)
            {
                string argument = e.GetString(i);

                BotClass parsedClass;
                BotSkillTier parsedTier;

                if (BotClassHelper.TryParse(argument, out parsedClass))
                {
                    cls = parsedClass;
                }
                else if (BotSkillTierHelper.TryParse(argument, out parsedTier))
                {
                    tier = parsedTier;
                }
                else
                {
                    from.SendMessage(0x35, String.Format("'{0}' is not a bot class or tier.", argument));
                    from.SendMessage(0x35, "Usage: [SpawnBot [class] [tier]");
                    SendVocabulary(from);
                    return;
                }
            }

            PlayerBot bot;

            try
            {
                bot = new PlayerBot(cls, tier);
            }
            catch (Exception ex)
            {
                from.SendMessage(0x35, "The bot could not be built: " + ex.Message);
                Log.Error(ex, "[SpawnBot failed for {0}/{1}.", cls, tier);
                return;
            }

            bot.MoveToWorld(from.Location, from.Map);

            from.SendMessage(String.Format(
                "Spawned {0}, {1}.",
                bot.Name,
                bot.Title));

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} spawning a {2} {3} bot named {4}",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    tier,
                    cls,
                    bot.Name));
        }

        [Usage("BotInfo")]
        [Description("Target a bot and dump its class, tier, stats and skills.")]
        private static void BotInfo_OnCommand(CommandEventArgs e)
        {
            e.Mobile.SendMessage("Target a bot.");
            e.Mobile.BeginTarget(12, false, TargetFlags.None, BotInfo_OnTarget);
        }

        private static void BotInfo_OnTarget(Mobile from, object targeted)
        {
            var bot = targeted as PlayerBot;

            if (bot == null)
            {
                from.SendMessage(0x35, "That is not a bot.");
                return;
            }

            BotCaps caps = BotSystem.Caps;

            from.SendMessage(0x3B2, String.Format("{0} {1}", bot.Name, bot.Title));
            from.SendMessage(String.Format(
                "Class {0}, tier {1}. {2}",
                BotClassHelper.DisplayName(bot.Class),
                BotSkillTierHelper.DisplayName(bot.SkillTier),
                bot.Behavior != null ? bot.Behavior.GetStatusLine(bot) ?? bot.Behavior.SerializableName : "no brain"));

            int statTotal = bot.RawStr + bot.RawDex + bot.RawInt;

            from.SendMessage(String.Format(
                "Str {0}  Dex {1}  Int {2}  (total {3} of {4})",
                bot.RawStr,
                bot.RawDex,
                bot.RawInt,
                statTotal,
                caps.StatTotal));

            double skillTotal = 0.0;
            var lines = new List<string>();

            for (int i = 0; i < bot.Skills.Length; i++)
            {
                Skill skill = bot.Skills[i];

                if (skill.Base <= 0.0)
                {
                    continue;
                }

                skillTotal += skill.Base;
                lines.Add(String.Format("{0} {1:0.0}", skill.SkillName, skill.Base));
            }

            from.SendMessage(String.Format(
                "Skills, total {0:0.0} of {1:0.0}:",
                skillTotal,
                caps.SkillTotal));

            foreach (string line in lines)
            {
                from.SendMessage("  " + line);
            }

            from.SendMessage(bot.Personality.ToString());

            PlayerBotBehavior behaviour = bot.Behavior;

            if (behaviour != null && bot.Personality.IsAssigned)
            {
                TimeSpan phase = BotLifecycle.Config_.PhaseLength(
                    behaviour.SerializableName, bot.Personality.AveragePhaseDuration);

                TimeSpan elapsed = CustomTime.Now - bot.PhaseStartedAt;
                TimeSpan left = phase - elapsed;

                if (behaviour.VisitExpiresAt != null)
                {
                    TimeSpan visit = behaviour.VisitExpiresAt.Value - CustomTime.Now;

                    from.SendMessage(String.Format(
                        "Phase {0}: a timed visit, {1:0} second(s) left.",
                        behaviour.SerializableName,
                        visit.TotalSeconds));
                }
                else
                {
                    from.SendMessage(String.Format(
                        "Phase {0}: {1:0}s of {2:0}s elapsed, {3:0}s left.",
                        behaviour.SerializableName,
                        elapsed.TotalSeconds,
                        phase.TotalSeconds,
                        left.TotalSeconds > 0 ? left.TotalSeconds : 0));
                }
            }
        }

        [Usage("BotLifecycle [on|off]")]
        [Description("Reports the phase roller, or pauses it so a behaviour can be watched.")]
        private static void BotLifecycle_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length == 0)
            {
                from.SendMessage(String.Format(
                    "Lifecycle is {0}; every {1:0}s, {2} transition(s) so far: {3}",
                    BotLifecycle.Enabled ? "running" : "PAUSED",
                    BotLifecycle.Interval.TotalSeconds,
                    BotLifecycle.TotalTransitions,
                    BotLifecycle.DescribeTransitions()));
                return;
            }

            string argument = e.GetString(0);

            if (Insensitive.Equals(argument, "on"))
            {
                BotLifecycle.Enabled = true;
            }
            else if (Insensitive.Equals(argument, "off"))
            {
                BotLifecycle.Enabled = false;
            }
            else
            {
                from.SendMessage(0x35, "Usage: [BotLifecycle [on|off]");
                return;
            }

            from.SendMessage(String.Format(
                "Lifecycle {0}.", BotLifecycle.Enabled ? "running" : "paused"));

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} turning the bot lifecycle {2}",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    BotLifecycle.Enabled ? "on" : "off"));
        }

        [Usage("BotBehavior [name]")]
        [Description("Target a bot to report its brain, or give a name to switch it.")]
        private static void BotBehavior_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length == 0)
            {
                from.SendMessage("Target a bot to report its behaviour.");
                from.BeginTarget(12, false, TargetFlags.None, BotBehaviorReport_OnTarget);
                return;
            }

            string name = e.GetString(0);

            if (!BotBehaviors.IsKnown(name))
            {
                from.SendMessage(0x35, String.Format("'{0}' is not a bot behaviour.", name));
                from.SendMessage(0x35, "Known: " + String.Join(", ", BotBehaviors.Names()));
                return;
            }

            from.SendMessage(String.Format("Target a bot to switch it to {0}.", name));
            from.BeginTarget(12, false, TargetFlags.None,
                (m, targeted) => BotBehaviorSet_OnTarget(m, targeted, name));
        }

        private static void BotBehaviorReport_OnTarget(Mobile from, object targeted)
        {
            var bot = targeted as PlayerBot;

            if (bot == null)
            {
                from.SendMessage(0x35, "That is not a bot.");
                return;
            }

            PlayerBotBehavior behavior = bot.Behavior;

            if (behavior == null)
            {
                from.SendMessage(0x35, String.Format("{0} has no behaviour at all.", bot.Name));
                return;
            }

            string status = behavior.GetStatusLine(bot);

            from.SendMessage(0x3B2, String.Format(
                "{0}: {1}{2}",
                bot.Name,
                behavior.SerializableName,
                String.IsNullOrEmpty(status) ? "" : " - " + status));

            // The voice. Which categories it draws from is the first thing to check when a bot is
            // saying the wrong sort of thing, and the line count is the first thing to check when
            // it is saying nothing - the two questions this command is actually asked.
            var sitter = behavior as BankSitterBehavior;

            from.SendMessage(0x3B2, String.Format(
                "  voice: {0}, {1} line(s) said, hue {2}{3}",
                behavior.ChatCategories == null || behavior.ChatCategories.Length == 0
                    ? "silent"
                    : String.Join(", ", behavior.ChatCategories),
                bot.SpeechLines,
                bot.SpeechHue,
                sitter == null ? "" : ", role " + sitter.Role));
        }

        private static void BotBehaviorSet_OnTarget(Mobile from, object targeted, string name)
        {
            var bot = targeted as PlayerBot;

            if (bot == null)
            {
                from.SendMessage(0x35, "That is not a bot.");
                return;
            }

            bot.Behavior = BotBehaviors.Create(name);

            from.SendMessage(String.Format("{0} is now {1}.", bot.Name, bot.Behavior.SerializableName));

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} setting {2}'s behaviour to {3}",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    bot.Name,
                    bot.Behavior.SerializableName));
        }

        [Usage("BotsReload")]
        [Aliases("ReloadBots")]
        [Description("Re-read Data/Custom/bots.json and the player caps it defaults from.")]
        private static void BotsReload_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            string error;
            IList<string> errors;

            if (!BotSystem.TryReload(out error, out errors))
            {
                from.SendMessage(0x35, "Bot config NOT reloaded: " + error);

                if (errors != null)
                {
                    foreach (string line in errors)
                    {
                        from.SendMessage(0x35, "  " + line);
                    }
                }

                return;
            }

            // Re-check the work sites against whatever graph is loaded now. Validate runs once at
            // Initialize, so without this a site fixed by an edit to navigation.json stays
            // excluded until a restart - and the exclusion is invisible except in Bots.Work,
            // which makes it exactly the kind of thing somebody rediscovers an hour later.
            //
            // It lives here rather than in [NavReload on purpose: the navigation layer is Core
            // and knows nothing about bots, and inverting that to save a keystroke would be a
            // poor trade. Edit the graph, [NavReload, then [BotsReload.
            BotWorkSites.Validate(Map.Trammel);

            from.SendMessage("Bot config reloaded. " + BotSystem.Caps.Describe());

            foreach (var entry in BotWorkSites.Excluded)
            {
                from.SendMessage(0x35, String.Format("  work site '{0}' excluded: {1}", entry.Key, entry.Value));
            }

            foreach (string line in BotWorkSites.Stationless)
            {
                from.SendMessage(0x35, "  " + line);
            }

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} reloading the bot config",
                    from.AccessLevel,
                    CommandLogging.Format(from)));
        }

        [Usage("BotSmoke")]
        [Description("Spawn one bot per class, check every one against the configured caps, and delete them.")]
        private static void BotSmoke_OnCommand(CommandEventArgs e)
        {
            BotSmoke.Run(e.Mobile);
        }

        private static void SendVocabulary(Mobile from)
        {
            var classes = new List<string>();

            foreach (BotClass cls in BotClassHelper.Rollable())
            {
                classes.Add(cls.ToString());
            }

            classes.Sort(StringComparer.Ordinal);

            from.SendMessage("Classes: " + String.Join(", ", classes.ToArray()));
            from.SendMessage("Tiers: Novice, Apprentice, Journeyman, Adept, Expert, Master, Grandmaster (or 0-6).");
        }
    }
}
