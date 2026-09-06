// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotPackAnimal.cs — the llama trailing the miner.
//
// A gatherer's beast doubles what a shift brings home, and it is the thing that
// makes the loop read as a person's errand rather than a spawn timer. It is also
// the most self-contained piece of upstream's economy layer: every API it needs
// is typed on Mobile, not PlayerMobile, so it ports with no shim at all.
//
//   beast.SetControlMaster(bot)    BaseCreature.cs:6613 — takes a plain Mobile;
//                                  its one gate is follower slots, and every bit
//                                  of PlayerMobile bookkeeping inside is behind
//                                  an `is PlayerMobile` guard a bot simply skips.
//   ControlOrder = Follow          BaseAI.DoOrderFollow reads ControlTarget and
//                                  nothing else. No NetState, no player.
//   beast.Backpack.DropItem(...)   a StrongBackpack, Movable = false.
//
// WHAT IS DELIBERATELY NOT HERE — the stable round trip.
//
// Upstream walked the beast to a stables after a delivery and stabled it, then
// reclaimed it next shift. That half is not contained: AnimalTrainer.EndStable
// hard-codes a 30gp fee taken from the pack or the bank (economy, which is 7f),
// DoClaim is private and would need reimplementing, and there is no `stables`
// destination in navigation.json to walk to. Upstream itself deletes the beast
// outright when no stables is within 300 tiles, so taking that path is following
// their code rather than inventing a shortcut.
//
// SEAM, restored by the economy session (7f): add a `stables` destination at
// their Britain Stables coordinate (1393,1645, just outside our west edge), give
// the bot the 30gp, and reinstate the detour.

using System;
using System.Collections.Generic;

using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// A gatherer's pack horse. Subclassed rather than used directly for exactly two reasons:
    /// the orphan reaper needs somewhere to live, and the stray sweep needs a type to look for.
    /// </summary>
    public class BotPackHorse : PackHorse
    {
        private DateTime _nextOwnerCheck;

        [Constructable]
        public BotPackHorse()
        {
        }

        public BotPackHorse(Serial serial)
            : base(serial)
        {
        }

        public override void OnThink()
        {
            base.OnThink();

            BotPackAnimals.ReapIfOrphaned(this, ref _nextOwnerCheck);
        }

        public static void Initialize()
        {
            BotPackAnimals.SweepStrays<BotPackHorse>();
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(0); // version
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            int version = reader.ReadInt();

            // Ephemeral, exactly as PlayerBot is: a beast whose owner will delete itself on load
            // has nothing to carry. ServUO has no [AfterDeserialization], so this is the idiom.
            Timer.DelayCall(Delete);
        }
    }

    public class BotPackLlama : PackLlama
    {
        private DateTime _nextOwnerCheck;

        [Constructable]
        public BotPackLlama()
        {
        }

        public BotPackLlama(Serial serial)
            : base(serial)
        {
        }

        public override void OnThink()
        {
            base.OnThink();

            BotPackAnimals.ReapIfOrphaned(this, ref _nextOwnerCheck);
        }

        public static void Initialize()
        {
            BotPackAnimals.SweepStrays<BotPackLlama>();
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(0); // version
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            int version = reader.ReadInt();

            Timer.DelayCall(Delete);
        }
    }

    public static class BotPackAnimals
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>Beasts released after a delivery, since boot. Reported by Bots.Work.</summary>
        public static int Released { get; private set; }

        /// <summary>Beasts reaped because their owner went away. Reported by Bots.Work.</summary>
        public static int Reaped { get; private set; }

        // Upstream's list, kept whole: the names are half the charm of the thing.
        private static readonly string[] BeastNames =
        {
            "Bessie", "Daisy", "Clyde", "Buck", "Maple", "Nutmeg",
            "Biscuit", "Juniper", "Star", "Willow", "Chester", "Rosie",
            "Patches", "Dusty", "Hazel", "Bramble",
        };

        /// <summary>
        /// Give this gatherer a beast, or hand back the one it already has.
        ///
        /// Upstream's species split is kept: a miner is likelier to bring a llama, a lumberjack a
        /// horse. It is pure flavour and costs nothing to keep.
        /// </summary>
        public static BaseCreature SpawnFor(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return null;
            }

            if (bot.PackAnimal != null && !bot.PackAnimal.Deleted)
            {
                return bot.PackAnimal;
            }

            bool llama = bot.Class == BotClass.Miner
                ? Utility.RandomDouble() < 0.60
                : Utility.RandomDouble() < 0.30;

            BaseCreature beast = llama ? (BaseCreature)new BotPackLlama() : new BotPackHorse();

            beast.Name = BeastNames[Utility.Random(BeastNames.Length)];
            beast.MoveToWorld(new Point3D(bot.X + 1, bot.Y + 1, bot.Z), bot.Map);

            if (!beast.SetControlMaster(bot))
            {
                // The only way this fails is follower slots, and a bot has five. Refuse quietly
                // rather than leaving an ownerless beast standing in a field.
                beast.Delete();
                return null;
            }

            beast.ControlTarget = bot;
            beast.ControlOrder = OrderType.Follow;

            bot.PackAnimal = beast;

            return beast;
        }

        /// <summary>
        /// Send the beast away after a delivery.
        ///
        /// Deleting it IS upstream's behaviour when there is no stables in range, and it is the
        /// only ending that leaves nothing behind. See the seam note at the top of the file.
        /// </summary>
        public static void Release(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }

            BaseCreature beast = bot.PackAnimal;

            bot.PackAnimal = null;

            if (beast == null || beast.Deleted)
            {
                return;
            }

            // Anything still in the panniers goes with it. The delivery hook empties them first,
            // so this is a guard rather than a discard.
            Released++;
            beast.Delete();
        }

        /// <summary>
        /// Delete a beast whose owner is gone, checked at most every ten seconds.
        ///
        /// Upstream's rule, including the exception that matters: a DEAD master keeps its llama
        /// waiting by the corpse. Only a master that is deleted or off-facet orphans it.
        /// </summary>
        public static void ReapIfOrphaned(BaseCreature beast, ref DateTime nextCheck)
        {
            if (beast == null || beast.Deleted)
            {
                return;
            }

            if (CustomTime.Now < nextCheck)
            {
                return;
            }

            nextCheck = CustomTime.Now + TimeSpan.FromSeconds(10.0);

            Mobile master = beast.ControlMaster;

            if (master != null && !master.Deleted && master.Map != Map.Internal)
            {
                return;
            }

            Reaped++;
            beast.Delete();
        }

        /// <summary>
        /// Delete every beast of this type left standing at world load.
        ///
        /// Belt and braces beside the ephemeral Deserialize above: that handles a beast that was
        /// saved, this handles one whose owner vanished between the save and the load, and
        /// neither costs anything on a clean boot.
        /// </summary>
        public static void SweepStrays<T>() where T : BaseCreature
        {
            var strays = new List<Mobile>();

            // Justified World.Mobiles walk (CLAUDE.md section 15): this runs once, at Initialize,
            // and there is no registry of beasts to consult - that is precisely what makes one a
            // stray. LiveRegistry holds bots, not their animals.
            foreach (Mobile mobile in World.Mobiles.Values)
            {
                if (mobile is T && !mobile.Deleted)
                {
                    strays.Add(mobile);
                }
            }

            if (strays.Count == 0)
            {
                return;
            }

            foreach (Mobile stray in strays)
            {
                stray.Delete();
            }

            Log.Info("Swept {0} stray {1}(s) left over from a previous run.", strays.Count, typeof(T).Name);
        }

        /// <summary>Everything in a beast's panniers, for the delivery hook.</summary>
        public static Container PanniersOf(PlayerBot bot)
        {
            BaseCreature beast = bot == null ? null : bot.PackAnimal;

            return beast == null || beast.Deleted ? null : beast.Backpack;
        }

        /// <summary>How many beasts are alive right now, for the health line.</summary>
        public static int LiveCount()
        {
            int count = 0;

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot != null && bot.PackAnimal != null && !bot.PackAnimal.Deleted)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
