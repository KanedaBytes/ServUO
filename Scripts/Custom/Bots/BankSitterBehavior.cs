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
        /// How far a sitter may drift from its spot.
        ///
        /// Small but NOT zero. Zero pins it to one tile and the crowd reads as statues that slide
        /// back into place when nudged; two lets them shuffle like people waiting.
        /// </summary>
        public const int SitterRange = 2;

        /// <summary>How far a sitter looks for somebody to face.</summary>
        private const int FaceRange = 6;

        /// <summary>How far off the arrival point a sitter settles. Bank floors are large.</summary>
        private const int ScatterRadius = 4;

        private const int ScatterAttempts = 12;

        /// <summary>Chance per tick of doing something idle - a turn, or a look in the bank box.</summary>
        private const double IdleChance = 0.05;

        private string _destinationId;
        private bool _pinned;
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

            // Standing at a counter, not riding at one.
            BotMovement.Settle(bot);

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
            bot.RangeHome = SitterRange;
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

            if (Utility.RandomDouble() < 0.35)
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
