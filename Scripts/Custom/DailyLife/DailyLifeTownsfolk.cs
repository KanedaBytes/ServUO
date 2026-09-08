using System;

using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// A townsperson who walks a nav route for ever.
    ///
    /// EPHEMERAL BY DESIGN: it deletes itself on world load and the owning system recreates it
    /// from config. That keeps the JSON the single source of truth and makes duplicate-on-restart
    /// impossible.
    ///
    /// It owns no pathing. A NavWalker steers it, and looping is just re-following the same lap
    /// when the walker reports arrival - the lap already contains its own return leg.
    /// </summary>
    public class DailyLifeTownsfolk : BaseCreature, IDailyLifeActor
    {
        private NavWalker _walker;
        private NavRoute _lap;

        public DailyLifeTownsfolk()
            : base(AIType.AI_Vendor, FightMode.None, 2, 1, 0.5, 2.0)
        {
            Race = Race.Human;
            Female = Utility.RandomBool();
            Body = Female ? 0x191 : 0x190;
            Hue = Race.RandomSkinHue();

            InitStats(70, 70, 25);

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

            LiveRegistry.Register(this);
        }

        public DailyLifeTownsfolk(Serial serial)
            : base(serial)
        {
        }

        // Registered from the live constructor only. These delete themselves on world load, so
        // registering from Deserialize would put a corpse in the live map for one tick.

        /// <summary>
        /// The AI that stands aside while NavWalker steers. See DailyLifeAI - without it the
        /// wander tick fights the route for every step.
        /// </summary>
        protected override BaseAI ForcedAI
        {
            get { return new DailyLifeAI(this); }
        }

        public bool Commuting { get; set; }

        /// <summary>
        /// A bot walks through this actor, as a player would. Without it the courier on his round
        /// is a wall to every bot behind him on a one-tile road - four of five walk-probe bots
        /// wedged at 1450,1683 that way. See BotShove for the rule and what a bot still yields to.
        /// </summary>
        public override bool OnMoveOver(Mobile m)
        {
            return BotShove.OnMoveOver(this, m) ?? base.OnMoveOver(m);
        }

        public override bool IsInvulnerable
        {
            get { return true; }
        }

        public override bool ClickTitle
        {
            get { return false; }
        }


        /// <summary>
        /// Applies the body a config entry asked for. "random" leaves the constructor's roll
        /// alone.
        ///
        /// The name is re-rolled when the sex changes, or a townsperson configured as male
        /// would keep the female name the constructor happened to pick. A config that supplies
        /// its own name overwrites this afterwards either way.
        /// </summary>
        public void ApplyBody(string body)
        {
            bool female;

            if (Insensitive.Equals(body, "male"))
            {
                female = false;
            }
            else if (Insensitive.Equals(body, "female"))
            {
                female = true;
            }
            else
            {
                return;
            }

            if (Female == female)
            {
                return;
            }

            Female = female;
            Body = female ? 0x191 : 0x190;
            Name = NameList.RandomName(female ? "female" : "male");
        }

        /// <summary>
        /// Walks a lap for ever. The lap is re-followed on arrival rather than rebuilt, so a
        /// patrol costs one route computation no matter how long it runs.
        /// </summary>
        public void FollowLap(NavRoute lap)
        {
            if (lap == null || lap.Count == 0)
            {
                return;
            }

            _lap = lap;

            if (_walker == null)
            {
                _walker = new NavWalker(this);
                _walker.Arrived = OnLapFinished;
            }

            Commuting = true;
            _walker.Follow(lap);
        }

        /// <summary>Stands still, wandering around Home like any other creature.</summary>
        public void StopWalking()
        {
            _lap = null;
            Commuting = false;

            if (_walker != null)
            {
                _walker.Stop();
            }
        }

        private void OnLapFinished(NavWalker walker)
        {
            if (_lap == null || Deleted || Map == null || Map == Map.Internal)
            {
                Commuting = false;
                return;
            }

            walker.Follow(_lap);
        }

        public override void OnDelete()
        {
            StopWalking();
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

            // ServUO has no [AfterDeserialization]; this is the equivalent, and it is what makes
            // the actor ephemeral across a restart.
            Timer.DelayCall(Delete);
        }
    }
}
