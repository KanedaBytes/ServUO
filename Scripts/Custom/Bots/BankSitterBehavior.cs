// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BankSitterBehavior.cs — the standing crowd.
//
// A bank with people at it is the single clearest sign a shard is inhabited,
// and it costs almost nothing: the bots are already walking there.
//
// HOW IT STANDS STILL, which is the whole trick. It does not steer itself. It
// sets Home to a spot near where it arrived and RangeHome to a couple of tiles,
// and the stock wander does the rest - BaseAI.WalkRandomInHome drifts inside two
// thirds of the range and steps back beyond it (BaseAI.cs:2548-2570). That is
// "standing about, shuffling occasionally" for no movement code at all.
// DailyLifePatron does the same thing in the tavern (TavernSystem.cs:102-103).
//
// Upstream had to steer by hand - a WalkBackIfShoved stepping one tile per tick -
// because their bot was a PlayerMobile with no BaseAI to delegate to. Ours is a
// BaseCreature, so the engine does it. Their PickScatteredHome we DO keep, and
// the reason is at OnAttached: it is load-bearing here in a way it was not there.
//
// THE ROLES. Upstream rolls six. Session 7d brought the three that are pure
// speech or pure silence - Regular, Hawker and Afk - and the three macro roles
// still need real spellcasting, Hidden toggling and reagent bookkeeping, which
// is a lot of machinery for posture. Their WEIGHTS are kept in the table below
// rather than removed, so restoring them when combat lands is one column of
// edits and not a re-derivation from upstream. See RollRole.

using System;

namespace Server.Custom
{
    public class BankSitterBehavior : PlayerBotBehavior
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// How far a sitter may stand from its spot before the engine walks it back.
        ///
        /// WAS `SitterRange = 2`, WRITTEN INTO RangeHome, AND THAT IS NOT WHAT RangeHome DOES. Its
        /// comment said "two lets them shuffle like people waiting", and it did far more than that:
        /// with a non-zero RangeHome, BaseAI.WalkRandomInHome takes a 50/50 between a random step and
        /// a step back toward Home at any distance from 1 to RangeHome (BaseAI.cs:2549-2571), on the
        /// AI timer, which is four times a second for a settled bot. Measured before the change: 30.1
        /// idle steps per bot-minute, 239 of them random wander and 196 of them the engine walking
        /// the bot back to a spot it had just shuffled off. That is not a shuffle, it is a pace.
        ///
        /// Now RangeHome is 0 - the pin idiom the Crafter already used, measured at 0.00 idle steps
        /// per bot-minute in the same window - and the slack is a TOLERANCE consulted by
        /// PlayerBot.CheckIdle instead: inside it the bot stands still, outside it the engine walks it
        /// straight back. Upstream's is HomeRadius 1 with the comment "I got shoved"
        /// (uo-offline BankSitterBehavior.cs:58, :586-617), and the number now lives in
        /// bots.json life.idle.sitterTolerance because it is the one somebody re-tunes by looking.
        /// </summary>
        private static int Tolerance
        {
            get { return BotLifecycle.Config_.Idle.SitterTolerance; }
        }

        /// <summary>How far a sitter looks for somebody to face.</summary>
        private const int FaceRange = 6;

        /// <summary>How far off the arrival point a sitter settles. Bank floors are large.</summary>
        /// <summary>
        /// How far a sitter may settle from the arrival it walked to. Public because the walk probe
        /// judges a settled sitter by this rather than by the arrival range: standing off the point
        /// is the DESIGN here, not a miss - see PickScatteredHome.
        /// </summary>
        public const int ScatterRadius = 4;

        private const int ScatterAttempts = 12;

        /// <summary>
        /// How long a sitter may spend failing to reach its spot before it settles for this one.
        ///
        /// Twenty seconds: long enough that walking four tiles round a crowd is not mistaken for a
        /// grind, short enough that nobody watching sees a bot shoving a counter for a whole visit.
        /// </summary>
        private const long ResettleMs = 20000L;

        /// <summary>
        /// Chance per tick of doing something idle - a turn, or a look in the bank box.
        ///
        /// In bots.json rather than here since the fidget fix, because with the stock wander gone
        /// this and the facing change ARE the idle life: if a still crowd reads as statues, this is
        /// the number that answers it, and it should not need a rebuild to try.
        /// </summary>
        private static double IdleChance
        {
            get { return BotLifecycle.Config_.Idle.BeatChance; }
        }

        private string _destinationId;
        private bool _pinned;

        /// <summary>When this sitter last stood on (or within tolerance of) its spot.</summary>
        private long _onSpotAt;
        private BankRole _role;

        /// <summary>
        /// What kind of person is standing at this bank.
        ///
        /// Upstream's six, three of them live. The macro three are listed so the enum still
        /// describes the design, and so the weights in RollRole have somewhere to point when
        /// spellcasting lands.
        /// </summary>
        public enum BankRole
        {
            /// <summary>Talks about everything. The bank crowd's baseline.</summary>
            Regular,

            /// <summary>Talks shop, loudly and often, and nothing else.</summary>
            Hawker,

            /// <summary>Away from the keyboard. Says "afk" once, then nothing, ever.</summary>
            Afk,

            /// <summary>SEAM: needs spellcasting. Rolls as Regular today.</summary>
            ResistMacro,

            /// <summary>SEAM: needs Hidden toggling. Rolls as Regular today.</summary>
            HidingMacro,

            /// <summary>SEAM: needs Hidden toggling and stealth. Rolls as Regular today.</summary>
            StealthMacro,
        }

        public override string SerializableName
        {
            get { return "BankSitter"; }
        }

        /// <summary>Which bank this sitter is standing at. Read by the crowd census.</summary>
        public string DestinationId
        {
            get { return _destinationId; }
            set { _destinationId = value; }
        }

        /// <summary>Where a {place} token resolves from while this bot is standing here.</summary>
        public override string CurrentDestinationId
        {
            get { return _destinationId; }
        }

        public BankRole Role
        {
            get { return _role; }
        }

        /// <summary>
        /// Is this one of the away-from-keyboard roles?
        ///
        /// Asked by the speech responder, which must not make an AFK bot answer you. Phrased as a
        /// question about the role rather than a test against Afk inline, so the macro roles
        /// answer it correctly the moment they come back.
        /// </summary>
        public bool IsAway
        {
            get
            {
                return _role == BankRole.Afk
                    || _role == BankRole.ResistMacro
                    || _role == BankRole.HidingMacro
                    || _role == BankRole.StealthMacro;
            }
        }

        public BankSitterBehavior()
        {
            ApplyRoleVoice(BankRole.Regular);
        }

        /// <summary>
        /// Upstream's role distribution, all six rows kept.
        ///
        /// The macro three map to Regular because their machinery is not ported. Restoring them is
        /// deleting the three redirects, NOT re-deriving the numbers - which is the whole reason
        /// the rows are still here rather than the table being rewritten as a three-way roll.
        ///
        ///   Regular      30      talks about everything
        ///   Hawker       20      talks shop
        ///   Afk          15      silent
        ///   ResistMacro  15      SEAM -> Regular
        ///   HidingMacro  10      SEAM -> Regular
        ///   StealthMacro 10      SEAM -> Regular
        ///
        /// Live distribution today is therefore Regular 65, Hawker 20, Afk 15.
        /// </summary>
        private static BankRole RollRole()
        {
            int roll = Utility.Random(100);

            if (roll < 30)
            {
                return BankRole.Regular;
            }

            if (roll < 50)
            {
                return BankRole.Hawker;
            }

            if (roll < 65)
            {
                return BankRole.Afk;
            }

            // SEAM: ResistMacro (65-79), HidingMacro (80-89), StealthMacro (90-99). Return the
            // real role here once spellcasting and Hidden toggling exist.
            return BankRole.Regular;
        }

        /// <summary>
        /// What this role talks about, and how loudly.
        ///
        /// Banks are loud places, so even a Regular is chattier and quicker off the mark than the
        /// wandering archetype.
        /// </summary>
        private void ApplyRoleVoice(BankRole role)
        {
            _role = role;

            switch (role)
            {
                case BankRole.Hawker:
                    {
                        // A seller talks shop and nothing else.
                        //
                        // SEAM: the WTS half is missing, and its absence is deliberate. Upstream's
                        // hawker shouts a line built by BotShop from a real item in its pack, so
                        // "WTS GM halberd 5k" means there IS one and 5k buys it. BotShop is the
                        // economy layer. A WTS from a bot holding nothing - or holding something
                        // no trade path can hand over - is precisely the lie this system exists
                        // not to tell, so until then a hawker only ever asks to buy. Wanting to
                        // buy promises nothing.
                        ChatCategories = new[] { ChatLibrary.Wtb };
                        ChatChance = 0.55;
                        MinChatCooldown = TimeSpan.FromSeconds(10.0);
                        MaxChatCooldown = TimeSpan.FromSeconds(25.0);
                        break;
                    }

                case BankRole.Afk:
                case BankRole.ResistMacro:
                case BankRole.HidingMacro:
                case BankRole.StealthMacro:
                    {
                        // Statues do not talk, and macroers were away by definition.
                        ChatCategories = new string[0];
                        ChatChance = 0.0;
                        break;
                    }

                default:
                    {
                        // Everything trade-related plus small talk. bank_actions are short ("bank",
                        // "withdraw 1000") so they land hard; lfg is here because the bank is
                        // historically where you found a group.
                        //
                        // No "wts", for the reason above: a regular is holding nothing.
                        ChatCategories = new[]
                        {
                            ChatLibrary.SmallTalk,
                            ChatLibrary.BankActions,
                            ChatLibrary.Wtb,
                            ChatLibrary.Lfg,
                        };

                        ChatChance = 0.25;
                        MinChatCooldown = TimeSpan.FromSeconds(15.0);
                        MaxChatCooldown = TimeSpan.FromSeconds(45.0);
                        break;
                    }
            }
        }

        /// <summary>
        /// Force a role, for the chat probe.
        ///
        /// In memory and per-instance, never persisted - the same discipline BotLifecycle.Override
        /// keeps. A probe that needs a bot which definitely talks cannot be left at the mercy of a
        /// 15% chance of rolling the one role whose entire job is silence.
        /// </summary>
        public void ForceRole(BankRole role)
        {
            ApplyRoleVoice(role);
            _roleFixed = true;
        }

        private bool _roleFixed;

        public override string GetStatusLine(PlayerBot bot)
        {
            return _destinationId == null ? "at the bank" : "at " + _destinationId;
        }

        public override void OnAttached(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            // Standing at a counter, or sitting one out in the saddle - the bot's own
            // disposition decides, drawn at birth from its tier and the Wealthy trait. This used to
            // be an unconditional dismount that also DELETED the horse, which is why a bank was
            // uniformly on foot and why nobody was ever mounted again after their first visit.
            BotMovement.Settle(bot, mustDismount: false);

            // Stand NEAR where it arrived, not exactly on it.
            //
            // This is upstream's PickScatteredHome and the reason for it is worth keeping with it:
            // "without this every bot homes on the exact tile it arrived at and the crowd stacks."
            // On this shard it is worse than cosmetic. An uncontrolled BaseCreature cannot walk
            // through another mobile (Movement.cs:411), so sitters parked on the arrival points
            // make those tiles unreachable - and the arrival points are exactly where the next
            // bot is aiming. Dropping this turned a bank with a crowd floor into a traffic jam
            // that showed up as a flood of Sidestep and Door recoveries.
            bot.Home = PickScatteredHome(bot);

            // RangeHome 0, NOT the old SitterRange: a non-zero RangeHome is a licence to wander
            // inside it, not a tolerance. See Tolerance above for what that cost and what replaced it.
            bot.RangeHome = 0;
            bot.IdleTolerance = Tolerance;
            _onSpotAt = Core.TickCount;
            _pinned = true;

            // The role is rolled on arrival rather than in the constructor, so a bot that visits
            // the same bank twice is not the same character both times.
            if (!_roleFixed)
            {
                ApplyRoleVoice(RollRole());
            }

            // The one line an AFK player ever said, on the way out of the chair. Most did not
            // even manage that, hence the roll.
            if (_role == BankRole.Afk && Utility.RandomDouble() < 0.25)
            {
                bot.Say("afk");

                bot.SpeechLines++;
                ChatLibrary.NoteSpoken("afk");
            }
        }

        public override void OnDetached(PlayerBot bot)
        {
            Release(bot);
        }

        /// <summary>
        /// A standable tile a short way off the arrival point, or the arrival point itself when
        /// nothing better is free.
        ///
        /// checkMobiles is TRUE, unlike upstream's: picking a tile somebody is already standing on
        /// is how a crowd stacks in the first place, and the cost of asking is one sector query
        /// per bot per bank visit.
        /// </summary>
        private static Point3D PickScatteredHome(PlayerBot bot)
        {
            Point3D arrival = bot.Location;
            Map map = bot.Map;

            if (map == null || map == Map.Internal)
            {
                return arrival;
            }

            for (int attempt = 0; attempt < ScatterAttempts; attempt++)
            {
                int dx = Utility.RandomMinMax(-ScatterRadius, ScatterRadius);
                int dy = Utility.RandomMinMax(-ScatterRadius, ScatterRadius);

                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                int x = arrival.X + dx;
                int y = arrival.Y + dy;
                int z = NavWalker.ResolveZ(map, new Point3D(x, y, arrival.Z));

                if (map.CanFit(x, y, z, 16, false, false, true) && !Occupied(map, x, y, z))
                {
                    return new Point3D(x, y, z);
                }
            }

            // Nothing free nearby. Standing on the arrival point is worse than standing beside it,
            // but it is better than not standing anywhere.
            return arrival;
        }

        private static bool Occupied(Map map, int x, int y, int z)
        {
            IPooledEnumerable<Mobile> mobiles = map.GetMobilesInRange(new Point3D(x, y, z), 0);

            try
            {
                foreach (Mobile mobile in mobiles)
                {
                    if (mobile != null && !mobile.Deleted && mobile.X == x && mobile.Y == y
                        && mobile.Alive && Math.Abs(mobile.Z - z) < 16)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                mobiles.Free();
            }

            return false;
        }

        /// <summary>
        /// Give the leash back.
        ///
        /// A behaviour that sets Home MUST clear it. PlayerBot's constructor sets Home to zero and
        /// everything downstream assumes that: WalkRandomInHome special-cases a zero Home into a
        /// free wander (BaseAI.cs:2516), so a leftover Home would quietly leash a later Traveler
        /// back to this bank every time it stopped commuting.
        /// </summary>
        private void Release(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || !_pinned)
            {
                return;
            }

            bot.Home = Point3D.Zero;
            bot.RangeHome = 0;
            bot.IdleTolerance = 0;
            _pinned = false;
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            // The visit ends on its own clock. This has already swapped the brain, so nothing
            // below may touch the bot.
            if (CheckVisitExpired(bot))
            {
                return;
            }

            // GIVE UP ON A SPOT IT CANNOT GET BACK TO, and stand where it is instead.
            //
            // The walk-back is the engine's straight-line DoMove toward Home (BaseAI.cs:2573-2578),
            // not a pathfind, and PickScatteredHome validates its tile for standing and occupancy
            // but NEVER for reachability - so a spot across a counter is a spot this bot will walk
            // into a wall to reach. That was survivable while base.CheckIdle's five-percent pause
            // damped it; the fidget fix returns false instead, deliberately, so the walk-back is
            // immediate and certain, and immediate-and-certain against a wall is a grind.
            //
            // Bounded rather than prevented: re-pinning to where it actually stands is what a person
            // does when the spot they wanted turns out to be behind the counter, and it is the one
            // outcome that cannot loop. The grind would also be INVISIBLE to the step census - a
            // blocked DoMove takes no step - which is the argument for handling it here rather than
            // waiting to see it in a window.
            if (_pinned && Core.TickCount - _onSpotAt >= ResettleMs)
            {
                int off = Math.Max(
                    Math.Abs(bot.X - bot.Home.X), Math.Abs(bot.Y - bot.Home.Y));

                if (off > bot.IdleTolerance)
                {
                    bot.Home = bot.Location;
                }

                _onSpotAt = Core.TickCount;
            }
            else if (_pinned
                && Math.Max(Math.Abs(bot.X - bot.Home.X), Math.Abs(bot.Y - bot.Home.Y))
                    <= bot.IdleTolerance)
            {
                _onSpotAt = Core.TickCount;
            }

            // Speak first: chatter is the whole point of a bank crowd, and the posture below was
            // always written to punctuate it rather than to stand on its own.
            if (TrySpeak(bot))
            {
                // You talk TO someone. A bot that announces at a wall is worse than a quiet one.
                FaceNearestPerson(bot);

                // A hawker punctuates the pitch - waves the goods around.
                if (_role == BankRole.Hawker && Utility.RandomDouble() < 0.40)
                {
                    bot.Animate(33, 5, 1, true, false, 0);
                }

                return;
            }

            // Idle life between lines. An AFK bot keeps doing this: it is a character standing
            // there, not a character switched off, and the bank box gesture is exactly what a
            // client left running looked like.
            if (Utility.RandomDouble() >= IdleChance)
            {
                return;
            }

            if (Utility.RandomDouble() < BotLifecycle.Config_.Idle.BankBoxShare)
            {
                // The bend over the bank box. Every bank crowd did this all day.
                bot.Animate(32, 5, 1, true, false, 0);
            }
            else
            {
                FaceNearestPerson(bot);
            }
        }

        /// <summary>
        /// Turn toward the nearest visible person.
        ///
        /// The smallest thing in the whole file and the one that matters most: a crowd that all
        /// faces the same way reads as scenery, and a crowd that looks at each other reads as
        /// people.
        /// </summary>
        private static void FaceNearestPerson(PlayerBot bot)
        {
            Mobile nearest = null;
            int bestDistance = Int32.MaxValue;

            IPooledEnumerable<Mobile> mobiles = bot.Map.GetMobilesInRange(bot.Location, FaceRange);

            try
            {
                foreach (Mobile mobile in mobiles)
                {
                    if (mobile == bot || mobile.Deleted || !mobile.Alive || mobile.Hidden)
                    {
                        continue;
                    }

                    // Players and bots both - a sitter should look at a real person too. Bots
                    // qualify because PlayerBot sets Mobile.Player for the party gate.
                    if (!mobile.Player)
                    {
                        continue;
                    }

                    int dx = Math.Abs(mobile.X - bot.X);
                    int dy = Math.Abs(mobile.Y - bot.Y);
                    int distance = dx > dy ? dx : dy;

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        nearest = mobile;
                    }
                }
            }
            finally
            {
                // ServUO's pooled enumerable does not free itself, unlike ModernUO's.
                mobiles.Free();
            }

            if (nearest == null)
            {
                return;
            }

            Direction direction = bot.GetDirectionTo(nearest);

            if (bot.Direction != direction)
            {
                bot.Direction = direction;
            }
        }
    }
}
