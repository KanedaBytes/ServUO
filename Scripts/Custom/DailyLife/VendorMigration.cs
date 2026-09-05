using System;
using System.Collections.Generic;

using Server.Commands;
using Server.Gumps;
using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// Records which upstream spawners daily life has taken over.
    ///
    /// Persisted so the migration is genuinely one-time: it must not re-run on every boot, and
    /// [GG_RestoreVendors must know exactly what to put back rather than guessing from the
    /// config, which may have changed in between.
    /// </summary>
    public sealed class VendorMigrationStore : CustomPersistence
    {
        public static VendorMigrationStore Instance { get; private set; }

        private readonly List<string> _disabled = new List<string>();

        public VendorMigrationStore()
            : base("DailyLifeVendorMigration", 0)
        {
            Instance = this;
        }

        /// <summary>Names of the stock spawners this migration switched off.</summary>
        public IList<string> Disabled
        {
            get { return _disabled.AsReadOnly(); }
        }

        public bool HasMigrated
        {
            get { return _disabled.Count > 0; }
        }

        public void Record(string spawnerName)
        {
            if (!_disabled.Contains(spawnerName))
            {
                _disabled.Add(spawnerName);
            }
        }

        public void Clear()
        {
            _disabled.Clear();
        }

        protected override void Reset()
        {
            _disabled.Clear();
        }

        protected override void SerializeCore(GenericWriter writer)
        {
            writer.Write(_disabled.Count);

            for (int i = 0; i < _disabled.Count; i++)
            {
                writer.Write(_disabled[i]);
            }
        }

        protected override void DeserializeCore(GenericReader reader, int version)
        {
            switch (version)
            {
                case 0:
                    {
                        int count = reader.ReadInt();

                        for (int i = 0; i < count; i++)
                        {
                            _disabled.Add(reader.ReadString());
                        }

                        break;
                    }
            }
        }
    }

    /// <summary>
    /// Hands Britain's shopkeeper spawn points from the stock spawners to daily life's own.
    ///
    /// This is an explicit, confirmed, one-time migration rather than a boot-time mutation of
    /// upstream state, because silently rewriting spawners every time the server starts is the
    /// kind of thing that is impossible to reason about six months later. It is also fully
    /// reversible: nothing is deleted, only switched off, and [GG_RestoreVendors puts it back.
    ///
    /// Spawners are matched BY NAME, scoped to the Britain region on Trammel. Matching by vendor
    /// type instead would take collateral: there is a second Britain jeweler (Vendors#1 at
    /// 1650,1642) that daily life does not manage and must not disturb.
    /// </summary>
    public static class VendorMigration
    {
        private static readonly CustomLogger Log = CustomLogger.For("DailyLife");

        /// <summary>The town region whose spawners this migration is allowed to touch.</summary>
        public const string RegionName = "Britain";

        public static void Configure()
        {
            // Constructed here rather than from a static Configure on the store: that would hide
            // CustomPersistence.Configure, which is what hooks the world events.
            new VendorMigrationStore();
        }

        public static void Initialize()
        {
            CommandSystem.Register("GG_MigrateVendors", AccessLevel.Administrator, Migrate_OnCommand);
            CommandSystem.Register("GGMigrateVendors", AccessLevel.Administrator, Migrate_OnCommand);
            CommandSystem.Register("GG_RestoreVendors", AccessLevel.Administrator, Restore_OnCommand);
            CommandSystem.Register("GGRestoreVendors", AccessLevel.Administrator, Restore_OnCommand);
        }

        // ---- [GG_MigrateVendors ----

        [Usage("GG_MigrateVendors")]
        [Aliases("GGMigrateVendors")]
        [Description("Switches off the stock Britain spawners daily life replaces. One time, reversible.")]
        private static void Migrate_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            VendorMigrationStore store = VendorMigrationStore.Instance;

            if (store == null)
            {
                from.SendMessage(0x35, "The migration store is not available.");
                return;
            }

            if (store.HasMigrated)
            {
                from.SendMessage(0x35, String.Format(
                    "Already migrated: {0} spawner(s) are switched off. [GG_RestoreVendors to undo.",
                    store.Disabled.Count));
                return;
            }

            IList<ShopkeeperConfig> shops = DailyLifeSystem.Config.Shopkeepers;

            if (shops.Count == 0)
            {
                from.SendMessage(0x35, "No shopkeepers are configured; nothing to migrate.");
                return;
            }

            from.CloseGump(typeof(WarningGump));
            from.SendGump(new WarningGump(
                1060635, // <CENTER>WARNING</CENTER>
                30720,
                String.Format(
                    "This will switch off {0} stock Britain spawner(s) and remove the vendors they " +
                    "spawned, so daily life's own shopkeepers can take their place.<br><br>" +
                    "Nothing is deleted and nothing upstream is edited - the spawners are only " +
                    "stopped, and [GG_RestoreVendors puts them back.<br><br>" +
                    "Run [GG_Reimport afterwards to bring in the replacements.<br><br>Proceed?",
                    shops.Count),
                0xFFC000,
                420,
                280,
                new WarningGumpCallback(OnMigrateConfirmed),
                null));
        }

        private static void OnMigrateConfirmed(Mobile from, bool okay, object state)
        {
            if (!okay)
            {
                from.SendMessage("Migration cancelled.");
                return;
            }

            VendorMigrationStore store = VendorMigrationStore.Instance;
            int touched = 0;
            int missing = 0;

            foreach (ShopkeeperConfig shop in DailyLifeSystem.Config.Shopkeepers)
            {
                XmlSpawner spawner = FindSpawner(shop.StockSpawner);

                if (spawner == null)
                {
                    missing++;
                    Log.Warn("Migration: no spawner '{0}' in the {1} region on Trammel.",
                        shop.StockSpawner, RegionName);
                    continue;
                }

                spawner.Running = false;
                spawner.RemoveSpawnObjects();

                store.Record(shop.StockSpawner);
                touched++;

                Log.Info("Migration: switched off '{0}' ({1}) at {2} and removed its spawns.",
                    spawner.Name, spawner.UniqueId, spawner.Location);
            }

            Report(from, String.Format(
                "Migration complete: {0} spawner(s) switched off, {1} not found. Now run [GG_Reimport.",
                touched, missing));

            CommandLogging.WriteLine(
                from,
                String.Format("{0} {1} migrating {2} Britain vendor spawner(s) to daily life",
                    from.AccessLevel, CommandLogging.Format(from), touched));
        }

        // ---- [GG_RestoreVendors ----

        [Usage("GG_RestoreVendors")]
        [Aliases("GGRestoreVendors")]
        [Description("Switches the stock Britain spawners back on and removes the daily-life vendors.")]
        private static void Restore_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            VendorMigrationStore store = VendorMigrationStore.Instance;

            if (store == null || !store.HasMigrated)
            {
                from.SendMessage(0x35, "Nothing has been migrated, so there is nothing to restore.");
                return;
            }

            from.CloseGump(typeof(WarningGump));
            from.SendGump(new WarningGump(
                1060635,
                30720,
                String.Format(
                    "This will switch {0} stock Britain spawner(s) back on and delete the " +
                    "daily-life shopkeepers that replaced them.<br><br>Proceed?",
                    store.Disabled.Count),
                0xFFC000,
                420,
                240,
                new WarningGumpCallback(OnRestoreConfirmed),
                null));
        }

        private static void OnRestoreConfirmed(Mobile from, bool okay, object state)
        {
            if (!okay)
            {
                from.SendMessage("Restore cancelled.");
                return;
            }

            VendorMigrationStore store = VendorMigrationStore.Instance;

            int restored = 0;

            foreach (string name in new List<string>(store.Disabled))
            {
                XmlSpawner spawner = FindSpawner(name);

                if (spawner == null)
                {
                    Log.Warn("Restore: no spawner '{0}' to re-enable.", name);
                    continue;
                }

                spawner.Running = true;
                spawner.Respawn();
                restored++;

                Log.Info("Restore: switched '{0}' back on.", spawner.Name);
            }

            int removed = RemoveDailyLifeSpawners();

            store.Clear();
            Report(from, String.Format(
                "Restore complete: {0} stock spawner(s) back on, {1} daily-life spawner(s) removed.",
                restored, removed));

            CommandLogging.WriteLine(
                from,
                String.Format("{0} {1} restoring {2} Britain vendor spawner(s)",
                    from.AccessLevel, CommandLogging.Format(from), restored));
        }

        /// <summary>Deletes the GG_ spawners this feature owns, which removes their vendors too.</summary>
        private static int RemoveDailyLifeSpawners()
        {
            var doomed = new List<XmlSpawner>();

            foreach (ShopkeeperConfig shop in DailyLifeSystem.Config.Shopkeepers)
            {
                XmlSpawner spawner = FindSpawner(shop.Spawner);

                if (spawner != null && !doomed.Contains(spawner))
                {
                    doomed.Add(spawner);
                }
            }

            foreach (XmlSpawner spawner in doomed)
            {
                // XmlSpawner.OnDelete calls RemoveSpawnObjects, so the vendors go with it.
                spawner.Delete();
            }

            return doomed.Count;
        }

        /// <summary>
        /// Finds a spawner by name inside the Britain region on Trammel.
        ///
        /// Walks World.Items because there is no index of spawners by name - the same
        /// justification GGSpawnCommands.DeleteExisting carries, and this runs only on an
        /// explicit staff command.
        /// </summary>
        private static XmlSpawner FindSpawner(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            foreach (Item item in World.Items.Values)
            {
                var spawner = item as XmlSpawner;

                if (spawner == null || spawner.Deleted || spawner.Map != Map.Trammel)
                {
                    continue;
                }

                if (!Insensitive.Equals(spawner.Name, name))
                {
                    continue;
                }

                Region region = Region.Find(spawner.Location, spawner.Map);

                if (region != null && region.IsPartOf(RegionName))
                {
                    return spawner;
                }
            }

            return null;
        }

        private static void Report(Mobile from, string line)
        {
            Log.Info(line);

            if (from != null)
            {
                from.SendMessage(0x40, line);
            }
        }
    }
}
