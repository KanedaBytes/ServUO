// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotPersonality.cs — per-bot inclination toward different behaviours.
//
// A struct on each bot, rolled once and persisted. The lifecycle manager will
// use it to weight which behaviour a bot transitions into next, so two bots of
// the same class and tier still live visibly different lives.
//
// Nothing consumes it yet - this session has only IdleBehavior. It is rolled and
// serialized now because it is part of the character, and because the save
// format is easier to get right before there is anything to migrate.
//
// Weights need not sum to 1: the lifecycle normalises at roll time.

using System;

namespace Server.Custom
{
    [Flags]
    public enum PersonalityTrait
    {
        None = 0,
        Restless = 1 << 0,  // shorter phases, more transitions
        Homebody = 1 << 1,  // longer phases, fewer transitions
        Brave = 1 << 2,     // adventurer tendency boost
        Cautious = 1 << 3,  // banker tendency boost
        Wealthy = 1 << 4,   // prefers banks and shops
        Rough = 1 << 5,     // prefers wilderness
    }

    public struct BotPersonality
    {
        public double BankerTendency;
        public double AdventurerTendency;
        public double TravelerTendency;
        public double IdleTendency;

        public PersonalityTrait Traits;

        /// <summary>
        /// How long this bot typically stays in one behaviour. Restless halves it, Homebody
        /// doubles it. Also the "has a personality been assigned yet?" flag - a zero duration
        /// means never rolled.
        /// </summary>
        public TimeSpan AveragePhaseDuration;

        public bool IsAssigned
        {
            get { return AveragePhaseDuration > TimeSpan.Zero; }
        }

        public static BotPersonality RollRandom()
        {
            var personality = new BotPersonality
            {
                BankerTendency = Utility.RandomDouble(),
                AdventurerTendency = Utility.RandomDouble(),
                TravelerTendency = Utility.RandomDouble(),

                // Idle is the rarest deliberate choice. A bot standing still should look like a
                // player who stepped away, not like the default state of the system.
                IdleTendency = Utility.RandomDouble() * 0.3,

                AveragePhaseDuration = TimeSpan.FromMinutes(Utility.RandomMinMax(30, 180)),
                Traits = PersonalityTrait.None,
            };

            // Independent low-probability rolls, so most bots carry one or two traits and a few
            // carry none.
            if (Utility.RandomDouble() < 0.20)
            {
                personality.Traits |= PersonalityTrait.Restless;
            }

            if (Utility.RandomDouble() < 0.20)
            {
                personality.Traits |= PersonalityTrait.Homebody;
            }

            if (Utility.RandomDouble() < 0.25)
            {
                personality.Traits |= PersonalityTrait.Brave;
            }

            if (Utility.RandomDouble() < 0.25)
            {
                personality.Traits |= PersonalityTrait.Cautious;
            }

            if (Utility.RandomDouble() < 0.20)
            {
                personality.Traits |= PersonalityTrait.Wealthy;
            }

            if (Utility.RandomDouble() < 0.20)
            {
                personality.Traits |= PersonalityTrait.Rough;
            }

            // Traits modify the tendencies they name. Restless and Homebody can both be rolled,
            // in which case they cancel - which is a reasonable description of an ordinary person.
            if (personality.HasTrait(PersonalityTrait.Brave))
            {
                personality.AdventurerTendency *= 1.5;
            }

            if (personality.HasTrait(PersonalityTrait.Cautious))
            {
                personality.BankerTendency *= 1.5;
            }

            if (personality.HasTrait(PersonalityTrait.Wealthy))
            {
                personality.BankerTendency *= 1.3;
            }

            if (personality.HasTrait(PersonalityTrait.Rough))
            {
                personality.AdventurerTendency *= 1.3;
            }

            if (personality.HasTrait(PersonalityTrait.Restless))
            {
                personality.AveragePhaseDuration =
                    TimeSpan.FromTicks(personality.AveragePhaseDuration.Ticks / 2);
            }

            if (personality.HasTrait(PersonalityTrait.Homebody))
            {
                personality.AveragePhaseDuration =
                    TimeSpan.FromTicks(personality.AveragePhaseDuration.Ticks * 2);
            }

            return personality;
        }

        public bool HasTrait(PersonalityTrait trait)
        {
            return (Traits & trait) != 0;
        }

        public override string ToString()
        {
            return String.Format(
                "B={0:F2} A={1:F2} T={2:F2} I={3:F2} dur={4:F0}m traits={5}",
                BankerTendency,
                AdventurerTendency,
                TravelerTendency,
                IdleTendency,
                AveragePhaseDuration.TotalMinutes,
                Traits);
        }

        // ---- Serialization ----
        //
        // Its own version byte, independent of PlayerBot's, so the personality shape can change
        // without touching the mobile's version ladder.

        public void Write(GenericWriter writer)
        {
            writer.Write((byte)1);
            writer.Write(BankerTendency);
            writer.Write(AdventurerTendency);
            writer.Write(TravelerTendency);
            writer.Write(IdleTendency);
            writer.Write((int)Traits);
            writer.Write(AveragePhaseDuration);
        }

        public static BotPersonality Read(GenericReader reader)
        {
            byte version = reader.ReadByte();

            if (version < 1)
            {
                return default(BotPersonality);
            }

            return new BotPersonality
            {
                BankerTendency = reader.ReadDouble(),
                AdventurerTendency = reader.ReadDouble(),
                TravelerTendency = reader.ReadDouble(),
                IdleTendency = reader.ReadDouble(),
                Traits = (PersonalityTrait)reader.ReadInt(),
                AveragePhaseDuration = reader.ReadTimeSpan(),
            };
        }
    }
}
