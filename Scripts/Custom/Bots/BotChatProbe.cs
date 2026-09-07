// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotChatProbe.cs — one bot, one listener, and four things that must be true.
//
//   1. every token in the corpus is one this build knows about
//   2. every category this session speaks from is whole
//   3. with somebody in earshot, a bank sitter says something, and what it says
//      has no token left in it
//   4. with nobody in earshot, it says nothing at all
//
// Plus a fifth that is not about speech at all - see stage 0 and the sentinel:
// a bot's Say must never be delivered to another mobile's OnSpeech. That one is
// true structurally today, and it is asserted precisely BECAUSE it is: it is the
// kind of invariant a later refactor breaks without noticing, and the blast
// radius is every "withdraw 1000" in bank_actions turning into a live command.
//
// 3 and 4 are the pair. Either alone is easy and neither alone means anything:
// a bot that always talks passes 3, a bot that never talks passes 4, and only a
// bot that does both is honouring the audience gate that keeps a thousand of
// them from chattering at an empty world.
//
// Asynchronous by nature, so it reports through Bots.Speech when it lands rather
// than making the caller wait - the same reason Bots.Party is separate from
// Bots.Smoke.

using System;
using System.Collections.Generic;

using Server.Mobiles;
using Server.Network;

namespace Server.Custom
{
    public static class BotChatProbe
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// How long to wait for a line.
        ///
        /// A Regular bank sitter rolls at 0.25 on a 2-second tick with no cooldown to serve, so
        /// the expected wait is about eight seconds and the chance of thirty consecutive misses is
        /// roughly two in ten thousand. Generous on purpose: a probe that fails once a fortnight
        /// for no reason is a probe people learn to ignore.
        /// </summary>
        public static readonly TimeSpan SpeakWindow = TimeSpan.FromSeconds(60.0);

        /// <summary>How long the bot must then stay quiet with nobody listening.</summary>
        public static readonly TimeSpan SilenceWindow = TimeSpan.FromSeconds(30.0);

        private static HealthResult _last;

        private static BotProbeClock _clock;
        private static Timer _watch;

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.0);
        private static bool _running;

        /// <summary>True while this probe is mid-run. Read by [BotSmoke to advance the chain.</summary>
        public static bool IsRunning
        {
            get { return _running; }
        }

        public static HealthResult BuildHealthResult()
        {
            if (_last == null)
            {
                return HealthResult.Ok("not run this boot (runs with [BotSmoke)");
            }

            return _last;
        }

        public static void Run(Map map, Point3D location)
        {
            if (_running)
            {
                Log.Warn("Chat probe already running; ignoring the second request.");
                return;
            }

            _running = true;

            try
            {
                // ---- stage 0: the static assertions, before anything is spawned ----

                List<string> problems = CheckCorpus();

                problems.AddRange(CheckSpeechIsNotACommand(map, location));

                if (problems.Count > 0)
                {
                    Finish(null, null, HealthResult.Fail(String.Format(
                        "{0} corpus problem(s). First: {1}",
                        problems.Count,
                        problems[0])));

                    foreach (string problem in problems)
                    {
                        Log.Error("  {0}", problem);
                    }

                    return;
                }

                // ---- stage 1: with an audience, it talks ----

                var bot = new PlayerBot();
                bot.MoveToWorld(location, map);

                // Force the role rather than roll it. Fifteen percent of sitters are Afk, whose
                // entire job is silence - a probe that fails one run in seven by design is worse
                // than no probe. ForceRole must precede the assignment: OnAttached is what rolls.
                var sitter = new BankSitterBehavior();
                sitter.ForceRole(BankSitterBehavior.BankRole.Regular);
                bot.SetBehavior(sitter, "probe setup");

                // The audience. A throwaway PlayerMobile, the same device the party probe uses -
                // and the reason IsPlayerNearby asks the sector rather than NetState.Instances,
                // since this one has no client behind it.
                var listener = new PlayerMobile();
                listener.Name = "chat probe listener";
                listener.AccessLevel = AccessLevel.Player;
                listener.Blessed = true;
                listener.Hidden = true;
                listener.MoveToWorld(location, map);

                Log.Info(
                    "Chat probe: {0} listening to {1} for {2:0}s.",
                    listener.Name,
                    bot.Name,
                    SpeakWindow.TotalSeconds);

                // The SPEAK stage ends the moment a line is heard - that is the whole assertion.
                // The SILENCE stage after it keeps its full window, because proving a bot stayed
                // quiet is an absence and cannot be established early.
                _clock = new BotProbeClock(SpeakWindow + SilenceWindow);

                _watch = Timer.DelayCall(PollInterval, PollInterval, 0, () =>
                {
                    if (bot != null && !bot.Deleted && bot.SpeechLines == 0
                        && _clock.Elapsed < SpeakWindow)
                    {
                        return;
                    }

                    if (_watch != null)
                    {
                        _watch.Stop();
                        _watch = null;
                    }

                    CheckSpoke(bot, listener);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Chat probe threw during setup.");
                Finish(null, null, HealthResult.Fail("threw during setup: " + ex.Message));
            }
        }

        // ---- stage 0a: the corpus ----

        /// <summary>
        /// The honest form of "every token resolves".
        ///
        /// It cannot mean "every line in all 145 files is speakable" - most of the corpus belongs
        /// to sessions that have not landed, and asserting that would ship a permanently red
        /// smoke. It means two things that ARE true and worth defending: nothing in the corpus
        /// uses a token this build has never heard of, and nothing in the categories a bot
        /// actually draws from was dropped on the way in.
        /// </summary>
        private static List<string> CheckCorpus()
        {
            var problems = new List<string>();

            if (!ChatLibrary.Loaded || ChatLibrary.LastError != null)
            {
                problems.Add("corpus did not load: " + (ChatLibrary.LastError ?? "never attempted"));
                return problems;
            }

            if (ChatLibrary.WiredLines == 0)
            {
                problems.Add("corpus loaded but no line is speakable");
                return problems;
            }

            // A token nobody registered is a typo, and a typo is a line that can never be said.
            foreach (string unknown in ChatLibrary.UnknownTokens)
            {
                problems.Add("unrecognised token " + unknown);
            }

            foreach (string line in ChatLibrary.RejectedEmotes)
            {
                problems.Add("line starts with '*' and cannot be spoken: " + line);
            }

            for (int i = 0; i < ChatLibrary.SessionCategories.Length; i++)
            {
                string category = ChatLibrary.SessionCategories[i];

                if (ChatLibrary.LineCount(category) == 0)
                {
                    problems.Add(String.Format(
                        "category '{0}' is spoken from but loaded no lines", category));

                    continue;
                }

                int dropped = ChatLibrary.DroppedIn(category);

                if (dropped > 0)
                {
                    problems.Add(String.Format(
                        "category '{0}' is spoken from but {1} of its line(s) were dropped as "
                        + "reserved or unknown - a wired pool must be whole",
                        category,
                        dropped));
                }
            }

            return problems;
        }

        // ---- stage 0b: speech is not a command ----

        /// <summary>
        /// A bot's Say must not reach any other mobile's OnSpeech. THE DELIBERATE-FAILURE CHECK.
        ///
        /// bank_actions has bots saying "bank", "withdraw 1000" and "balance" out loud, and a bot
        /// carries Player = true for the party gate - so the obvious worry is a real Banker
        /// treating those as commands aimed at it.
        ///
        /// It cannot today, and the reason is structural rather than anything this layer arranges.
        /// Mobile.Say goes to PublicOverheadMessage (Mobile.cs:7236-7239), which only sends
        /// packets; the listener pipeline hangs off Mobile.DoSpeech, and DoSpeech is invoked from
        /// exactly two places, both real client speech packets (PacketHandlers.cs:1547, :1628).
        ///
        /// Which is precisely why it is worth asserting. Nothing in this folder holds that
        /// invariant up, so nothing in this folder would notice it going: the day somebody routes
        /// bot speech through DoSpeech to make bots hear each other, every bank_actions line
        /// becomes a live command in the same commit. The fix then is to suppress the dispatch for
        /// bot-originated speech - never to strip the lines out of the corpus.
        /// </summary>
        private static List<string> CheckSpeechIsNotACommand(Map map, Point3D location)
        {
            var problems = new List<string>();

            SpeechSentinel sentinel = null;
            PlayerBot speaker = null;

            try
            {
                sentinel = new SpeechSentinel();
                sentinel.MoveToWorld(location, map);

                speaker = new PlayerBot();
                speaker.MoveToWorld(location, map);

                // Standing on the same tile, and the sentinel answers HandlesOnSpeech with an
                // unconditional true, so nothing but the dispatch itself can keep it quiet.
                speaker.Say("withdraw 1000");

                if (sentinel.Heard)
                {
                    problems.Add(
                        "a bot's Say reached another mobile's OnSpeech - bot speech is being "
                        + "dispatched as real speech, so every bank_actions line is now a live "
                        + "command. Suppress the dispatch for bot speech; do not edit the corpus");
                }
            }
            catch (Exception ex)
            {
                problems.Add("the speech-is-not-a-command check threw: " + ex.Message);
            }
            finally
            {
                if (sentinel != null && !sentinel.Deleted)
                {
                    sentinel.Delete();
                }

                if (speaker != null && !speaker.Deleted)
                {
                    speaker.Delete();
                }
            }

            return problems;
        }

        // ---- stage 1: it talks ----

        private static void CheckSpoke(PlayerBot bot, PlayerMobile listener)
        {
            try
            {
                if (bot.Deleted)
                {
                    Finish(bot, listener, HealthResult.Fail("the probe bot was deleted mid-run"));
                    return;
                }

                if (bot.SpeechLines == 0)
                {
                    Finish(bot, listener, HealthResult.Fail(String.Format(
                        "said nothing in {0:0}s with a listener on its tile. Role {1}, {2} "
                        + "categor(ies), {3} line(s) available",
                        SpeakWindow.TotalSeconds,
                        DescribeRole(bot),
                        DescribeCategories(bot),
                        ChatLibrary.WiredLines)));

                    return;
                }

                string spoken = ChatLibrary.LastSpokenLine;

                // The invariant the whole token registry exists to protect.
                if (spoken != null && spoken.IndexOf('{') >= 0)
                {
                    Finish(bot, listener, HealthResult.Fail(
                        "a spoken line still contained a token: \"" + spoken + "\""));

                    return;
                }

                Log.Info(
                    "Chat probe: {0} said {1} line(s); last was \"{2}\".",
                    bot.Name,
                    bot.SpeechLines,
                    spoken);

                // ---- stage 2: take the audience away ----
                //
                // Deleting rather than moving: a listener walked out of range is still a
                // PlayerMobile somewhere on the facet, and this shard's sector query is cheap
                // enough that "somewhere" is not obviously far enough.
                listener.Delete();

                int spokenSoFar = bot.SpeechLines;

                Timer.DelayCall(SilenceWindow, () => CheckSilent(bot, spokenSoFar));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Chat probe threw while checking for speech.");
                Finish(bot, listener, HealthResult.Fail("threw while checking speech: " + ex.Message));
            }
        }

        // ---- stage 2: it stops ----

        private static void CheckSilent(PlayerBot bot, int spokenBefore)
        {
            try
            {
                if (bot.Deleted)
                {
                    Finish(bot, null, HealthResult.Fail("the probe bot was deleted mid-run"));
                    return;
                }

                int since = bot.SpeechLines - spokenBefore;

                // The speak stage ends as soon as a line is heard, so quoting SpeakWindow here
                // would claim a minute the probe did not spend. The silence stage always runs its
                // full window - absence needs it - so that one is quoted as configured.
                string summary = String.Format(
                    "in {0} - {1} line(s) with a listener, then {2} in the {3:0}s silence stage. "
                    + "corpus {4} wired / {5} reserved / {6} unknown",
                    _clock == null ? "?" : _clock.Describe(),
                    spokenBefore,
                    since,
                    SilenceWindow.TotalSeconds,
                    ChatLibrary.WiredLines,
                    ChatLibrary.ReservedLines,
                    ChatLibrary.UnknownLines);

                if (since > 0)
                {
                    // A real player standing next to the probe is a legitimate reason for this,
                    // and a staff member running [BotSmoke in-game is standing right there. Warn
                    // rather than fail, and say which it was, so a genuine gate failure is not
                    // written off as the observer effect.
                    if (RealPlayerNear(bot))
                    {
                        Finish(bot, null, HealthResult.Warn(
                            "kept talking after the probe listener left, but a real player is "
                            + "within earshot - inconclusive. Re-run headless with "
                            + "Custom.BotSmokeOnStart. " + summary));

                        return;
                    }

                    Finish(bot, null, HealthResult.Fail(
                        "kept talking to an empty room - the audience gate is not holding, and "
                        + "every bot on the shard is now chattering whether or not anyone is "
                        + "there. " + summary));

                    return;
                }

                Finish(bot, null, HealthResult.Ok(summary));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Chat probe threw while checking for silence.");
                Finish(bot, null, HealthResult.Fail("threw while checking silence: " + ex.Message));
            }
        }

        /// <summary>
        /// Is a real client near the bot?
        ///
        /// NetState.Instances here, unlike the audience gate: this asks specifically about REAL
        /// players, and the short online list is exactly that (CLAUDE.md section 15).
        /// </summary>
        private static bool RealPlayerNear(PlayerBot bot)
        {
            foreach (NetState state in NetState.Instances)
            {
                Mobile player = state.Mobile;

                if (player == null || player.Deleted || player.Map != bot.Map)
                {
                    continue;
                }

                if (player.InRange(bot.Location, 22))
                {
                    return true;
                }
            }

            return false;
        }

        private static string DescribeRole(PlayerBot bot)
        {
            var sitter = bot.Behavior as BankSitterBehavior;

            return sitter == null
                ? (bot.Behavior == null ? "none" : bot.Behavior.SerializableName)
                : sitter.Role.ToString();
        }

        private static string DescribeCategories(PlayerBot bot)
        {
            PlayerBotBehavior behaviour = bot.Behavior;

            return behaviour == null || behaviour.ChatCategories == null
                ? "0"
                : behaviour.ChatCategories.Length.ToString();
        }

        private static void StopWatch()
        {
            if (_watch != null)
            {
                _watch.Stop();
                _watch = null;
            }
        }

        private static void Finish(PlayerBot bot, PlayerMobile listener, HealthResult result)
        {
            _last = result;
            _running = false;

            if (result.Status == HealthStatus.Ok)
            {
                Log.Info("Bot chat probe PASSED - {0}", result.Detail);
            }
            else if (result.Status == HealthStatus.Warn)
            {
                Log.Warn("Bot chat probe - {0}", result.Detail);
            }
            else
            {
                Log.Error("Bot chat probe FAILED - {0}", result.Detail);
            }

            if (bot != null && !bot.Deleted)
            {
                bot.Delete();
            }

            if (listener != null && !listener.Deleted)
            {
                listener.Delete();
            }
        }

        /// <summary>
        /// A mobile that hears absolutely everything and remembers that it did.
        ///
        /// Exists only for CheckSpeechIsNotACommand. HandlesOnSpeech is unconditionally true, so
        /// if a bot's Say were ever dispatched as real speech this would catch it with no
        /// dependence on a Banker's keyword list, its range, or what it does about it.
        ///
        /// Ephemeral like every bot: deleted at the end of the check, and deleted again on load if
        /// a save somehow caught one standing (ServUO has no [AfterDeserialization]).
        /// </summary>
        private class SpeechSentinel : BaseCreature
        {
            public bool Heard { get; private set; }

            public SpeechSentinel()
                : base(AIType.AI_Animal, FightMode.None, 10, 1, 0.2, 0.4)
            {
                Body = 0x190;
                Name = "speech sentinel";
                Blessed = true;
                Hidden = true;
            }

            public SpeechSentinel(Serial serial)
                : base(serial)
            {
            }

            public override bool HandlesOnSpeech(Mobile from)
            {
                return true;
            }

            public override void OnSpeech(SpeechEventArgs e)
            {
                Heard = true;

                base.OnSpeech(e);
            }

            public override void Serialize(GenericWriter writer)
            {
                base.Serialize(writer);

                writer.Write(0); // version
            }

            public override void Deserialize(GenericReader reader)
            {
                base.Deserialize(reader);

                reader.ReadInt();

                Timer.DelayCall(Delete);
            }
        }
    }
}
