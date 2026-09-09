// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotPopulationCommands.cs — the staff surface of the population layer.
//
//   [BotPopulationAudit   what the recipe would produce, spawning nothing
//   [BotPopulationGen     write GG_BotPop.xml from the recipe
//   [BotPopulation [n]    read or set the target, and refill
//   [BotSessions [on|off] the curve's status, or pin the population
//
// Upstream's equivalents are [GenerateBots, [SetBotPopulation, [ClearSpawners
// and [BotSessions. Two of those four are not ported and their absence is the
// point: [ClearSpawners exists to unstack accumulated spawner items, and
// [GenerateBots' clear-then-recreate exists for the same reason. Nothing here
// can stack, because the file is the record and a slot's UniqueId is derived
// from its identity rather than minted - so a regen is a rewrite, not a purge.

using System;
using System.Collections.Generic;

using Server.Commands;

namespace Server.Custom
{
    public static class BotPopulationCommands
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        public static void Initialize()
        {
            CommandSystem.Register("BotPopulationAudit", AccessLevel.Administrator, Audit_OnCommand);
            CommandSystem.Register("BotPopulationGen", AccessLevel.Administrator, Generate_OnCommand);
            CommandSystem.Register("BotPopulation", AccessLevel.Administrator, Population_OnCommand);
            CommandSystem.Register("BotSessions", AccessLevel.GameMaster, Sessions_OnCommand);
        }

        // -------------------------------------------------------------------

        [Usage("BotPopulationAudit")]
        [Description(
            "Reports what the population recipe would produce for each town, and whether "
            + "GG_BotPop.xml on disk still matches it. Spawns nothing and writes nothing.")]
        private static void Audit_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            Map map = BotPopulation.Facet;
            BotRecipe recipe = BotPopulation.Build(map);

            foreach (string line in BotPopulation.Describe(map, recipe))
            {
                from.SendMessage(line);
            }

            List<string> differences = BotPopulation.DiffAgainstFile(map, recipe);

            if (differences.Count == 0)
            {
                from.SendMessage(0x40, "  " + BotPopulation.GeneratedPath(map) + " matches the recipe.");
                return;
            }

            from.SendMessage(0x35, String.Format(
                "  REGEN NEEDED - {0} difference(s) between the recipe and {1}:",
                differences.Count,
                BotPopulation.GeneratedPath(map)));

            // Capped, because a first run against a missing file lists every slot and there is no
            // sense in scrolling forty lines off the top of somebody's client to say "run the
            // command".
            for (int i = 0; i < differences.Count && i < 12; i++)
            {
                from.SendMessage(0x35, "    " + differences[i]);
            }

            if (differences.Count > 12)
            {
                from.SendMessage(0x35, String.Format("    ... and {0} more.", differences.Count - 12));
            }

            from.SendMessage(0x35, "  Run [BotPopulationGen, then [GG_Reimport.");
        }

        // -------------------------------------------------------------------

        [Usage("BotPopulationGen")]
        [Description(
            "Writes the population recipe to Spawns/Custom/<facet>/GG_BotPop.xml. Run "
            + "[GG_Reimport afterwards to put it in the world. Hand-authored GG_Bots.xml is untouched.")]
        private static void Generate_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            Map map = BotPopulation.Facet;
            BotRecipe recipe = BotPopulation.Build(map);

            if (recipe.Slots.Count == 0)
            {
                from.SendMessage(0x35, "The recipe is empty - nothing was written.");

                foreach (string note in recipe.Notes)
                {
                    from.SendMessage(0x35, "  " + note);
                }

                return;
            }

            string error;

            if (!BotPopulation.Write(map, recipe, out error))
            {
                from.SendMessage(0x35, "Nothing was written: " + error);
                return;
            }

            from.SendMessage(0x40, String.Format(
                "Wrote {0}: {1} spawner(s) for {2} bot(s), {3} of them fixed.",
                BotPopulation.GeneratedPath(map),
                recipe.Slots.Count,
                recipe.TotalBots,
                recipe.FixedBots));

            foreach (string note in recipe.Notes)
            {
                from.SendMessage(0x35, "  note: " + note);
            }

            // Said rather than done. [GG_Reimport deletes every GG_ spawner in the world along
            // with its spawned mobiles, and doing that as a side effect of writing a file is the
            // kind of surprise the editor's own Resync button puts behind a confirmation.
            from.SendMessage("Now run [GG_Reimport to load it.");

            CommandLogging.WriteLine(
                from,
                String.Format(
                    "{0} {1} generating the bot population ({2} spawner(s), {3} bot(s))",
                    from.AccessLevel,
                    CommandLogging.Format(from),
                    recipe.Slots.Count,
                    recipe.TotalBots));
        }

        // -------------------------------------------------------------------

        [Usage("BotPopulation [count]")]
        [Description(
            "Reports the live population against the target and the tick cost, or sets the "
            + "target for this session. A new target needs [BotPopulationGen and [GG_Reimport to take effect.")]
        private static void Population_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            BotPopulationConfig config = BotSystem.Store.Population;

            if (e.Length >= 1)
            {
                int wanted = e.GetInt32(0);

                if (wanted < 0 || wanted > BotPopulationConfig.MaxTarget)
                {
                    from.SendMessage(0x35, String.Format(
                        "The target must be between 0 and {0}.", BotPopulationConfig.MaxTarget));
                    return;
                }

                int old = config.Target;
                config.Target = wanted;

                from.SendMessage(0x40, String.Format("Bot population target: {0} -> {1}.", old, wanted));

                // IN MEMORY ONLY, and said out loud. Upstream's [SetBotPopulation regenerates the
                // world's spawners on the spot (SetBotPopulationCommand.cs:61); here the file is
                // the record, so a target changed and not written back is a target that a
                // [BotsReload - or a restart - will quietly undo.
                from.SendMessage(
                    "This is in memory only. Put it in bots.json, then [BotPopulationGen and [GG_Reimport.");

                CommandLogging.WriteLine(
                    from,
                    String.Format(
                        "{0} {1} setting the bot population target to {2}",
                        from.AccessLevel,
                        CommandLogging.Format(from),
                        wanted));

                return;
            }

            int live = 0;
            int fixtures = 0;

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted)
                {
                    continue;
                }

                if (bot.LifecycleExempt)
                {
                    fixtures++;
                }
                else
                {
                    live++;
                }
            }

            from.SendMessage(String.Format(
                "{0} lifecycle bot(s) live against a curve target of {1}, plus {2} fixture(s).",
                live,
                BotSession.TargetNow,
                fixtures));

            from.SendMessage(BotSession.Describe());
            from.SendMessage(BotTickManager.DescribeCost());
            from.SendMessage(String.Format(
                "{0} bot spawner(s) in the world.", BotPopulation.LiveSpawners().Count));
        }

        // -------------------------------------------------------------------

        [Usage("BotSessions [on|off]")]
        [Description("Reports the session curve, or turns it off to pin the population where it is.")]
        private static void Sessions_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Arguments.Length > 0)
            {
                string argument = e.Arguments[0].ToLowerInvariant();

                if (argument == "on" || argument == "off")
                {
                    BotSession.Enabled = argument == "on";

                    from.SendMessage(0x40, BotSession.Describe());

                    CommandLogging.WriteLine(
                        from,
                        String.Format(
                            "{0} {1} turning bot sessions {2}",
                            from.AccessLevel,
                            CommandLogging.Format(from),
                            argument));

                    return;
                }

                from.SendMessage(0x35, "Usage: [BotSessions [on|off]");
                return;
            }

            from.SendMessage(BotSession.Describe());
        }
    }
}
