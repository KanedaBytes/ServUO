using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// Loads Data/Custom/britain-daily-life.json and owns the live town config.
    ///
    /// GAME THREAD ONLY, like the navigation layer it sits on.
    ///
    /// Failure contract, matching NavigationSystem and RestrictedZoneSystem: on ANY load failure
    /// the live config is kept, because a bad edit must never leave the town empty with nothing
    /// but a "loaded" line in the log. Structural problems fail the load; dangling NAV ids are
    /// collected and reported by the DailyLife.Config health check instead.
    /// </summary>
    public static class DailyLifeSystem
    {
        private static readonly CustomLogger Log = CustomLogger.For("DailyLife");

        public const string ConfigPath = "Data/Custom/britain-daily-life.json";

        public const int SchemaVersion = 1;

        private static DailyLifeStore _store = new DailyLifeStore();

        private static readonly List<string> _configWarnings = new List<string>();

        private static string _lastError;
        private static DateTime? _lastLoadUtc;

        public static DailyLifeStore Config
        {
            get { return _store; }
        }

        public static string LastError
        {
            get { return _lastError; }
        }

        public static IList<string> ConfigWarnings
        {
            get { return _configWarnings.AsReadOnly(); }
        }

        // ---- boot ----

        /// <summary>
        /// CallPriority 110 puts this above NavigationSystem.Configure (100), because every
        /// location in this config is a nav id and the graph must exist before they can be
        /// resolved.
        /// </summary>
        [CallPriority(110)]
        public static void Configure()
        {
            string error;

            if (!TryLoad(out error))
            {
                Log.Error("Daily life is INACTIVE - {0} was not loaded: {1}", ConfigPath, error);
                return;
            }

            LogWarnings();
        }

        /// <summary>
        /// CallPriority -5: after DayCycleSystem settles the phase (-10) and before the tavern,
        /// watch, townsfolk and shop systems apply it (untagged, so 0).
        /// </summary>
        [CallPriority(-5)]
        public static void Initialize()
        {
            HealthCheck.Register("DailyLife.Config", BuildHealthResult);
        }

        // ---- loading ----

        public static bool TryLoad(out string error)
        {
            IList<string> ignored;
            return TryLoad(out error, out ignored);
        }

        /// <summary>As TryLoad, keeping the validator's errors as a list. See NavigationSystem.</summary>
        public static bool TryLoad(out string error, out IList<string> errors)
        {
            DailyLifeStore store;

            if (!JsonConfig.TryLoad(ConfigPath, out store, out errors))
            {
                error = String.Join("; ", ToArray(errors));
                _lastError = error;
                return false;
            }

            // Only now is it safe to swap.
            _store = store;

            Rebuild();

            error = null;
            _lastError = null;
            _lastLoadUtc = DateTime.UtcNow;

            return true;
        }

        /// <summary>Single entry point for the staff command and, later, the admin API.</summary>
        public static bool TryReload(out string error)
        {
            IList<string> ignored;
            return TryReload(out error, out ignored);
        }

        /// <summary>As TryReload, keeping the validator's errors as a list.</summary>
        public static bool TryReload(out string error, out IList<string> errors)
        {
            if (!TryLoad(out error, out errors))
            {
                NotifyStaff(String.Format("Daily life config NOT reloaded: {0}", error));
                Log.Error("Daily life config NOT reloaded: {0}", error);
                return false;
            }

            Log.Info(
                "Reloaded daily life: {0} shopkeeper(s), {1} watch post(s), {2} townsfolk, {3} warning(s).",
                _store.Shopkeepers.Count,
                _store.Watch.Count,
                _store.Townsfolk.Count,
                _configWarnings.Count);

            LogWarnings();
            return true;
        }

        private static void Rebuild()
        {
            _configWarnings.Clear();

            foreach (WatchPostConfig post in _store.Watch)
            {
                post.Bind();
            }

            CheckReferences();
            RunCanary();
        }

        /// <summary>Every nav id in the config must name something that exists.</summary>
        private static void CheckReferences()
        {
            RequireLocation("anchor", _store.Anchor);

            if (_store.Tavern != null)
            {
                NavDestination tavern = Nav.Destination(_store.Tavern.Destination);

                if (tavern == null)
                {
                    Warn("tavern names unknown destination '{0}'", _store.Tavern.Destination);
                }
                else if (tavern.ArrivalList.Count == 0)
                {
                    Warn("tavern destination '{0}' has no arrival points", tavern.Id);
                }
            }

            foreach (WatchPostConfig post in _store.Watch)
            {
                if (post.IsPatrolRoute)
                {
                    if (Nav.RouteDefinition(post.Route) == null)
                    {
                        Warn("watch post '{0}' names unknown or unusable route '{1}'", post.Id, post.Route);
                    }

                    continue;
                }

                foreach (string id in post.DestinationList)
                {
                    if (Nav.Destination(id) == null)
                    {
                        Warn("watch post '{0}' names unknown destination '{1}'", post.Id, id);
                    }
                }
            }

            foreach (TownsfolkConfig entry in _store.Townsfolk)
            {
                if (Nav.RouteDefinition(entry.Route) == null)
                {
                    Warn("townsfolk '{0}' names unknown or unusable route '{1}'", entry.Id, entry.Route);
                }
            }

            foreach (ShopkeeperConfig shop in _store.Shopkeepers)
            {
                if (Nav.Destination(shop.Shop) == null)
                {
                    Warn("shopkeeper '{0}' names unknown shop destination '{1}'", shop.Id, shop.Shop);
                }

                if (Nav.Destination(shop.Home) == null)
                {
                    Warn("shopkeeper '{0}' names unknown home destination '{1}'", shop.Id, shop.Home);
                }
            }
        }

        /// <summary>
        /// The boot canary: every actor's scheduled destinations must actually be routable.
        ///
        /// This is the selfTests idea from the navigation layer applied to actors, and it is what
        /// makes the whole feature verifiable with no client - a shopkeeper who cannot reach his
        /// own house says so at boot rather than by standing in the street all night.
        /// </summary>
        private static void RunCanary()
        {
            NavRoute route;
            string error;

            foreach (ShopkeeperConfig shop in _store.Shopkeepers)
            {
                if (!shop.Closes)
                {
                    continue;
                }

                if (!Nav.TryRoute(shop.Shop, shop.Home, out route, out error))
                {
                    Warn("shopkeeper '{0}' cannot reach '{1}' from '{2}': {3}",
                        shop.Id, shop.Home, shop.Shop, error);
                }

                if (!Nav.TryRoute(shop.Home, shop.Shop, out route, out error))
                {
                    Warn("shopkeeper '{0}' cannot return to '{1}' from '{2}': {3}",
                        shop.Id, shop.Shop, shop.Home, error);
                }
            }

            foreach (WatchPostConfig post in _store.Watch)
            {
                if (post.IsPatrolRoute)
                {
                    if (!Nav.TryBuildRouteLap(post.Route, out route, out error))
                    {
                        Warn("watch post '{0}' cannot walk route '{1}': {2}", post.Id, post.Route, error);
                    }

                    continue;
                }

                if (post.DestinationList.Length == 1)
                {
                    NavDestination only = Nav.Destination(post.DestinationList[0]);

                    if (only != null && only.ArrivalList.Count == 0)
                    {
                        Warn("watch post '{0}' destination '{1}' has no arrival points",
                            post.Id, only.Id);
                    }

                    continue;
                }

                if (!Nav.TryBuildPatrol(post.DestinationList, out route, out error))
                {
                    Warn("watch post '{0}' cannot patrol its destinations: {1}", post.Id, error);
                }
            }

            foreach (TownsfolkConfig entry in _store.Townsfolk)
            {
                if (!Nav.TryBuildRouteLap(entry.Route, out route, out error))
                {
                    Warn("townsfolk '{0}' cannot walk route '{1}': {2}", entry.Id, entry.Route, error);
                }
            }
        }

        private static void RequireLocation(string what, string navId)
        {
            Point3D location;
            Map map;

            if (!TryResolveLocation(navId, out location, out map))
            {
                Warn("{0} names unknown nav id '{1}'", what, navId);
            }
        }

        private static void Warn(string format, params object[] args)
        {
            try
            {
                _configWarnings.Add(String.Format(format, args));
            }
            catch
            {
                _configWarnings.Add(format);
            }
        }

        // ---- lookup ----

        /// <summary>Resolves a nav id as a destination first, then as a waypoint.</summary>
        public static bool TryResolveLocation(string navId, out Point3D location, out Map map)
        {
            location = Point3D.Zero;
            map = null;

            NavDestination destination = Nav.Destination(navId);

            if (destination != null)
            {
                location = destination.Location;
                map = destination.Map;
                return map != null;
            }

            NavWaypoint waypoint = Nav.Waypoint(navId);

            if (waypoint != null)
            {
                location = waypoint.Location;
                map = waypoint.Map;
                return map != null;
            }

            return false;
        }

        public static bool TryGetAnchor(out Point3D location, out Map map)
        {
            return TryResolveLocation(_store.Anchor, out location, out map);
        }

        public static string RandomTavernLine()
        {
            return RandomLine(_store.Chatter);
        }

        public static string RandomWatchLine()
        {
            return RandomLine(_store.WatchChatter);
        }

        private static string RandomLine(List<string> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                return null;
            }

            return lines[Utility.Random(lines.Count)];
        }

        // ---- health ----

        public static HealthResult BuildHealthResult()
        {
            string loaded = _lastLoadUtc.HasValue
                ? _lastLoadUtc.Value.ToString("HH:mm:ss") + "Z"
                : "never";

            if (_lastError != null)
            {
                return HealthResult.Fail(String.Format(
                    "{0} did not load: {1}. The town is inactive; last good load {2}.",
                    ConfigPath,
                    _lastError,
                    loaded));
            }

            int inWorld = ShopScheduleSystem.CountVendorsInWorld();

            string counts = String.Format(
                "{0} shopkeeper(s) ({1} in world), {2} watch post(s), {3} townsfolk, {4} patron(s) wanted, phase {5}, last load {6}",
                _store.Shopkeepers.Count,
                inWorld,
                _store.Watch.Count,
                _store.Townsfolk.Count,
                _store.Tavern != null ? _store.Tavern.PatronCount : 0,
                DayCycleSystem.Current.ToFriendlyString(),
                loaded);

            // Configured but absent is the signature of a shard that has not been migrated.
            if (_configWarnings.Count == 0 && _store.Shopkeepers.Count > 0 && inWorld == 0)
            {
                return HealthResult.Warn(
                    "no managed shopkeepers in the world - run [GG_MigrateVendors then [GG_Reimport. " + counts);
            }

            if (_configWarnings.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "{0} config warning(s) - first: {1}. {2}",
                    _configWarnings.Count,
                    _configWarnings[0],
                    counts));
            }

            return HealthResult.Ok(counts);
        }

        private static void LogWarnings()
        {
            if (_configWarnings.Count == 0)
            {
                return;
            }

            Log.Warn("{0} daily life config warning(s):", _configWarnings.Count);

            for (int i = 0; i < _configWarnings.Count && i < 40; i++)
            {
                Log.Warn("  " + _configWarnings[i]);
            }

            if (_configWarnings.Count > 40)
            {
                Log.Warn("  ... and {0} more.", _configWarnings.Count - 40);
            }
        }

        // ---- helpers ----

        /// <summary>
        /// Third private copy on the shard (see RestrictedZoneSystem and NavigationSystem).
        /// SHARD.md carries the note that a fourth should become a Custom/Core helper.
        /// </summary>
        public static void NotifyStaff(string message)
        {
            foreach (Server.Network.NetState ns in Server.Network.NetState.Instances)
            {
                Mobile staff = ns.Mobile;

                if (staff != null && staff.AccessLevel >= AccessLevel.Counselor)
                {
                    staff.SendMessage(0x35, message);
                }
            }
        }

        private static string[] ToArray(IList<string> source)
        {
            if (source == null)
            {
                return new string[0];
            }

            var result = new string[source.Count];

            for (int i = 0; i < source.Count; i++)
            {
                result[i] = source[i];
            }

            return result;
        }
    }
}
