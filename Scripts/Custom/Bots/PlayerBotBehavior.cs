// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// PlayerBotBehavior.cs — the brain contract, and the voice.
//
// A behaviour is what a bot is currently doing. Swapping the Behavior property
// swaps the brain; OnAttached and OnDetached fire either side of the change.
//
// Session 7d added the speech half, in the shapes upstream gave it, which is
// what the previous session's header promised it was leaving room for. A
// behaviour now declares WHAT it talks about (ChatCategories) and HOW OFTEN
// (ChatChance and the cooldown pair), and the machinery below does the rest.
//
// THE THREE GATES, IN THIS ORDER, AND THE ORDER IS THE COST CONTROL:
//
//   1. cooldown   - a long, an integer compare
//   2. chance     - a double compare
//   3. audience   - a sector query, and the only expensive one
//
// Behaviour ticks run on a shared timer whether or not anybody is watching
// (BotTickManager), so without gate 3 a thousand bots would chatter to an empty
// world for ever. Putting it last means the query is paid only when a line is
// otherwise about to be said. Upstream ordered it this way and the reason
// survives the port intact.

using System;

using Server.Mobiles;

namespace Server.Custom
{
    public abstract class PlayerBotBehavior
    {
        /// <summary>
        /// The name this behaviour serializes as, and the key it is constructed back from.
        ///
        /// The only abstract member, deliberately: the whole lifecycle model is "the state IS the
        /// behaviour name", so a brain that cannot name itself cannot be persisted or transitioned
        /// into.
        /// </summary>
        public abstract string SerializableName { get; }

        /// <summary>
        /// One line describing what this bot is doing right now, for [BotInfo and the live map.
        /// Null means "nothing worth saying beyond the behaviour's name".
        /// </summary>
        public virtual string GetStatusLine(PlayerBot bot)
        {
            return null;
        }

        /// <summary>
        /// Called on the game thread by BotTickManager, on the shared behaviour timer.
        ///
        /// Not the AI timer: PlayerRangeSensitive stops that entirely when no player is in the
        /// sector, and a bot must keep deciding when nobody is watching. Speech is the exception
        /// that wants the opposite, which is what IsPlayerNearby is for.
        /// </summary>
        public virtual void Tick(PlayerBot bot)
        {
        }

        /// <summary>
        /// May the lifecycle interrupt this behaviour right now?
        ///
        /// Default true. A behaviour says no when being swapped mid-action would look broken
        /// rather than natural - a Traveler halfway down a street, a Shopper mid-visit. The
        /// lifecycle skips it and asks again on the next pass rather than forcing it.
        /// </summary>
        public virtual bool CanTransition(PlayerBot bot)
        {
            return true;
        }

        /// <summary>
        /// When a timed visit ends, or null for an open-ended behaviour.
        ///
        /// A behaviour reached by ARRIVING somewhere - a BankSitter at a bank, a Shopper at a shop -
        /// is a visit: it runs for a while and then hands the bot back to travelling. A behaviour
        /// reached by a LIFECYCLE ROLL is open-ended and ends when its phase does.
        ///
        /// Set by whoever performs the handoff, not by the behaviour itself. Not serialized: a
        /// visit does not survive a restart, and nor does the bot.
        /// </summary>
        public DateTime? VisitExpiresAt { get; set; }

        /// <summary>
        /// Ends the visit if it has lapsed, returning true if it did.
        ///
        /// Call it as the first thing in Tick, and RETURN IMMEDIATELY when it returns true: it has
        /// already swapped the bot back to a Traveler, which detached this behaviour. Touching any
        /// more state afterwards is touching a brain the bot no longer has.
        /// </summary>
        protected bool CheckVisitExpired(PlayerBot bot)
        {
            // A FIXTURE'S VISIT NEVER LAPSES, because a fixture is not visiting.
            //
            // BotLifecycle already refuses to roll an exempt bot, but the roller is not the only
            // thing that can take a brain away: a behaviour that stamps its own window - Gatherer
            // does, at GathererBehavior.cs:217-219 - would hand a fixture back to Traveler on its
            // own clock and walk the bank crowd out of the bank. The invariant belongs here, where
            // every behaviour asks the question, rather than in each behaviour that might set one.
            if (VisitExpiresAt == null || bot == null || bot.LifecycleExempt
                || CustomTime.Now < VisitExpiresAt.Value)
            {
                return false;
            }

            bot.SetBehavior(BotBehaviors.Create("Traveler"), "visit expired");

            return true;
        }

        public virtual void OnAttached(PlayerBot bot)
        {
        }

        public virtual void OnDetached(PlayerBot bot)
        {
        }

        /// <summary>
        /// The walker steering this bot, or null when it is not walking.
        ///
        /// Exposed on the base so anything outside can ask "is this bot wedged?" without knowing
        /// which of the four walking behaviours it happens to be. The live map needs exactly that
        /// and nothing more; the alternative was a stuck flag maintained by each behaviour, which
        /// is the same fact written down four times and eventually four different ways.
        /// </summary>
        public virtual NavWalker Walker
        {
            get { return null; }
        }

        /// <summary>
        /// Subscribe a freshly built walker to this bot's event log.
        ///
        /// Every walking behaviour here creates its walker the same way and would otherwise
        /// repeat this lambda four times, which is four places for it to drift. Called once, next
        /// to the Arrived assignment, and deliberately NOT inside NavWalker's constructor: Core
        /// navigation knows nothing about bots, and a walker steering a daily-life townsfolk has
        /// no bot log to write to.
        /// </summary>
        protected static void LogWalker(PlayerBot bot, NavWalker walker)
        {
            if (bot == null || walker == null)
            {
                return;
            }

            walker.RungFired = (w, rung, what) =>
                BotLog.Note(bot, BotLogKind.Rung, "stuck at {0},{1} [{2}] - {3}", bot.X, bot.Y, rung, what);
        }

        // ---------------------------------------------------------------------
        // Speech
        // ---------------------------------------------------------------------

        /// <summary>
        /// Which corpus categories this behaviour draws ambient chatter from.
        ///
        /// Empty means silent, which is the correct default: a behaviour that has not thought
        /// about what it would talk about should not talk.
        /// </summary>
        public string[] ChatCategories { get; protected set; }

        /// <summary>Chance per tick of wanting to say something, before the audience gate.</summary>
        public double ChatChance { get; protected set; }

        public TimeSpan MinChatCooldown { get; protected set; }

        public TimeSpan MaxChatCooldown { get; protected set; }

        /// <summary>
        /// How far a real player can be and still be worth talking to.
        ///
        /// 22 is upstream's, and it is generous on purpose: a bank crowd should be audible as you
        /// walk up to it, not switch on when you arrive.
        /// </summary>
        public int ChatHearRange { get; protected set; }

        /// <summary>
        /// How often a line gets a capital letter.
        ///
        /// Upstream's note, kept because the number looks arbitrary without it: it "mimics how
        /// some real players naturally capitalize sentences while others don't. ~35% feels right -
        /// most lowercase (1998 UO style), but enough capitals to vary the feed."
        /// </summary>
        protected virtual double CapitalizeChance
        {
            get { return 0.35; }
        }

        /// <summary>Late-night lines, when it is actually late at night.</summary>
        protected virtual string NightTalkCategory
        {
            get { return ChatLibrary.NightTalk; }
        }

        /// <summary>
        /// Where this behaviour is, as a nav destination id, or null.
        ///
        /// The only context a {place} or {dest} token has to work from. A behaviour that knows
        /// where it is should say so; one that does not returns null and those lines are simply
        /// never spoken by it.
        /// </summary>
        public virtual string CurrentDestinationId
        {
            get { return null; }
        }

        // Wraparound-safe: compared by subtraction, never a < b (CLAUDE.md section 15).
        private long _nextChatAllowed;

        protected PlayerBotBehavior()
        {
            // The quiet default. Every behaviour that talks overrides these in its own ctor.
            ChatCategories = new string[0];
            ChatChance = 0.15;
            MinChatCooldown = TimeSpan.FromSeconds(30.0);
            MaxChatCooldown = TimeSpan.FromSeconds(90.0);
            ChatHearRange = 22;
        }

        /// <summary>
        /// Say an ambient line, if the cooldown has lapsed, the roll lands and somebody can hear.
        ///
        /// Returns true only when a line was actually spoken, so a caller can punctuate it - the
        /// bank crowd faces whoever it just spoke to, which is the difference between talking to
        /// someone and announcing at a wall.
        /// </summary>
        protected bool TrySpeak(PlayerBot bot)
        {
            if (!CanSpeak(bot))
            {
                return false;
            }

            // Gate 1: cooldown.
            if (Core.TickCount - _nextChatAllowed < 0)
            {
                return false;
            }

            // Gate 2: the roll.
            if (Utility.RandomDouble() > ChatChance)
            {
                return false;
            }

            // Gate 3: is there anybody to talk to? The expensive one, paid last.
            if (!IsPlayerNearby(bot))
            {
                return false;
            }

            // GOSSIP GOES HERE, when the event journal lands. Upstream spends 20% of ambient slots
            // retelling something that really happened - a murder, a notable kill - composed from
            // its event ring through the Gossip/ templates. Those files are copied and sitting
            // there unread; ChatLibrary deliberately does not scan that subdirectory, and their
            // {actor}/{other}/{when} tokens are registered as Journal-owned and unwired, so the
            // lines cannot be spoken by accident before their session arrives.

            string line = PickTexturedLine();

            if (String.IsNullOrEmpty(line))
            {
                return false;
            }

            return SpeakLine(bot, line);
        }

        /// <summary>
        /// A line the CALLER composed, through the same gates.
        ///
        /// For speech about something REAL rather than drawn from a pool: a bot describing what
        /// is actually in its pack, at a price it would actually take.
        ///
        /// NOTHING CALLS IT YET, and that is the seam rather than an oversight. Its one upstream
        /// caller is the Hawker's stock pitch, which BotShop builds from a real item - the economy
        /// session. It is here now because the alternative when that session lands is to
        /// rediscover that a composed line still has to pass the cooldown, the roll and the
        /// audience gate like any other, and the obvious shortcut is a bare Say that bypasses all
        /// three.
        ///
        /// The Shopper's vendor triggers deliberately do NOT come through here - see its Tick.
        /// </summary>
        protected bool TrySpeakLine(PlayerBot bot, string line, double chance)
        {
            if (String.IsNullOrEmpty(line) || !CanSpeak(bot))
            {
                return false;
            }

            if (Core.TickCount - _nextChatAllowed < 0)
            {
                return false;
            }

            if (Utility.RandomDouble() > chance)
            {
                return false;
            }

            if (!IsPlayerNearby(bot))
            {
                return false;
            }

            return SpeakLine(bot, line);
        }

        /// <summary>
        /// The ambient pick, with the two texture layers that keep a feed from reading as a loop.
        ///
        /// Both steal an occasional slot rather than adding one, so neither makes a bot chattier.
        /// </summary>
        private string PickTexturedLine()
        {
            string[] categories = ChatCategories;

            if (categories == null || categories.Length == 0)
            {
                return null;
            }

            string line = null;

            // The real wall clock, not the shard's: these lines are about the person at the
            // keyboard ("i have work in the morning"), not the character.
            int hour = DateTime.Now.Hour;
            bool night = hour >= 21 || hour < 5;

            double texture = Utility.RandomDouble();

            if (night && texture < 0.25)
            {
                line = ChatLibrary.PickRandom(NightTalkCategory);
            }
            else if (texture < 0.10)
            {
                // emotes.txt, which on this shard is a Say like any other - there is no emote
                // path here, and its asterisk lines are rejected at load. What is left is the
                // stray "asdf" and "oops wrong window" typo-noise, and its own header explains
                // why that is deliberate: "imperfection reads human".
                line = ChatLibrary.PickRandom(ChatLibrary.Emotes);
            }

            if (String.IsNullOrEmpty(line))
            {
                line = ChatLibrary.PickRandom(categories);
            }

            return line;
        }

        /// <summary>
        /// The shared voice treatment: resolve, capitalise, say, count, restart the cooldown.
        ///
        /// Every spoken ambient line funnels through here. Upstream also hung BotBanking and
        /// BotWantAd off this point - "the words are said, now do the thing" - so that a spoken
        /// "withdraw 5000" moved real coin and a "WTB GM hally" became a want a player could
        /// answer. Both belong to the economy session; the funnel is here waiting for them.
        /// </summary>
        private bool SpeakLine(PlayerBot bot, string line)
        {
            var context = new ChatTokenContext
            {
                Bot = bot,
                DestinationId = CurrentDestinationId,
            };

            string resolved;

            // The last line of defence. Corpus lines whose tokens are not wired were already
            // dropped at load, so this catches the other case: a wired token with nothing to
            // resolve against right now. Silence beats a sentence with a hole in it.
            if (!ChatTokens.TryResolve(line, context, out resolved))
            {
                return false;
            }

            // Probabilistic capitalisation. Lines starting with a non-letter keep their case as
            // written, so "WTS GM hally" is never mangled into "wTS".
            if (Utility.RandomDouble() < CapitalizeChance
                && resolved.Length > 0
                && Char.IsLower(resolved[0]))
            {
                resolved = Char.ToUpper(resolved[0]) + resolved.Substring(1);
            }

            // Mobile.Say picks up bot.SpeechHue on its own; the hue was rolled at construction.
            bot.Say(resolved);

            bot.SpeechLines++;
            ChatLibrary.NoteSpoken(resolved);

            BotLog.Note(bot, BotLogKind.Speech, "\"{0}\"", resolved);

            _nextChatAllowed = Core.TickCount + (long)RandomCooldown().TotalMilliseconds;

            return true;
        }

        private static bool CanSpeak(PlayerBot bot)
        {
            return bot != null
                && !bot.Deleted
                && bot.Alive
                && !bot.Hidden
                && bot.Map != null
                && bot.Map != Map.Internal;
        }

        private TimeSpan RandomCooldown()
        {
            double min = MinChatCooldown.TotalMilliseconds;
            double max = MaxChatCooldown.TotalMilliseconds;

            return TimeSpan.FromMilliseconds(min + (Utility.RandomDouble() * (max - min)));
        }

        /// <summary>
        /// Is a real player close enough to hear this?
        ///
        /// A bounded sector query, not a World.Mobiles sweep and not NetState.Instances. The
        /// sector query is what FaceNearestPerson already does, it costs nothing for a bot alone
        /// in the woods, and unlike the client list it can see a mobile with no NetState - which
        /// is what the chat probe's synthetic player is.
        ///
        /// Upstream tested "is a PlayerMobile and is not a PlayerBot". Here a PlayerBot is a
        /// BaseCreature, so it can never be a PlayerMobile and the second test is unnecessary -
        /// bots cannot trigger each other into an echo chamber by construction.
        /// </summary>
        protected bool IsPlayerNearby(PlayerBot bot)
        {
            IPooledEnumerable<Mobile> mobiles = bot.Map.GetMobilesInRange(bot.Location, ChatHearRange);

            try
            {
                foreach (Mobile mobile in mobiles)
                {
                    if (mobile is PlayerMobile && !mobile.Deleted && mobile.Alive)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                // ServUO's pooled enumerable does not free itself, unlike ModernUO's.
                mobiles.Free();
            }

            return false;
        }
    }
}
