using System;

using Server.Mobiles;

namespace Server.Custom
{
    /*
     * The Britain shopkeepers daily life manages.
     *
     * Each subclasses its stock type, so all shop inventory, skills, speech and context menus are
     * inherited untouched. They EXIST for exactly one reason: a stock BaseVendor cannot walk.
     * VendorAI.TransformMoveDelay (VendorAI.cs:148) returns BaseVendor.GetMoveDelay verbatim -
     * Utility.RandomMinMax(30, 120) SECONDS per step (BaseVendor.cs:74) - and there is no way to
     * override that on an upstream instance. ForcedAI (BaseCreature.cs:3342) is the seam, and
     * DailyLifeAI is what goes through it.
     *
     * THEY ARE NO LONGER ONE-LINE SUBCLASSES, which is what this comment called them for four
     * sessions after it stopped being true (REVIEW.md section 11). Each now carries six overrides,
     * and a reader who trusts the old description will not look for any of them:
     *
     *   ForcedAI            the original reason above
     *   OnMoveOver          forwards to BotShove, so a bot walks through a shopkeeper as a player
     *                       would - without this a shopkeeper walking home at dusk stopped dead at
     *                       a bot standing in its doorway
     *   CheckVendorAccess   greys buy and sell while the shops are shut (cosmetic; see CheckAccess)
     *   OnAfterSpawn        registers on the live map and asks the schedule to reconcile
     *   OnDelete            unregisters
     *   Deserialize         re-registers, because OnAfterSpawn does not fire on load and these
     *                       persist - without it the live map loses every shopkeeper on a restart
     *
     * The shared halves live in DailyLifeVendor below, because the six cannot share a base class:
     * each derives from a different stock vendor.
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

        /// <summary>
        /// A managed shopkeeper has just been spawned, so the schedule has to catch it up.
        ///
        /// Deferred and debounced rather than reconciled here: XmlSpawner sets Mobile.Spawner
        /// around the same time it calls this, and the vendor is found BY its spawner, so
        /// reconciling on this tick can miss it. Waiting a second also turns a [GG_Reimport
        /// that spawns six at once into one reconcile.
        /// </summary>
        public static void OnSpawned(Mobile vendor)
        {
            LiveRegistry.Register(vendor);
            ShopScheduleSystem.ReconcileSoon();
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

        /// <summary>A bot walks through this shopkeeper as a player would; see BotShove.</summary>
        public override bool OnMoveOver(Mobile m)
        {
            return BotShove.OnMoveOverActor(this, m) ?? base.OnMoveOver(m);
        }

        public override bool CheckVendorAccess(Mobile from)
        {
            return DailyLifeVendor.CheckAccess(this, from) && base.CheckVendorAccess(from);
        }

        public override void OnAfterSpawn()
        {
            base.OnAfterSpawn();
            DailyLifeVendor.OnSpawned(this);
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

            // OnAfterSpawn does not fire on load, and these persist, so the live map would lose
            // every shopkeeper across a restart without this.
            LiveRegistry.Register(this);
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

        /// <summary>A bot walks through this shopkeeper as a player would; see BotShove.</summary>
        public override bool OnMoveOver(Mobile m)
        {
            return BotShove.OnMoveOverActor(this, m) ?? base.OnMoveOver(m);
        }

        public override bool CheckVendorAccess(Mobile from)
        {
            return DailyLifeVendor.CheckAccess(this, from) && base.CheckVendorAccess(from);
        }

        public override void OnAfterSpawn()
        {
            base.OnAfterSpawn();
            DailyLifeVendor.OnSpawned(this);
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

            // OnAfterSpawn does not fire on load, and these persist, so the live map would lose
            // every shopkeeper across a restart without this.
            LiveRegistry.Register(this);
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

        /// <summary>A bot walks through this shopkeeper as a player would; see BotShove.</summary>
        public override bool OnMoveOver(Mobile m)
        {
            return BotShove.OnMoveOverActor(this, m) ?? base.OnMoveOver(m);
        }

        public override bool CheckVendorAccess(Mobile from)
        {
            return DailyLifeVendor.CheckAccess(this, from) && base.CheckVendorAccess(from);
        }

        public override void OnAfterSpawn()
        {
            base.OnAfterSpawn();
            DailyLifeVendor.OnSpawned(this);
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

            // OnAfterSpawn does not fire on load, and these persist, so the live map would lose
            // every shopkeeper across a restart without this.
            LiveRegistry.Register(this);
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

        /// <summary>A bot walks through this shopkeeper as a player would; see BotShove.</summary>
        public override bool OnMoveOver(Mobile m)
        {
            return BotShove.OnMoveOverActor(this, m) ?? base.OnMoveOver(m);
        }

        public override bool CheckVendorAccess(Mobile from)
        {
            return DailyLifeVendor.CheckAccess(this, from) && base.CheckVendorAccess(from);
        }

        public override void OnAfterSpawn()
        {
            base.OnAfterSpawn();
            DailyLifeVendor.OnSpawned(this);
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

            // OnAfterSpawn does not fire on load, and these persist, so the live map would lose
            // every shopkeeper across a restart without this.
            LiveRegistry.Register(this);
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

        /// <summary>A bot walks through this shopkeeper as a player would; see BotShove.</summary>
        public override bool OnMoveOver(Mobile m)
        {
            return BotShove.OnMoveOverActor(this, m) ?? base.OnMoveOver(m);
        }

        public override bool CheckVendorAccess(Mobile from)
        {
            return DailyLifeVendor.CheckAccess(this, from) && base.CheckVendorAccess(from);
        }

        public override void OnAfterSpawn()
        {
            base.OnAfterSpawn();
            DailyLifeVendor.OnSpawned(this);
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

            // OnAfterSpawn does not fire on load, and these persist, so the live map would lose
            // every shopkeeper across a restart without this.
            LiveRegistry.Register(this);
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

        /// <summary>A bot walks through this shopkeeper as a player would; see BotShove.</summary>
        public override bool OnMoveOver(Mobile m)
        {
            return BotShove.OnMoveOverActor(this, m) ?? base.OnMoveOver(m);
        }

        public override bool CheckVendorAccess(Mobile from)
        {
            return DailyLifeVendor.CheckAccess(this, from) && base.CheckVendorAccess(from);
        }

        public override void OnAfterSpawn()
        {
            base.OnAfterSpawn();
            DailyLifeVendor.OnSpawned(this);
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

            // OnAfterSpawn does not fire on load, and these persist, so the live map would lose
            // every shopkeeper across a restart without this.
            LiveRegistry.Register(this);
        }
    }
}
