using System;

using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// A tavern drinker.
    ///
    /// Deliberately ephemeral: patrons are created at dusk and deleted at dawn, so nothing about
    /// them needs persisting. If a world save catches them mid-evening they delete themselves on
    /// the next load and the scheduler recreates them if the phase still calls for it - which
    /// also means a restart can never leave a crowd of orphans behind.
    ///
    /// Patrons never commute: they mill about their arrival spot on the stock wander, which is
    /// exactly what Home and RangeHome are for.
    /// </summary>
    public class DailyLifePatron : BaseCreature, IDailyLifeActor
    {
        private static readonly TimeSpan MinChatterDelay = TimeSpan.FromSeconds(25.0);
        private static readonly TimeSpan MaxChatterDelay = TimeSpan.FromSeconds(60.0);

        private long _nextChatter;

        public DailyLifePatron()
            : base(AIType.AI_Vendor, FightMode.None, 2, 1, 0.5, 2.0)
        {
            Race = Race.Human;
            Female = Utility.RandomBool();
            Body = Female ? 0x191 : 0x190;
            Hue = Race.RandomSkinHue();
            Title = "the patron";

            InitStats(60, 60, 25);

            Name = NameList.RandomName(Female ? "female" : "male");

            Utility.AssignRandomHair(this);

            AddItem(new Backpack());
            AddItem(new Shoes(Utility.RandomNeutralHue()));

            if (Female)
            {
                AddItem(new Skirt(Utility.RandomNeutralHue()));
                AddItem(new FancyShirt(Utility.RandomNeutralHue()));
            }
            else
            {
                AddItem(new LongPants(Utility.RandomNeutralHue()));
                AddItem(new Shirt(Utility.RandomNeutralHue()));
            }

            ScheduleNextChatter();

            LiveRegistry.Register(this);
        }

        public DailyLifePatron(Serial serial)
            : base(serial)
        {
        }

        protected override BaseAI ForcedAI
        {
            get { return new DailyLifeAI(this); }
        }

        /// <summary>Always false - a patron is never steered by a NavWalker.</summary>
        public bool Commuting { get; set; }

        public override bool IsInvulnerable
        {
            get { return true; }
        }

        public override bool ClickTitle
        {
            get { return false; }
        }


        public override void OnThink()
        {
            base.OnThink();

            if (Core.TickCount - _nextChatter < 0)
            {
                return;
            }

            ScheduleNextChatter();
            Chatter();
        }

        private void ScheduleNextChatter()
        {
            _nextChatter = Core.TickCount + Utility.RandomMinMax(
                (int)MinChatterDelay.TotalMilliseconds,
                (int)MaxChatterDelay.TotalMilliseconds);
        }

        /// <summary>
        /// Lines are read live from the config, so a reload changes what the existing crowd says
        /// without respawning it.
        /// </summary>
        private void Chatter()
        {
            string line = DailyLifeSystem.RandomTavernLine();

            if (line != null)
            {
                Say(line);
            }
        }

        public override void OnDelete()
        {
            LiveRegistry.Unregister(this);

            base.OnDelete();
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
}
