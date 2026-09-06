using System;
using System.Collections.Generic;
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

                Timer.DelayCall(window, () =>
                {
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
                                "still not in a party {0:0.#}s after the invite (bot.Party is {1}). "
                                + "The DeclineTimer will refuse it at 30s.",
                                window.TotalSeconds,
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
                                "invite accepted within {0:0.#}s, well inside the 30s decline timer",
                                window.TotalSeconds));
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
        public static HealthResult Run(Mobile from)
        {
            BotCaps caps = BotSystem.Caps;
            var problems = new List<string>();
            var spawned = new List<PlayerBot>();

            Map map = from != null && from.Map != null && from.Map != Map.Internal ? from.Map : Map.Trammel;
            Point3D location = from != null ? from.Location : new Point3D(1475, 1645, 20);

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

            string summary = String.Format(
                "{0} class(es) audited at Grandmaster. {1}",
                spawned.Count,
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

            // Five travellers, deliberately made to contend, reporting through Bots.Travel. This
            // is what exercises rungs 2-5 of the recovery ladder; a lone walker recovers at rung 1
            // every time and leaves the rest unproven.
            BotWalkProbe.Run(map, location);

            // The lifecycle probe runs after the walk probe rather than alongside it: both spawn
            // bots that compete for the same arrival points, and a stuck reading from one would
            // be indistinguishable from contention caused by the other.
            TimeSpan afterWalk =
                BotWalkProbe.ConvergeWindow + BotWalkProbe.DisperseWindow + TimeSpan.FromSeconds(10.0);

            Timer.DelayCall(afterWalk, () => BotLifeProbe.Run(map, location));

            // Last, and alone. The chat probe spawns a PlayerMobile listener, and a player
            // standing in the middle of the walk or lifecycle probes would set every one of their
            // bots talking - harmless, but it makes three overlapping console logs unreadable.
            // Its own silence stage also needs the room genuinely empty to mean anything.
            TimeSpan afterLife = afterWalk + BotLifeProbe.Window + TimeSpan.FromSeconds(10.0);

            Timer.DelayCall(afterLife, () => BotChatProbe.Run(map, location));

            // The work probe goes last of all, and it is the only one that does not spawn at
            // `location`: its bots start at a mine face and a forge, which are the two places the
            // other probes never go. It still waits its turn, because a Miner walking a haul back
            // into town would collide with the chat probe's deliberately empty room.
            Timer.DelayCall(
                afterLife + BotChatProbe.SpeakWindow + BotChatProbe.SilenceWindow + TimeSpan.FromSeconds(10.0),
                () => BotWorkProbe.Run(map, location));

            return _last;
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
