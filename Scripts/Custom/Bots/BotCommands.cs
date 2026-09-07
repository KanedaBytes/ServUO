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
            CommandSystem.Register("BotTrace", AccessLevel.GameMaster, BotTrace_OnCommand);
        }

        [Usage("BotTrace on | off | off all | list")]
        [Description("Echoes one bot's event log to the console. Target the bot after typing it.")]
        private static void BotTrace_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            string what = e.Length == 0 ? "" : e.GetString(0);

            if (Insensitive.Equals(what, "list"))
            {
                List<string> traced = BotLog.TracedNames();

                from.SendMessage(traced.Count == 0
                    ? "No bots are being traced."
                    : String.Format("Tracing {0}: {1}", traced.Count, String.Join(", ", traced.ToArray())));
                return;
            }

            // The all-off escape hatch. Tracing is per bot and a bot is ephemeral, so without this
            // the only way to quiet a console after tracing several is to remember every one of
            // them - and the reason you are turning it off is usually that you cannot read it.
            if (Insensitive.Equals(what, "off")
                && e.Length > 1
                && Insensitive.Equals(e.GetString(1), "all"))
            {
                int cleared = BotLog.TraceNone();

                from.SendMessage(String.Format("Tracing off for {0} bot(s).", cleared));
                return;
            }

            bool on;

            if (Insensitive.Equals(what, "on"))
            {
                on = true;
            }
            else if (Insensitive.Equals(what, "off"))
            {
                on = false;
            }
            else
            {
                from.SendMessage(0x35, "Usage: [BotTrace on | off | off all | list");
                return;
            }

            from.SendMessage(String.Format("Target a bot to turn tracing {0}.", on ? "on" : "off"));
            from.BeginTarget(12, false, TargetFlags.None,
                (m, targeted) => BotTrace_OnTarget(m, targeted, on));
        }

        private static void BotTrace_OnTarget(Mobile from, object targeted, bool on)
        {
            var bot = targeted as PlayerBot;

            if (bot == null)
            {
                from.SendMessage(0x35, "That is not a bot.");
                return;
            }

            BotLog.Trace(bot, on);

            from.SendMessage(String.Format(
                "Tracing {0} for {1}. Its last {2} event(s) are in Data/Live/botlog.json.",
                on ? "on" : "off",
                bot.Name,
                BotLog.Entries(bot).Count));
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

            if (bot.Class == BotClass.Crafter)
            {
                // The legacy one-class-many-subtypes shape. Without this the report says only
                // "Class Crafter", and the station it works - forge, tailor shop, dock - is
                // invisible, which is how a Crafter at the forge came to be told it had no station.
                from.SendMessage(String.Format(
                    "Sub-type {0} (works as {1}).",
                    CrafterTypeHelper.DisplayName(bot.CrafterSpec),
                    BotClassHelper.DisplayName(bot.TradeClass)));
            }

            from.SendMessage(bot.Personality.ToString());

            PlayerBotBehavior behaviour = bot.Behavior;

            if (behaviour != null && bot.Personality.IsAssigned)
            {
                TimeSpan phase = BotLifecycle.Config_.PhaseLength(
                    behaviour.SerializableName, bot.Personality.AveragePhaseDuration);

                TimeSpan elapsed = CustomTime.Now - bot.PhaseStartedAt;
                TimeSpan left = phase - elapsed;

                // CLAMP AND SAY SO rather than print two thousand years.
                //
                // An unset clock used to render as "63924344915s of 14160s elapsed", which reads
                // as a display glitch and is in fact the symptom of a bot the lifecycle will
                // overwrite on its next pass. Naming it is the difference between shrugging at a
                // silly number and finding the bug.
                if (bot.PhaseClockUnset)
                {
                    from.SendMessage(0x35, String.Format(
                        "Phase {0}: CLOCK UNSET - this bot is permanently overdue and the "
                        + "lifecycle will roll it on its next pass.",
                        behaviour.SerializableName));
                }
                else if (behaviour.VisitExpiresAt != null)
                {
                    TimeSpan visit = behaviour.VisitExpiresAt.Value - CustomTime.Now;

                    from.SendMessage(String.Format(
                        "Phase {0}: a timed visit, {1:0} second(s) left.",
                        behaviour.SerializableName,
                        visit.TotalSeconds));
                }
                else
                {
                    if (elapsed < TimeSpan.Zero)
                    {
                        elapsed = TimeSpan.Zero;
                    }

                    if (elapsed > phase)
                    {
                        elapsed = phase;
                    }

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

        /// <summary>
        /// Give a hand-switched work behaviour the destination the arrival handoff would have.
        ///
        /// Returns false when the bot's class has nowhere to work, having said so - a Lumberjack
        /// on a shard with no lumber site, say. Refusing is better than attaching a brain that is
        /// going to walk away within one tick and leave the person who typed the command none the
        /// wiser.
        /// </summary>
        private static bool AssignStation(Mobile from, PlayerBot bot, PlayerBotBehavior chosen)
        {
            var gatherer = chosen as GathererBehavior;
            var crafter = chosen as CrafterBehavior;

            if (gatherer == null && crafter == null)
            {
                return true;
            }

            NavDestination site = BotWorkSites.NearestSiteFor(bot);

            if (site == null)
            {
                from.SendMessage(0x35, String.Format(
                    "{0} is a {1}, and nothing on this facet is a site or station it works.",
                    bot.Name,
                    BotClassHelper.DisplayName(bot.TradeClass)));
                return false;
            }

            if (gatherer != null)
            {
                gatherer.DestinationId = site.Id;
            }
            else
            {
                crafter.DestinationId = site.Id;
            }

            from.SendMessage(String.Format("Sending {0} to {1}.", bot.Name, site.Name));

            return true;
        }

        private static void BotBehaviorSet_OnTarget(Mobile from, object targeted, string name)
        {
            var bot = targeted as PlayerBot;

            if (bot == null)
            {
                from.SendMessage(0x35, "That is not a bot.");
                return;
            }

            PlayerBotBehavior chosen = BotBehaviors.Create(name);

            // A HAND SWITCH IS A TRANSITION, and has to leave the bot in the same state one does.
            //
            // Setting Behavior alone was not enough: the lifecycle asks "is this bot's phase over"
            // on its next pass, and with the phase clock untouched the answer was yes - so every
            // [BotBehavior was quietly rolled away within seconds of being typed. Starting the
            // clock is what buys the behaviour its phase; the visit window is what protects the
            // ones that are visits, exactly as the arrival handoff protects them.
            TimeSpan? window = BotBehaviors.VisitWindowFor(chosen);

            if (window != null)
            {
                chosen.VisitExpiresAt = CustomTime.Now + window.Value;
            }

            // A HAND SWITCH ALSO HAS TO HAND OVER THE DESTINATION.
            //
            // Crafter and Gatherer are arrival handoffs: you become one by turning up somewhere
            // that wants one, and the Traveler passes the destination across at the same moment it
            // passes the brain across. This command passed only the brain, so a hand-switched
            // Gatherer had a null DestinationId - ResolveSite fell through to
            // Nav.Destination(null), found no work zone, and OnAttached walked it straight back to
            // Traveler. Typing [BotBehavior Gatherer therefore appeared to do nothing at all,
            // which is exactly how the walk-in fault below stayed hidden: nobody could hold a
            // gatherer still long enough to watch it fail.
            if (!AssignStation(from, bot, chosen))
            {
                return;
            }

            bot.Behavior = chosen;
            bot.PhaseStartedAt = CustomTime.Now;
            bot.TransitionPending = false;

            from.SendMessage(String.Format(
                "{0} is now {1}{2}.",
                bot.Name,
                bot.Behavior.SerializableName,
                window == null
                    ? " for a full phase"
                    : String.Format(" for {0:0} minute(s)", window.Value.TotalMinutes)));

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
