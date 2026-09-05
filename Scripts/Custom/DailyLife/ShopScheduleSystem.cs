using System;
using System.Collections.Generic;

using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// Walks the configured shopkeepers home at dusk and back at dawn.
    ///
    /// The vendor is found by its SPAWNER, not by type near a point. The ModernUO original
    /// searched eight tiles from the shop for a vendor of a given type name, which cannot tell
    /// two bakers apart and - worse - cannot find a vendor that has already walked thirty tiles
    /// home, so its shops never actually reopened. Holding the spawner reference fixes both.
    ///
    /// "Closed" is enforced by absence, not by a hard block. The managed vendors grey their own
    /// buy menu after dark (GGVendors.CheckVendorAccess), which is cosmetic: VendorBuyEntry
    /// calls VendorBuy without rechecking, and "vendor buy" speech bypasses the menu entirely.
    /// A player who catches a shopkeeper mid-walk can still trade with them.
    ///
    /// There is deliberately no shop-district region. ServUO's GuardedRegion exposes no
    /// parent-taking constructor (Scripts/Regions/GuardedRegion.cs:22-34), so one laid over
    /// Britain would be unparented and could shadow the town's own guard rules - a real
    /// gameplay risk for a purely cosmetic effect. Putting the check on the six vendors we own
    /// is both narrower and safer.
    /// </summary>
    public static class ShopScheduleSystem
    {
        private static readonly CustomLogger Log = CustomLogger.For("DailyLife");

        /// <summary>Wander radius restored at the shop when the spawner cannot supply one.</summary>
        private const int DefaultShopRange = 5;

        /// <summary>How near a destination counts as being there, for reconciliation.</summary>
        private const int ArrivedRange = 10;

        private static bool _reconcilePending;

        private sealed class ShopWalk
        {
            public ShopkeeperConfig Config;
            public BaseVendor Vendor;
            public NavWalker Walker;
            public bool GoingHome;
        }

        private static readonly List<ShopWalk> _walks = new List<ShopWalk>();

        public static bool ShopsAreClosed
        {
            get { return DayCycleSystem.Current.IsAfterDark(); }
        }

        public static int WalkingCount
        {
            get { return _walks.Count; }
        }

        /// <summary>Which way the last phase application sent the shopkeepers.</summary>
        public static bool LastGoingHome { get; private set; }

        /// <summary>How many configured shopkeepers resolved to a vendor last time.</summary>
        public static int ManagedVendorCount { get; private set; }

        /// <summary>
        /// Counts the configured shopkeepers that currently exist in the world.
        ///
        /// Reported by the DailyLife.Config health check, because "six configured, none in the
        /// world" is the signature of a shard where [GG_MigrateVendors has not been run - and
        /// that is otherwise completely silent.
        /// </summary>
        public static int CountVendorsInWorld()
        {
            int found = 0;

            foreach (ShopkeeperConfig entry in DailyLifeSystem.Config.Shopkeepers)
            {
                if (FindVendor(entry) != null)
                {
                    found++;
                }
            }

            return found;
        }

        public static void Initialize()
        {
            DayCycleSystem.PhaseChanged += OnPhaseChanged;

            // Apply the current phase as an end state rather than walking: a restart at midnight
            // should find the shopkeepers already at home, not strolling there.
            ApplyPhase(DayCycleSystem.Current, true);
        }

        private static void OnPhaseChanged(DayPhase oldPhase, DayPhase newPhase)
        {
            ApplyPhase(newPhase, false);
        }

        /// <summary>
        /// Snaps rather than walks - a reload should put shopkeepers where the new config says
        /// they belong straight away, not send them strolling across town.
        /// </summary>
        public static void Reload()
        {
            ApplyPhase(DayCycleSystem.Current, true);
        }

        private static void ApplyPhase(DayPhase phase, bool snap)
        {
            bool goingHome = phase.IsAfterDark();

            LastGoingHome = goingHome;

            int managed = 0;

            // Cancel anything in flight first, so a dusk transition arriving mid-dawn-walk stops
            // the dawn walk rather than racing it.
            StopAll();

            DailyLifeStore config = DailyLifeSystem.Config;

            foreach (ShopkeeperConfig entry in config.Shopkeepers)
            {
                BaseVendor vendor = FindVendor(entry);

                if (vendor == null)
                {
                    // Normal enough before [GG_MigrateVendors has been run, or if the spawner has
                    // not produced one yet.
                    continue;
                }

                managed++;

                bool closes = entry.Closes;

                string destinationId = goingHome && closes ? entry.Home : entry.Shop;

                if (snap || !goingHome && !closes)
                {
                    Snap(entry, vendor, destinationId, goingHome && closes);
                    continue;
                }

                if (!closes)
                {
                    continue;
                }

                Walk(entry, vendor, destinationId, goingHome);
            }

            ManagedVendorCount = managed;

            Log.Info(
                "Shops {0}: {1} of {2} managed shopkeeper(s) on the move.",
                goingHome ? "closing" : "opening",
                _walks.Count,
                managed);
        }

        /// <summary>
        /// Puts any managed vendor that is in the wrong place for the current phase into the
        /// right one, without disturbing the ones already walking there.
        ///
        /// This is what makes a shopkeeper who appears LATER - a spawner tick, a [GG_Reimport,
        /// a respawn after a wipe - obey a phase that changed before it existed. Without it a
        /// shard that booted at night and imported its vendors afterwards had six shopkeepers
        /// standing in their shops until dawn, because the only thing that ever moved them was
        /// a phase transition they missed.
        /// </summary>
        public static void Reconcile()
        {
            bool goingHome = DayCycleSystem.Current.IsAfterDark();

            int managed = 0;
            int moved = 0;

            foreach (ShopkeeperConfig entry in DailyLifeSystem.Config.Shopkeepers)
            {
                BaseVendor vendor = FindVendor(entry);

                if (vendor == null)
                {
                    continue;
                }

                managed++;

                // Already on its way there under its own steam; leave it walking.
                if (IsWalking(vendor))
                {
                    continue;
                }

                string destinationId = goingHome && entry.Closes ? entry.Home : entry.Shop;

                if (IsAt(vendor, destinationId))
                {
                    continue;
                }

                Snap(entry, vendor, destinationId, goingHome && entry.Closes);
                moved++;
            }

            ManagedVendorCount = managed;
            LastGoingHome = goingHome;

            if (moved > 0)
            {
                Log.Info("Reconciled {0} of {1} shopkeeper(s) to the {2} phase.",
                    moved, managed, DayCycleSystem.Current.ToFriendlyString());
            }
        }

        /// <summary>
        /// Reconciles on the next second, coalescing a burst into one pass.
        ///
        /// [GG_Reimport spawns six vendors at once and each one calls this; debouncing turns
        /// that into a single reconcile rather than six.
        /// </summary>
        public static void ReconcileSoon()
        {
            if (_reconcilePending)
            {
                return;
            }

            _reconcilePending = true;

            Timer.DelayCall(TimeSpan.FromSeconds(1.0), () =>
            {
                _reconcilePending = false;
                Reconcile();
            });
        }

        /// <summary>Managed vendors standing in the wrong place, ignoring any still walking.</summary>
        public static int CountMisplaced()
        {
            bool goingHome = DayCycleSystem.Current.IsAfterDark();
            int misplaced = 0;

            foreach (ShopkeeperConfig entry in DailyLifeSystem.Config.Shopkeepers)
            {
                BaseVendor vendor = FindVendor(entry);

                if (vendor == null || IsWalking(vendor))
                {
                    continue;
                }

                if (!IsAt(vendor, goingHome && entry.Closes ? entry.Home : entry.Shop))
                {
                    misplaced++;
                }
            }

            return misplaced;
        }

        private static bool IsWalking(BaseVendor vendor)
        {
            for (int i = 0; i < _walks.Count; i++)
            {
                if (_walks[i].Vendor == vendor)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Close enough to count as arrived. Generous on purpose: a vendor wandering its
        /// shop-sized radius has not gone anywhere, and re-snapping it every reconcile would
        /// yank it back to one tile.
        /// </summary>
        private static bool IsAt(BaseVendor vendor, string destinationId)
        {
            NavDestination destination = Nav.Destination(destinationId);

            if (destination == null || vendor.Map != destination.Map)
            {
                return false;
            }

            return vendor.InRange(destination.Location, ArrivedRange);
        }

        private static void Snap(ShopkeeperConfig entry, BaseVendor vendor, string destinationId, bool atHome)
        {
            NavDestination destination = Nav.Destination(destinationId);

            if (destination == null || destination.Map == null || destination.Map == Map.Internal)
            {
                return;
            }

            Point3D spot;

            if (!NavArrivals.TryPick(destination, vendor, out spot))
            {
                spot = destination.Location;
            }

            Settle(vendor, spot, destination.Map, atHome);
            vendor.MoveToWorld(spot, destination.Map);
        }

        private static void Walk(ShopkeeperConfig entry, BaseVendor vendor, string destinationId, bool goingHome)
        {
            NavRoute route;
            string error;

            if (!Nav.TryRouteFrom(vendor.Location, vendor.Map, destinationId, vendor, out route, out error))
            {
                Log.Warn("Shopkeeper '{0}' cannot walk to '{1}': {2}. Snapping instead.",
                    entry.Id, destinationId, error);

                Snap(entry, vendor, destinationId, goingHome);
                return;
            }

            var actor = vendor as IDailyLifeActor;

            if (actor == null)
            {
                // A stock vendor moves one step per 30-120 seconds and would still be in the
                // street at dawn. Refusing loudly beats a shopkeeper who never arrives.
                Log.Warn("Shopkeeper '{0}' is a {1}, which is not a daily-life vendor; snapping.",
                    entry.Id, vendor.GetType().Name);

                Snap(entry, vendor, destinationId, goingHome);
                return;
            }

            var walk = new ShopWalk
            {
                Config = entry,
                Vendor = vendor,
                GoingHome = goingHome,
                Walker = new NavWalker(vendor)
            };

            // Point the vendor's own wander at each hop as it is taken, so WalkRandomInHome pulls
            // the same way instead of dragging it back to where it started.
            walk.Walker.KeepHomeAligned = true;
            walk.Walker.Arrived = OnArrived;

            actor.Commuting = true;

            _walks.Add(walk);
            walk.Walker.Follow(route);
        }

        private static void OnArrived(NavWalker walker)
        {
            for (int i = _walks.Count - 1; i >= 0; i--)
            {
                ShopWalk walk = _walks[i];

                if (walk.Walker != walker)
                {
                    continue;
                }

                _walks.RemoveAt(i);

                if (walk.Vendor == null || walk.Vendor.Deleted)
                {
                    return;
                }

                Settle(walk.Vendor, walk.Vendor.Location, walk.Vendor.Map, walk.GoingHome);

                Log.Debug("Shopkeeper '{0}' arrived {1}.",
                    walk.Config.Id, walk.GoingHome ? "home" : "at the shop");
                return;
            }
        }

        /// <summary>
        /// Ends the commute: stock move delay and stock wander come back, and Home is left
        /// pointing at where the vendor actually is.
        /// </summary>
        private static void Settle(BaseVendor vendor, Point3D spot, Map map, bool atHome)
        {
            var actor = vendor as IDailyLifeActor;

            if (actor != null)
            {
                actor.Commuting = false;
            }

            vendor.Home = spot;

            // Pinned at the lodgings overnight; the spawner's own radius behind the counter.
            vendor.RangeHome = atHome ? 0 : GetShopRange(vendor);
        }

        /// <summary>
        /// Restore the daytime wander radius from the spawner that owns the vendor rather than
        /// guessing - for the GG spawners that is the &lt;Range&gt; element.
        /// </summary>
        private static int GetShopRange(BaseVendor vendor)
        {
            ISpawner spawner = vendor.Spawner;

            if (spawner != null && spawner.HomeRange > 0)
            {
                return spawner.HomeRange;
            }

            return DefaultShopRange;
        }

        /// <summary>
        /// Finds the vendor a config entry names, by the spawner that produced it.
        ///
        /// BaseVendor.AllVendors (BaseVendor.cs:40) is a maintained static registry, so this is a
        /// list walk rather than a sector query - and it finds the vendor wherever it currently
        /// is, which a range search around the shop cannot.
        /// </summary>
        private static BaseVendor FindVendor(ShopkeeperConfig entry)
        {
            List<BaseVendor> all = BaseVendor.AllVendors;

            if (all == null)
            {
                return null;
            }

            for (int i = 0; i < all.Count; i++)
            {
                BaseVendor vendor = all[i];

                if (vendor == null || vendor.Deleted)
                {
                    continue;
                }

                var spawner = vendor.Spawner as XmlSpawner;

                if (spawner != null && !spawner.Deleted && Insensitive.Equals(spawner.Name, entry.Spawner))
                {
                    return vendor;
                }
            }

            return null;
        }

        private static void StopAll()
        {
            for (int i = _walks.Count - 1; i >= 0; i--)
            {
                ShopWalk walk = _walks[i];

                if (walk.Walker != null)
                {
                    walk.Walker.Arrived = null;
                    walk.Walker.Stop();
                }

                var actor = walk.Vendor as IDailyLifeActor;

                if (actor != null)
                {
                    actor.Commuting = false;
                }
            }

            _walks.Clear();
        }
    }
}
