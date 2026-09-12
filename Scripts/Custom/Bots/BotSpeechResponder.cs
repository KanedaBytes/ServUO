// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotSpeechResponder.cs — bots answer when a real player talks to them.
//
// Upstream's framing of why this file exists is exactly right and worth keeping
// with the code: "The single loudest 'that's an NPC' tell is a 'player' who
// ignores you." Everything else in this layer - the names, the outfits, the
// walking, the bank crowd - is undone the first time somebody says hello and
// gets nothing back.
//
// The response surface is deliberately minimal:
//
//   say a bot's NAME nearby   -> it turns and answers ("yeah?")
//   greet within earshot      -> the CLOSEST bot greets back ("sup")
//   ask a question close by   -> a shrug ("dunno", "no idea m8")
//   say anything in its face  -> sometimes "what", sometimes nothing, because
//                                sometimes nothing is what a real player did
//
// HARD RULES, all upstream's, all kept:
//   - only real players trigger it, so there is no bot-to-bot echo
//   - one reply per utterance, so a "hi" turns one head and not the whole bank
//   - per-bot cooldowns, so nobody can farm chatter out of a crowd
//   - the AFK bank crowd stays silent, because silence IS their answer
//   - replies come after a typing delay, never instantly
//
// WHY OnSpeech AND NOT OnThink. The brief for this session suggested hanging
// this off the OnThink path, the way the party check is. That is right for an
// INVITATION, which is state sitting there to be noticed. Speech is an event:
// ServUO delivers it through Mobile.DoSpeech -> HandlesOnSpeech -> OnSpeech, and
// that path fires only when somebody actually speaks near the bot - a tighter
// gate than OnThink's sector test, not a looser one, and one that cannot miss an
// utterance by being between ticks. PlayerBot.HandlesOnSpeech is what opens it
// past the two-tile default perception.

using System;
using System.Collections.Generic;

using Server.Mobiles;

namespace Server.Custom
{
    public static class BotSpeechResponder
    {
        /// <summary>
        /// Your name carries across a room; "hi" only lands close by; and talking right in
        /// somebody's face is its own thing. Upstream's three ranges, unchanged.
        /// </summary>
        public const int NameRange = 10;

        private const int GreetRange = 5;

        private const int CloseRange = 2;

        /// <summary>
        /// What PlayerBot.HandlesOnSpeech opens the ear to. The widest branch above, so no branch
        /// is unreachable, and no wider, because a bot has no business hearing what it will not
        /// answer.
        /// </summary>
        public const int ListenRange = NameRange;

        /// <summary>
        /// After answering, a bot is quiet for this long. Long on purpose: a bot that answers
        /// every third word is as unconvincing as one that never answers at all.
        /// </summary>
        private const int MinReplyCooldownSeconds = 45;

        private const int MaxReplyCooldownSeconds = 120;

        /// <summary>
        /// Name mentions cut through the general cooldown - you answer to your NAME even if you
        /// just spoke - but keep their own short guard, so "thorgil thorgil thorgil" cannot farm
        /// chatter out of one bot.
        /// </summary>
        private static readonly TimeSpan NameReplyGuard = TimeSpan.FromSeconds(15.0);

        /// <summary>How long a claim on an utterance stands. See Claimed.</summary>
        private static readonly TimeSpan ClaimWindow = TimeSpan.FromSeconds(2.0);

        private static readonly Dictionary<Serial, DateTime> _cooldowns = new Dictionary<Serial, DateTime>();

        private static readonly Dictionary<Serial, DateTime> _lastReplyAt = new Dictionary<Serial, DateTime>();

        // ---- the claim ----
        //
        // One reply per utterance. Every listener's OnSpeech runs in the same pass, so distance
        // checks alone cannot stop a chorus: ties and same-pass cooldown writes poison any "am I
        // the closest?" logic. So the first bot that decides to answer CLAIMS the utterance and
        // the rest let it stand. A name mention overrides a generic claim - "hey Tobias" belongs
        // to Tobias no matter who spoke up first.

        private static Serial _claimSpeaker;
        private static string _claimText;
        private static DateTime _claimAt;

        /// <summary>Replies actually made since boot, for the health line.</summary>
        public static int Replies { get; private set; }

        public static void OnSpeech(PlayerBot bot, SpeechEventArgs e)
        {
            if (bot == null || e == null || e.Mobile == null)
            {
                return;
            }

            Mobile speaker = e.Mobile;

            // Real players only - and the second half of that test is no longer free.
            //
            // This comment used to say a PlayerBot is a BaseCreature and so can never be a
            // PlayerMobile, which closed upstream's echo chamber "by construction". It does
            // not any more: a bot IS a PlayerMobile. Without the PlayerBot test a bot's own
            // ambient chatter would be delivered to every bot in earshot as though a player
            // had spoken to it, each answer triggering the next - which is precisely the echo
            // chamber upstream guards against with the same two tests (PlayerBot.cs:851).
            if (!(speaker is PlayerMobile) || speaker is PlayerBot || speaker.Deleted)
            {
                return;
            }

            if (bot.Deleted || !bot.Alive || bot.Hidden || bot.Combatant != null
                || bot.Map == null || bot.Map == Map.Internal || bot.Map != speaker.Map
                || String.IsNullOrWhiteSpace(e.Speech))
            {
                return;
            }

            // The bank's AFK crowd is away from the keyboard. Silence is their answer, and it is
            // the most period-accurate thing in this file.
            if (IsAway(bot))
            {
                return;
            }

            // DoSpeech broadcasts to fifteen tiles, so the range must be re-checked here rather
            // than trusted from HandlesOnSpeech.
            int distance = Chebyshev(bot.Location, speaker.Location);

            if (distance > NameRange)
            {
                return;
            }

            string lower = e.Speech.ToLowerInvariant().Trim();

            // A local rather than an inline call: FirstName(null) is null, and OnSpeech runs for
            // every listener of every utterance - a throw here would take out the whole room's
            // dispatch, not just leave one bot quiet.
            string firstName = ChatTokens.FirstName(bot.Name);

            // 1. My name. Always gets a turn and an answer - even if somebody else already piped
            //    up, and even mid-cooldown, since its own guard applies instead.
            if (!String.IsNullOrEmpty(firstName) && ContainsWord(lower, firstName.ToLowerInvariant()))
            {
                DateTime last;

                if (!_lastReplyAt.TryGetValue(bot.Serial, out last) || CustomTime.Now - last >= NameReplyGuard)
                {
                    Claim(speaker, lower);
                    Reply(bot, speaker, ChatLibrary.RespondName);
                }

                return;
            }

            // Past this point somebody else may already have taken the utterance.
            if (Claimed(speaker, lower) || OnCooldown(bot))
            {
                return;
            }

            // 2. A greeting, close by.
            if (distance <= GreetRange && IsGreeting(lower))
            {
                if (ClosestEligible(bot, speaker, distance))
                {
                    Claim(speaker, lower);

                    // A "hi" at the bank going unanswered is period-accurate too.
                    if (Utility.RandomDouble() < 0.65)
                    {
                        Reply(bot, speaker, ChatLibrary.RespondGreet);
                    }
                }

                return;
            }

            // 3. A question. Bots are regular players, not tour guides - mostly shrugs.
            if (distance <= GreetRange && LooksLikeQuestion(lower))
            {
                if (ClosestEligible(bot, speaker, distance))
                {
                    Claim(speaker, lower);

                    if (Utility.RandomDouble() < 0.75)
                    {
                        Reply(bot, speaker, ChatLibrary.RespondQuestion);
                    }
                }

                return;
            }

            // 4. Something else, said right in my face. Sometimes "what", sometimes pointedly
            //    nothing - both are human, and the silence is not an oversight.
            if (distance <= CloseRange && ClosestEligible(bot, speaker, distance))
            {
                Claim(speaker, lower);

                if (Utility.RandomDouble() < 0.45)
                {
                    Reply(bot, speaker, ChatLibrary.RespondWhat);
                }
                else
                {
                    SetCooldown(bot, TimeSpan.FromSeconds(20.0));
                }
            }
        }

        /// <summary>
        /// Face the speaker, then answer after a human typing delay.
        ///
        /// The delay is the single most important line in this file. Upstream, in BotShopTalk:
        /// "an instant answer is the loudest tell there is." Roughly 0.9 to 3.3 seconds, scaled by
        /// how much there was to type and capped so a long line does not stall.
        /// </summary>
        private static void Reply(PlayerBot bot, Mobile speaker, string category)
        {
            string template = ChatLibrary.PickRandom(category);

            if (String.IsNullOrEmpty(template))
            {
                return;
            }

            var context = new ChatTokenContext
            {
                Bot = bot,
                Other = speaker,
                DestinationId = bot.Behavior == null ? null : bot.Behavior.CurrentDestinationId,
            };

            string line;

            if (!ChatTokens.TryResolve(template, context, out line))
            {
                return;
            }

            SetCooldown(bot, TimeSpan.FromSeconds(
                Utility.RandomMinMax(MinReplyCooldownSeconds, MaxReplyCooldownSeconds)));

            // Bots are ephemeral and serials are not reused within a boot, so this only ever grows
            // by one per bot that has ever answered. Clearing wholesale beats sweeping it.
            if (_lastReplyAt.Count > 2000)
            {
                _lastReplyAt.Clear();
            }

            _lastReplyAt[bot.Serial] = CustomTime.Now;

            // Turn first. Answering without looking up is its own kind of wrong.
            Direction direction = bot.GetDirectionTo(speaker);

            if (bot.Direction != direction)
            {
                bot.Direction = direction;
            }

            double delay = 0.9 + (Utility.RandomDouble() * 1.2) + Math.Min(line.Length * 0.04, 1.2);

            Timer.DelayCall(TimeSpan.FromSeconds(delay), () =>
            {
                if (bot.Deleted || !bot.Alive || bot.Hidden || speaker.Deleted)
                {
                    return;
                }

                bot.Say(line);

                bot.SpeechLines++;
                ChatLibrary.NoteSpoken(line);
                Replies++;
            });
        }

        /// <summary>
        /// Am I the closest eligible bot to the speaker?
        ///
        /// Keeps a "hi" from turning six heads at once - one person answers, like a real room.
        /// </summary>
        private static bool ClosestEligible(PlayerBot bot, Mobile speaker, int myDistance)
        {
            IPooledEnumerable<Mobile> mobiles =
                speaker.Map.GetMobilesInRange(speaker.Location, GreetRange);

            try
            {
                foreach (Mobile mobile in mobiles)
                {
                    var other = mobile as PlayerBot;

                    if (other == null || other == bot || other.Deleted || !other.Alive
                        || other.Hidden || other.Combatant != null || OnCooldown(other) || IsAway(other))
                    {
                        continue;
                    }

                    if (Chebyshev(other.Location, speaker.Location) < myDistance)
                    {
                        return false;
                    }
                }
            }
            finally
            {
                mobiles.Free();
            }

            return true;
        }

        /// <summary>
        /// Is this bot away from the keyboard?
        ///
        /// Today that is exactly the BankSitter's Afk role. When the macro roles come back with
        /// spellcasting they answer here too, which is why the question is asked of the behaviour
        /// rather than tested against one role inline.
        /// </summary>
        private static bool IsAway(PlayerBot bot)
        {
            var sitter = bot.Behavior as BankSitterBehavior;

            return sitter != null && sitter.IsAway;
        }

        // ---- cooldowns ----

        private static bool OnCooldown(PlayerBot bot)
        {
            DateTime until;

            return _cooldowns.TryGetValue(bot.Serial, out until) && CustomTime.Now < until;
        }

        private static void SetCooldown(PlayerBot bot, TimeSpan duration)
        {
            if (_cooldowns.Count > 2000)
            {
                _cooldowns.Clear();
            }

            _cooldowns[bot.Serial] = CustomTime.Now + duration;
        }

        // ---- the claim ----

        private static bool Claimed(Mobile speaker, string text)
        {
            return _claimSpeaker == speaker.Serial
                && _claimText == text
                && CustomTime.Now - _claimAt < ClaimWindow;
        }

        private static void Claim(Mobile speaker, string text)
        {
            _claimSpeaker = speaker.Serial;
            _claimText = text;
            _claimAt = CustomTime.Now;
        }

        // ---- matchers ----

        private static readonly string[] Greetings =
        {
            "hi", "hello", "hey", "heya", "hiya", "yo", "sup", "hail", "oi",
            "greetings", "wassup", "o/", "ello", "hey there",
        };

        private static readonly string[] QuestionStarts =
        {
            "who", "what", "where", "when", "why", "how", "anyone", "any1",
            "can", "does", "do", "is", "are", "u know", "you know",
        };

        private static bool IsGreeting(string lower)
        {
            for (int i = 0; i < Greetings.Length; i++)
            {
                string greeting = Greetings[i];

                if (lower == greeting
                    || lower.StartsWith(greeting + " ", StringComparison.Ordinal)
                    || lower.StartsWith(greeting + ",", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool LooksLikeQuestion(string lower)
        {
            if (lower.EndsWith("?", StringComparison.Ordinal))
            {
                return true;
            }

            for (int i = 0; i < QuestionStarts.Length; i++)
            {
                if (lower.StartsWith(QuestionStarts[i] + " ", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whole-word match without a regex allocation.
        ///
        /// Whole-word matters: a bot called Al must not answer to "always", and this runs for
        /// every bot in earshot of every utterance.
        /// </summary>
        private static bool ContainsWord(string text, string word)
        {
            if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(word) || word.Length < 2)
            {
                return false;
            }

            int index = 0;

            while ((index = text.IndexOf(word, index, StringComparison.Ordinal)) >= 0)
            {
                bool startOk = index == 0 || !Char.IsLetter(text[index - 1]);
                int end = index + word.Length;
                bool endOk = end >= text.Length || !Char.IsLetter(text[end]);

                if (startOk && endOk)
                {
                    return true;
                }

                index = end;
            }

            return false;
        }

        private static int Chebyshev(Point3D a, Point3D b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);

            return dx > dy ? dx : dy;
        }
    }
}
