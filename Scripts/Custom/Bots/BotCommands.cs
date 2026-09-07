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

        /// <summary>
        /// One bot, laid out to be read.
        ///
        /// Same data as before, in the shape uo-offline's BotInfoCommand uses: a header, what the
        /// bot is doing, its stats, its notoriety, then its skills in an aligned column. The old
        /// output ran the class, the tier and the status line together on one wrapped line and
        /// printed skills as "Mining 87.4" with no alignment, which is legible for three skills
        /// and not for twelve.
        ///
        /// The shard's own extras are kept and folded in rather than dropped: the caps each total
        /// is measured against, the Crafter sub-type, the personality, and the phase clock. The
        /// caps in particular are the reason this command exists - a bot four classes over the
        /// skill budget is only actionable if you can see which number broke it.
        /// </summary>
        private static void BotInfo_OnTarget(Mobile from, object targeted)
        {
            var bot = targeted as PlayerBot;

            if (bot == null)
            {
                from.SendMessage(0x35, "That is not a bot.");
                return;
            }

            BotCaps caps = BotSystem.Caps;

            var lines = new List<string>();

            // --- header ---------------------------------------------------------------------
            lines.Add(String.Format("--- {0} ---", bot.Name));

            if (!String.IsNullOrEmpty(bot.Title))
            {
                lines.Add(String.Format("  {0}", bot.Title));
            }

            lines.Add(String.Format(
                "Class: {0}   Tier: {1}",
                BotClassHelper.DisplayName(bot.Class),
                BotSkillTierHelper.DisplayName(bot.SkillTier)));

            if (bot.Class == BotClass.Crafter)
            {
                // The legacy one-class-many-subtypes shape. Without this the report says only
                // "Class Crafter", and the station it works - forge, tailor shop, dock - is
                // invisible, which is how a Crafter at the forge came to be told it had no
                // station.
                lines.Add(String.Format(
                    "Sub-type: {0}   Works as: {1}",
                    CrafterTypeHelper.DisplayName(bot.CrafterSpec),
                    BotClassHelper.DisplayName(bot.TradeClass)));
            }

            // --- what it is doing -----------------------------------------------------------
            PlayerBotBehavior behaviour = bot.Behavior;

            lines.Add(String.Format(
                "Behavior: {0}",
                behaviour != null ? behaviour.SerializableName : "no brain"));

            if (behaviour != null)
            {
                string status = behaviour.GetStatusLine(bot);

                if (!String.IsNullOrEmpty(status))
                {
                    lines.Add(String.Format("  {0}", status));
                }

                string destination = behaviour.CurrentDestinationId;

                lines.Add(String.Format(
                    "Destination: {0}", String.IsNullOrEmpty(destination) ? "-" : destination));

                // Leg progress, which is the number that says whether a walker is moving or
                // wedged. A bot standing still on step 4 of 19 for a minute is the shape of every
                // stuck-walker fault this system has had.
                NavWalker walker = behaviour.Walker;

                if (walker != null && walker.Active && walker.Route != null)
                {
                    lines.Add(String.Format(
                        "Leg: step {0} of {1}{2}",
                        walker.StepIndex,
                        walker.Route.Count,
                        walker.TotalRungsFired > 0
                            ? String.Format("   stuck-recovery fired {0}x", walker.TotalRungsFired)
                            : ""));
                }
            }

            // --- stats ----------------------------------------------------------------------
            int statTotal = bot.RawStr + bot.RawDex + bot.RawInt;

            lines.Add(String.Format(
                "Str/Dex/Int: {0} / {1} / {2}   (total {3} of {4})",
                bot.RawStr,
                bot.RawDex,
                bot.RawInt,
                statTotal,
                caps.StatTotal));

            lines.Add(String.Format(
                "HP: {0}/{1}   Stam: {2}/{3}   Mana: {4}/{5}",
                bot.Hits, bot.HitsMax,
                bot.Stam, bot.StamMax,
                bot.Mana, bot.ManaMax));

            // --- notoriety ------------------------------------------------------------------
            //
            // What the guards read when they decide whether to attack. Criminal or Murderer true
            // on a freshly spawned bot is a bug, and this block is where you see it.
            lines.Add("Notoriety:");
            lines.Add(String.Format(
                "  Criminal: {0}   Murderer: {1}   Kills: {2}",
                bot.Criminal, bot.Murderer, bot.Kills));
            lines.Add(String.Format(
                "  Karma: {0}   Fame: {1}", bot.Karma, bot.Fame));
            lines.Add(String.Format(
                "  AccessLevel: {0}   Player: {1}", bot.AccessLevel, bot.Player));

            // --- skills ---------------------------------------------------------------------
            double skillTotal = 0.0;
            var named = new List<Skill>();

            for (int i = 0; i < bot.Skills.Length; i++)
            {
                Skill skill = bot.Skills[i];

                if (skill.Base <= 0.0)
                {
                    continue;
                }

                skillTotal += skill.Base;
                named.Add(skill);
            }

            if (named.Count == 0)
            {
                lines.Add("Skills: none above zero.");
            }
            else
            {
                // Highest first. A bot's identity is its top two or three skills, and sorting by
                // the enum order buries them wherever the alphabet put them.
                named.Sort(
                    delegate(Skill a, Skill b) { return b.Base.CompareTo(a.Base); });

                lines.Add(String.Format(
                    "Skills, total {0:0.0} of {1:0.0}:", skillTotal, caps.SkillTotal));

                foreach (Skill skill in named)
                {
                    lines.Add(String.Format(
                        "  {0}{1:0.0}",
                        (skill.SkillName.ToString() + ":").PadRight(16),
                        skill.Base));
                }
            }

            // --- personality and the phase clock ---------------------------------------------
            lines.Add(bot.Personality.ToString());

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
                    lines.Add(String.Format(
                        "Phase {0}: CLOCK UNSET - this bot is permanently overdue and the "
                        + "lifecycle will roll it on its next pass.",
                        behaviour.SerializableName));
                }
                else if (behaviour.VisitExpiresAt != null)
                {
                    TimeSpan visit = behaviour.VisitExpiresAt.Value - CustomTime.Now;

                    lines.Add(String.Format(
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

                    lines.Add(String.Format(
                        "Phase {0}: {1:0}s of {2:0}s elapsed, {3:0}s left.",
                        behaviour.SerializableName,
                        elapsed.TotalSeconds,
                        phase.TotalSeconds,
                        left.TotalSeconds > 0 ? left.TotalSeconds : 0));
                }
            }
        
            CommandReport.Send(from, String.Format("[BotInfo {0}", bot.Name), lines);
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
        private static bool AssignStation(PlayerBot bot, PlayerBotBehavior chosen, out string note)
        {
            note = null;

            var gatherer = chosen as GathererBehavior;
            var crafter = chosen as CrafterBehavior;

            if (gatherer == null && crafter == null)
            {
                return true;
            }

            NavDestination site = BotWorkSites.NearestSiteFor(bot);

            if (site == null)
            {
                note = String.Format(
                    "{0} is a {1}, and nothing on this facet is a site or station it works.",
                    bot.Name,
                    BotClassHelper.DisplayName(bot.TradeClass));
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

            note = "Sending it to " + site.Name + ".";

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

            string message;

            if (!TryHandSwitch(bot, name, out message))
            {
                from.SendMessage(0x35, message);
                return;
            }

            from.SendMessage(message);

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} setting {2}'s behaviour to {3}",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    bot.Name,
                    bot.Behavior.SerializableName));
        }

        /// <summary>
        /// Switch a bot's brain by hand, and leave it in the state a real transition leaves it in.
        ///
        /// Public and separated from the targeting so the LIFE PROBE can drive the same code rather
        /// than a copy of it. That is the whole point: the fault this guards against was a hand
        /// switch that looked correct and was undone seconds later, and a probe asserting against
        /// its own reimplementation of the switch would not have caught it.
        ///
        /// Returns false with the refusal in <paramref name="message"/>, true with the
        /// confirmation.
        /// </summary>
        public static bool TryHandSwitch(PlayerBot bot, string name, out string message)
        {
            message = null;

            if (bot == null || bot.Deleted)
            {
                message = "That bot is gone.";
                return false;
            }

            if (!BotBehaviors.IsKnown(name))
            {
                message = String.Format("'{0}' is not a bot behaviour.", name);
                return false;
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
            string station;

            if (!AssignStation(bot, chosen, out station))
            {
                message = station;
                return false;
            }

            bot.SetBehavior(chosen, "hand switch");
            bot.PhaseStartedAt = CustomTime.Now;
            bot.TransitionPending = false;

            message = String.Format(
                "{0} is now {1}{2}.{3}",
                bot.Name,
                bot.Behavior.SerializableName,
                window == null
                    ? " for a full phase"
                    : String.Format(" for {0:0} minute(s)", window.Value.TotalMinutes),
                station == null ? "" : " " + station);

            return true;
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
