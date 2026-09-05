using System;

using Server.Mobiles;

namespace Server.Custom
{
    /*
     * The Britain shopkeepers daily life manages.
     *
     * Each is a one-line subclass of its stock type, so all shop inventory, skills, speech and
     * context menus are inherited untouched. They exist for exactly one reason: a stock
     * BaseVendor cannot walk. VendorAI.TransformMoveDelay (VendorAI.cs:148) returns
     * BaseVendor.GetMoveDelay verbatim - Utility.RandomMinMax(30, 120) SECONDS per step
     * (BaseVendor.cs:74) - and there is no way to override that on an upstream instance.
     * ForcedAI (BaseCreature.cs:3342) is the seam, and DailyLifeAI is what goes through it.
     *
     * Only the types daily life actually touches are subclassed. Every other Britain vendor
     * stays stock.
     *
     * Unlike the patrons, watch and townsfolk, these are NOT ephemeral: they are spawned by
     * Spawns/Custom/trammel/GG_DailyLife.xml and persist like any other vendor. Commuting is
     * deliberately not serialized, so a shard that saves mid-walk comes back with the flag
     * clear and ShopScheduleSystem snaps the vendor to where the phase says it belongs.
     */

    /// <summary>
    /// The one piece of behaviour the six subclasses share. They cannot share a base class -
    /// each derives from a different stock vendor - so the shared rule lives here and each
    /// forwards to it.
    /// </summary>
    public static class DailyLifeVendor
    {
        /// <summary>
        /// Greys the buy and sell menu entries while the shops are shut.
        ///
        /// Cosmetic: VendorBuyEntry.OnClick calls VendorBuy without rechecking this, and the
        /// "vendor buy" speech command bypasses the menu entirely. The real "closed" is the
        /// shopkeeper not being there.
        /// </summary>
        public static bool CheckAccess(BaseVendor vendor, Mobile from)
        {
            if (from == null || from.AccessLevel > AccessLevel.Player)
            {
                return true;
            }

            return !ShopScheduleSystem.ShopsAreClosed;
        }
    }

    public class GGBaker : Baker, IDailyLifeActor
    {
        [Constructable]
        public GGBaker()
        {
        }

        public GGBaker(Serial serial)
            : base(serial)
        {
        }

        protected override BaseAI ForcedAI { get { return new DailyLifeAI(this); } }

        public bool Commuting { get; set; }

        public override bool CheckVendorAccess(Mobile from)
        {
            return DailyLifeVendor.CheckAccess(this, from) && base.CheckVendorAccess(from);
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
        }
    }

    public class GGJeweler : Jeweler, IDailyLifeActor
    {
        [Constructable]
        public GGJeweler()
        {
        }

        public GGJeweler(Serial serial)
            : base(serial)
        {
        }

        protected override BaseAI ForcedAI { get { return new DailyLifeAI(this); } }

        public bool Commuting { get; set; }

        public override bool CheckVendorAccess(Mobile from)
        {
            return DailyLifeVendor.CheckAccess(this, from) && base.CheckVendorAccess(from);
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
        }
    }

    public class GGProvisioner : Provisioner, IDailyLifeActor
    {
        [Constructable]
        public GGProvisioner()
        {
        }

        public GGProvisioner(Serial serial)
            : base(serial)
        {
        }

        protected override BaseAI ForcedAI { get { return new DailyLifeAI(this); } }

        public bool Commuting { get; set; }

        public override bool CheckVendorAccess(Mobile from)
        {
            return DailyLifeVendor.CheckAccess(this, from) && base.CheckVendorAccess(from);
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
        }
    }

    public class GGButcher : Butcher, IDailyLifeActor
    {
        [Constructable]
        public GGButcher()
        {
        }

        public GGButcher(Serial serial)
            : base(serial)
        {
        }

        protected override BaseAI ForcedAI { get { return new DailyLifeAI(this); } }

        public bool Commuting { get; set; }

        public override bool CheckVendorAccess(Mobile from)
        {
            return DailyLifeVendor.CheckAccess(this, from) && base.CheckVendorAccess(from);
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
        }
    }

    public class GGBowyer : Bowyer, IDailyLifeActor
    {
        [Constructable]
        public GGBowyer()
        {
        }

        public GGBowyer(Serial serial)
            : base(serial)
        {
        }

        protected override BaseAI ForcedAI { get { return new DailyLifeAI(this); } }

        public bool Commuting { get; set; }

        public override bool CheckVendorAccess(Mobile from)
        {
            return DailyLifeVendor.CheckAccess(this, from) && base.CheckVendorAccess(from);
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
        }
    }

    public class GGCarpenter : Carpenter, IDailyLifeActor
    {
        [Constructable]
        public GGCarpenter()
        {
        }

        public GGCarpenter(Serial serial)
            : base(serial)
        {
        }

        protected override BaseAI ForcedAI { get { return new DailyLifeAI(this); } }

        public bool Commuting { get; set; }

        public override bool CheckVendorAccess(Mobile from)
        {
            return DailyLifeVendor.CheckAccess(this, from) && base.CheckVendorAccess(from);
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
        }
    }
}
