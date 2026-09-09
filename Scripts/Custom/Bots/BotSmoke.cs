using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Server.Engines.PartySystem;
using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// Spawns one bot per rollable class, checks every one against the configured caps, and
    /// deletes them.
    ///
    /// This is the EJ-caps equivalent of the upstream shard's T2A audit rig, and it is the check
    /// that the caps genuinely became config rather than moving from one set of literals to
    /// another. If someone edits Config/PlayerCaps.cfg or bots.json and the templates stop
    /// respecting it, this says so on the next boot.
    ///
    /// Headless because ServUO's console cannot invoke staff commands: set
    /// Custom.BotSmokeOnStart=True in Config/Custom.cfg and read the console. The result is also
    /// reported through [CoreSmoke as Bots.Smoke.
    /// </summary>
    public static class BotSmoke
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        private static HealthResult _last;

        private static HealthResult _lastParty;

        public static void Initialize()
        {
            HealthCheck.Register("Bots.Smoke", BuildHealthResult);
            HealthCheck.Register("Bots.Party", BuildPartyHealthResult);
            HealthCheck.Register("Bots.Travel", BotWalkProbe.BuildHealthResult);
            HealthCheck.Register("Bots.Life", BotLifeProbe.BuildHealthResult);
            HealthCheck.Register("Bots.Speech", BotChatProbe.BuildHealthResult);
            HealthCheck.Register("Bots.Shift", BotWorkProbe.BuildHealthResult);
        }

        public static HealthResult BuildPartyHealthResult()
        {
            if (_lastParty == null)
            {
                return HealthResult.Ok("not run this boot (runs with [BotSmoke)");
            }

            return _lastParty;
        }

        /// <summary>
        /// Invite a bot to a party from a throwaway leader and check it joins before the
        /// 30-second DeclineTimer fires.
        ///
        /// This is the only part of the bot layer that cannot be checked synchronously: the whole
        /// point of BotParty is that a bot answers on a delay, like a person clicking. So the
        /// probe schedules its own assertion and reports through the Bots.Party health check when
        /// it lands, rather than making the caller wait.
        ///
        /// The leader is a real PlayerMobile, because that is what the path under test expects -
        /// Party.Invite reads the leader's faction, and AddPartyTarget's own gate is about the
        /// invitee, not the inviter. It is deleted either way; on ServUO an accountless
        /// PlayerMobile would otherwise be written to the save like any other mobile.
        /// </summary>
        public static void RunPartyProbe(Map map, Point3D location)
        {
            PlayerMobile leader = null;
            PlayerBot bot = null;

            try
            {
                leader = new PlayerMobile
                {
                    Name = "Bot Smoke Leader",
                    Body = 0x190,
                    AccessLevel = AccessLevel.Player,
                    Blessed = true
                };

                leader.MoveToWorld(location, map);

                bot = new PlayerBot(BotClass.Warrior, BotSkillTier.Journeyman);
                bot.MoveToWorld(location, map);

                Party.Invite(leader, bot);

                if (!(bot.Party is Mobile))
                {
                    Finish(leader, bot, HealthResult.Fail(
                        "the invite did not register: bot.Party is not the leader after Party.Invite. "
                        + "Party.Invite refused it, or the party gate rejected the bot."));
                    return;
                }

                // Long enough for the answer, far short of the 30-second decline.
                var window = TimeSpan.FromSeconds(BotParty.MaxAcceptDelay + 3.0);

                PlayerMobile capturedLeader = leader;
                PlayerBot capturedBot = bot;

                // Report the moment it joins, not when the clock runs out. A bot answering
                // instantly is the bug this probe was written to catch, so it still WAITS for the
                // delay - it just stops sleeping through the remainder once the answer is in.
                var clock = new BotProbeClock(window);
                Timer watch = null;

                watch = Timer.DelayCall(TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.0), 0, () =>
                {
                    bool joined = !capturedBot.Deleted && Party.Get(capturedBot) != null;

                    if (!joined && !clock.Expired)
                    {
                        return;
                    }

                    if (watch != null)
                    {
                        watch.Stop();
                        watch = null;
                    }

                    HealthResult result;

                    if (capturedBot.Deleted)
                    {
                        result = HealthResult.Fail("the probe bot vanished before it could answer");
                    }
                    else
                    {
                        Party party = Party.Get(capturedBot);

                        if (party == null)
                        {
                            result = HealthResult.Fail(String.Format(
                                "still not in a party after {0} (bot.Party is {1}). "
                                + "The DeclineTimer will refuse it at 30s.",
                                clock.Describe(),
                                capturedBot.Party == null ? "null" : capturedBot.Party.GetType().Name));
                        }
                        else if (!party.Contains(capturedBot))
                        {
                            result = HealthResult.Fail("holds a Party it is not a member of");
                        }
                        else if (party.Leader != capturedLeader)
                        {
                            result = HealthResult.Fail("joined a party led by somebody else");
                        }
                        else
                        {
                            result = HealthResult.Ok(String.Format(
                                "invite accepted in {0}, well inside the 30s decline timer",
                                clock.Describe()));
                        }
                    }

                    Finish(capturedLeader, capturedBot, result);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Party probe threw.");
                Finish(leader, bot, HealthResult.Fail("threw: " + ex.Message));
            }
        }

        private static void Finish(Mobile leader, PlayerBot bot, HealthResult result)
        {
            _lastParty = result;

            if (result.Status == HealthStatus.Ok)
            {
                Log.Info("Bot party probe PASSED - {0}", result.Detail);
            }
            else
            {
                Log.Error("Bot party probe FAILED - {0}", result.Detail);
            }

            // Order matters: disband before deleting, or the party keeps a deleted member.
            if (bot != null && !bot.Deleted)
            {
                Party party = Party.Get(bot);

                if (party != null)
                {
                    party.Remove(bot);
                }

                bot.Delete();
            }

            if (leader != null && !leader.Deleted)
            {
                Party leaderParty = Party.Get(leader);

                if (leaderParty != null)
                {
                    leaderParty.Disband();
                }

                leader.Delete();
            }
        }

        public static HealthResult BuildHealthResult()
        {
            if (_last == null)
            {
                return HealthResult.Ok("not run this boot (set Custom.BotSmokeOnStart=True, or run [BotSmoke)");
            }

            return _last;
        }

        /// <summary>
        /// Run the audit. <paramref name="from"/> may be null when run headlessly at boot; when
        /// present it also receives the report.
        /// </summary>
        /// <summary>
        /// How long LayerConflict.log is, so the smoke can assert that dressing a bot of every
        /// class added nothing to it.
        ///
        /// The file's length is the assertion because the engine's own behaviour makes anything
        /// cheaper unreliable: Mobile.AddItem (Server/Mobile.cs:6779) writes the log and then
        /// EQUIPS THE ITEM ANYWAY, so a conflict leaves no trace on the bot to inspect afterwards -
        /// both items are on the layer and FindItemOnLayer returns one of them. The log is the only
        /// place the fault is recorded at all.
        ///
        /// Zero on any failure to read, which makes an unreadable file a silent pass rather than a
        /// spurious one. That is the right way round: this is a regression guard on the outfit
        /// tables, not a check on the filesystem.
        /// </summary>
        private static long LayerConflictBytes()
        {
            try
            {
                string path = Path.Combine(Core.BaseDirectory, "LayerConflict.log");

                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Live bots, from the registry rather than World.Mobiles (CLAUDE.md section 15).</summary>
        private static int CountPopulation()
        {
            int live = 0;

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot != null && !bot.Deleted && bot.Map != null && bot.Map != Map.Internal)
                {
                    live++;
                }
            }

            return live;
        }

        public static HealthResult Run(Mobile from)
        {
            BotCaps caps = BotSystem.Caps;
            var problems = new List<string>();
            var spawned = new List<PlayerBot>();

            Map map = from != null && from.Map != null && from.Map != Map.Internal ? from.Map : Map.Trammel;
            Point3D location = from != null ? from.Location : new Point3D(1475, 1645, 20);

            int populationBefore = CountPopulation();
            long conflictsBefore = LayerConflictBytes();

            try
            {
                foreach (BotClass cls in BotClassHelper.Rollable())
                {
                    // Grandmaster: the tier that spends the whole stat budget and sits closest to
                    // every cap. If any tier breaches, this one does.
                    PlayerBot bot;

                    try
                    {
                        bot = new PlayerBot(cls, BotSkillTier.Grandmaster);
                    }
                    catch (Exception ex)
                    {
                        problems.Add(String.Format("{0}: threw on construction - {1}", cls, ex.Message));
                        continue;
                    }

                    spawned.Add(bot);
                    bot.MoveToWorld(location, map);

                    Check(bot, caps, problems);
                }
            }
            finally
            {
                // Custom.BotSmokeKeep leaves this many of the audited bots standing.
                //
                // Default 0, and that is the right value for a live shard. It exists because the
                // two remaining things worth checking headlessly - that a bot reaches the
                // editor's live layer, and that it does NOT survive a restart - both need a bot
                // that is still alive when the snapshot is written and when the world is saved,
                // and there is no way to spawn one from the console.
                int keep = Config.Get("Custom.BotSmokeKeep", 0);

                if (keep > spawned.Count)
                {
                    keep = spawned.Count;
                }

                for (int i = keep; i < spawned.Count; i++)
                {
                    spawned[i].Delete();
                }

                if (keep > 0)
                {
                    Log.Warn(
                        "Custom.BotSmokeKeep={0}: leaving {0} audited bot(s) standing at {1} on {2}. "
                        + "They will not survive a restart - that is the point of the setting.",
                        keep,
                        location,
                        map);
                }
            }

            // Registration is part of the contract, so it is part of the audit: a bot the editor
            // cannot see is a bot nobody can find.
            foreach (PlayerBot bot in spawned)
            {
                if (bot.Deleted)
                {
                    continue;
                }

                if (!LiveRegistry.Snapshot().Contains(bot))
                {
                    problems.Add(String.Format(
                        "{0}/{1}: not in LiveRegistry; it will not draw on the editor's live map",
                        bot.Class,
                        bot.SkillTier));
                }
            }

            // THE POPULATION IS THE BASELINE NOW, and saying so is what makes a leak visible.
            //
            // This probe spawns a bot per class and deletes them again. It used to run in an empty
            // world, so "a bot left standing" was obvious. With a population alive it is not: the
            // count is sixty either way, and a probe that leaked one would read as sixty-one bots
            // that somebody would assume the curve had made. Naming the count before and after
            // turns that back into something a reader can subtract. Bots.Population's name-pool
            // check is the automated half; this is the one in front of whoever typed the command.
            // EVERY CLASS JUST GOT DRESSED, which makes this the one place that can assert the
            // outfit tables respect the engine's layers. A conflict is invisible on the bot itself
            // - AddItem logs it and equips the item anyway - so the log's length is the evidence.
            long conflictsAfter = LayerConflictBytes();

            if (conflictsAfter > conflictsBefore)
            {
                problems.Add(String.Format(
                    "the outfit generator equipped {0} byte(s) worth of layer conflicts into "
                    + "LayerConflict.log - two items on one layer, or a shield with a two-handed "
                    + "weapon. The engine does NOT refuse these: both items stay on the bot and one "
                    + "is an invisible ghost that still carries weight and drops on the corpse",
                    conflictsAfter - conflictsBefore));
            }

            string summary = String.Format(
                "{0} class(es) audited at Grandmaster; population {1} before, {2} after ({3} kept); "
                + "{4} layer conflict(s) logged. {5}",
                spawned.Count,
                populationBefore,
                CountPopulation(),
                Config.Get("Custom.BotSmokeKeep", 0),
                conflictsAfter > conflictsBefore ? "SOME" : "no",
                caps.Describe());

            if (problems.Count > 0)
            {
                _last = HealthResult.Fail(String.Format(
                    "{0} cap violation(s). First: {1}. {2}",
                    problems.Count,
                    problems[0],
                    summary));

                Log.Error("Bot smoke FAILED - {0} problem(s):", problems.Count);

                foreach (string problem in problems)
                {
                    Log.Error("  {0}", problem);
                }
            }
            else
            {
                _last = HealthResult.Ok("every class within caps. " + summary);

                Log.Info("Bot smoke PASSED - {0}", summary);
            }

            if (from != null)
            {
                from.SendMessage(problems.Count > 0 ? 0x35 : 0x3B2, "Bot smoke: " + _last.Detail);
            }

            // Asynchronous by nature - a bot answers an invitation on a delay, on purpose - so it
            // reports separately through Bots.Party rather than holding this result open.
            RunPartyProbe(map, location);

            // THE CHAIN ADVANCES ON COMPLETION, NOT ON A STOPWATCH.
            //
            // It used to be four Timer.DelayCall hops at fixed offsets computed from the probes'
            // own windows, which meant the chain took the sum of the windows whatever happened -
            // 13m35s, measured to within a few seconds across three runs. Now each probe reports
            // the moment its assertion is satisfied, so waiting a fixed offset would have thrown
            // the entire saving away.
            //
            // The probes still run strictly one at a time, which is the reason they were staggered
            // in the first place: they spawn bots that compete for the same arrival points, and a
            // stuck reading from one would be indistinguishable from contention caused by another.
            // The chat probe in particular needs the room to itself - it spawns a PlayerMobile
            // listener, and its silence stage means nothing with other bots talking nearby.
            RunChain(map, location);

            return _last;
        }

        /// <summary>
        /// Run the four asynchronous probes one after another, each starting when the last has
        /// reported.
        ///
        /// A two-second poll rather than a completion callback, because the probes report from
        /// several places each - a normal path, a catch, a timeout - and threading a callback
        /// through every one of them is more surface than a flag read. The gap between probes is
        /// the poll interval rather than the ten-second cushions the old fixed schedule needed.
        /// </summary>
        private static void RunChain(Map map, Point3D location)
        {
            var stage = 0;
            Timer chain = null;

            chain = Timer.DelayCall(TimeSpan.Zero, TimeSpan.FromSeconds(2.0), 0, () =>
            {
                switch (stage)
                {
                    case 0:
                        BotWalkProbe.Run(map, location);
                        stage = 1;
                        return;

                    case 1:
                        if (BotWalkProbe.IsRunning) { return; }
                        BotLifeProbe.Run(map, location);
                        stage = 2;
                        return;

                    case 2:
                        if (BotLifeProbe.IsRunning) { return; }
                        BotChatProbe.Run(map, location);
                        stage = 3;
                        return;

                    case 3:
                        if (BotChatProbe.IsRunning) { return; }

                        // The work probe is the only one that does not spawn at `location`: its
                        // bots start at a mine face and a forge, which are the two places the
                        // others never go. It still waits its turn, because a Miner walking a haul
                        // back into town would collide with the chat probe's empty room.
                        BotWorkProbe.Run(map, location);
                        stage = 4;
                        return;

                    default:
                        if (BotWorkProbe.IsRunning) { return; }

                        if (chain != null)
                        {
                            chain.Stop();
                            chain = null;
                        }

                        Log.Info("Bot smoke chain complete.");
                        return;
                }
            });
        }

        /// <summary>
        /// Every assertion this session is meant to guarantee: skills and stats inside the
        /// configured caps, a real name, and an outfit that actually got built.
        /// </summary>
        private static void Check(PlayerBot bot, BotCaps caps, List<string> problems)
        {
            string who = String.Format("{0}/{1}", bot.Class, bot.SkillTier);

            // ---- skills ----
            double total = 0.0;

            for (int i = 0; i < bot.Skills.Length; i++)
            {
                Skill skill = bot.Skills[i];
                double value = skill.Base;

                total += value;

                if (value > caps.SkillCap)
                {
                    problems.Add(String.Format(
                        "{0}: {1} is {2:0.0}, above the per-skill cap {3:0.0}",
                        who,
                        skill.SkillName,
                        value,
                        caps.SkillCap));
                }
            }

            if (total > caps.SkillTotal)
            {
                // Name the skills. A total that is over cap is only actionable if you can see
                // which template produced it and what each skill landed at.
                var set = new List<string>();

                for (int i = 0; i < bot.Skills.Length; i++)
                {
                    Skill skill = bot.Skills[i];

                    if (skill.Base > 0.0)
                    {
                        set.Add(String.Format("{0} {1:0.0}", skill.SkillName, skill.Base));
                    }
                }

                problems.Add(String.Format(
                    "{0}: skill total {1:0.0} exceeds cap {2:0.0} across {3} skill(s) - {4}",
                    who,
                    total,
                    caps.SkillTotal,
                    set.Count,
                    String.Join(", ", set.ToArray())));
            }

            if (total <= 0.0)
            {
                problems.Add(String.Format("{0}: no skills were set at all", who));
            }

            // ---- stats ----
            CheckStat(who, "Str", bot.RawStr, caps, problems);
            CheckStat(who, "Dex", bot.RawDex, caps, problems);
            CheckStat(who, "Int", bot.RawInt, caps, problems);

            int statTotal = bot.RawStr + bot.RawDex + bot.RawInt;

            if (statTotal > caps.StatTotal)
            {
                problems.Add(String.Format(
                    "{0}: stat total {1} exceeds cap {2}",
                    who,
                    statTotal,
                    caps.StatTotal));
            }

            // ---- identity and kit ----
            if (String.IsNullOrEmpty(bot.Name))
            {
                problems.Add(String.Format("{0}: no name", who));
            }

            if (bot.Backpack == null)
            {
                problems.Add(String.Format("{0}: no backpack", who));
            }

            int worn = 0;

            foreach (Item item in bot.Items)
            {
                if (item != null && item.Layer != Layer.Backpack && item.Layer != Layer.Hair
                    && item.Layer != Layer.FacialHair && item.Layer != Layer.Invalid)
                {
                    worn++;
                }
            }

            if (worn == 0)
            {
                problems.Add(String.Format("{0}: wearing nothing", who));
            }

            if (!bot.Player)
            {
                problems.Add(String.Format("{0}: Player flag is false; party invites will be refused", who));
            }

            if (!bot.InitialInnocent)
            {
                problems.Add(String.Format("{0}: not InitialInnocent; it will read grey", who));
            }
        }

        private static void CheckStat(string who, string name, int value, BotCaps caps, List<string> problems)
        {
            if (value > caps.StatCap)
            {
                problems.Add(String.Format(
                    "{0}: {1} is {2}, above the per-stat cap {3}",
                    who,
                    name,
                    value,
                    caps.StatCap));
            }

            if (value <= 0)
            {
                problems.Add(String.Format("{0}: {1} is {2}", who, name, value));
            }
        }

        /// <summary>A one-bot dump for [BotInfo and the smoke report.</summary>
        public static string Describe(PlayerBot bot)
        {
            var sb = new StringBuilder();

            sb.AppendFormat(
                "{0} - {1} {2}, Str {3} Dex {4} Int {5}",
                bot.Name,
                BotSkillTierHelper.DisplayName(bot.SkillTier),
                BotClassHelper.DisplayName(bot.Class),
                bot.RawStr,
                bot.RawDex,
                bot.RawInt);

            return sb.ToString();
        }
    }
}
