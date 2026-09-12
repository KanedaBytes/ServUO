using System;
using System.Collections.Generic;

using Server.Mobiles;
using Server.Network;

namespace Server.Custom
{
    /// <summary>
    /// Restricted zones: entering one starts a 30-second countdown, and at zero the player is
    /// handed to the jail service.
    ///
    /// Zones live in Data/Custom/restricted-zones.json in a schema identical to the ModernUO
    /// shard's, so the map editor works against either.
    /// </summary>
    public static class RestrictedZoneSystem
    {
        private static readonly CustomLogger Log = CustomLogger.For("Zones");

        public const string ConfigPath = "Data/Custom/restricted-zones.json";

        public static readonly TimeSpan WarningDelay = TimeSpan.FromSeconds(30.0);

        private static readonly List<RestrictedZoneRecord> _records = new List<RestrictedZoneRecord>();

        private static readonly Dictionary<RestrictedZoneRecord, RestrictedZoneRegion> _regions =
            new Dictionary<RestrictedZoneRecord, RestrictedZoneRegion>();

        private static readonly Dictionary<PlayerMobile, ZoneCountdown> _countdowns =
            new Dictionary<PlayerMobile, ZoneCountdown>();

        /// <summary>
        /// False until Initialize(). TryLoad consults it so one body is correct both at boot
        /// (records only - the region tree does not exist yet) and at runtime (rebuild regions).
        /// </summary>
        private static bool _regionsLive;

        private static string _lastError;
        private static DateTime? _lastLoadUtc;

        public static IList<RestrictedZoneRecord> Zones { get { return _records.AsReadOnly(); } }

        public static string LastError { get { return _lastError; } }

        /// <summary>
        /// Records only. Regions resolve their parent with Region.Find, and the town and dungeon
        /// regions they need are loaded by Region.Load() after the Configure sweep has run.
        ///
        /// CallPriority 100 puts this above MapDefinitions.Configure() (untagged, so 0), because
        /// validating a record resolves its facet name and the facets must exist by then.
        /// </summary>
        [CallPriority(100)]
        public static void Configure()
        {
            string error;

            if (!TryLoad(out error))
            {
                Log.Error("Restricted zones are INACTIVE - {0} was not loaded: {1}", ConfigPath, error);
            }

            // OnExit does NOT fire when a client disconnects - it fires only when the logout
            // timer expires, up to five minutes later. Without this a logged-out player would be
            // jailed by a timer they cannot see.
            EventSink.Disconnected += OnDisconnected;

            // Login does fire OnEnter, but via a map change while the location is still stale, so
            // it can fire for the wrong region. The login event is the reliable hook.
            EventSink.Login += OnLogin;
        }

        public static void Initialize()
        {
            _regionsLive = true;
            RegisterAll();

            HealthCheck.Register("RestrictedZones", BuildHealthResult);
        }

        // -----------------------------------------------------------------------------------
        // Loading and saving
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Reloads from disk.
        ///
        /// On ANY failure the live zones are kept: every error path returns before the existing
        /// set is torn down. A bad edit must never leave the shard with no zones.
        /// </summary>
        public static bool TryLoad(out string error)
        {
            IList<string> ignored;
            return TryLoad(out error, out ignored);
        }

        /// <summary>As TryLoad, keeping the validator's errors as a list.</summary>
        public static bool TryLoad(out string error, out IList<string> errors)
        {
            RestrictedZoneStore store;

            if (!JsonConfig.TryLoad(ConfigPath, out store, out errors))
            {
                error = String.Join("; ", ToArray(errors));
                _lastError = error;
                return false;
            }

            // Only now is it safe to swap.
            UnregisterAll();

            _records.Clear();
            _records.AddRange(store.Zones);

            if (_regionsLive)
            {
                RegisterAll();
            }

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
                NotifyStaff(String.Format("Restricted zones NOT reloaded: {0}", error));
                Log.Error("Restricted zones NOT reloaded: {0}", error);
                return false;
            }

            Log.Info("Reloaded {0} restricted zone(s).", _records.Count);
            return true;
        }

        public static bool Save(out string error)
        {
            var store = new RestrictedZoneStore { Zones = new List<RestrictedZoneRecord>(_records) };

            if (!JsonConfig.TrySave(ConfigPath, store, out error))
            {
                Log.Error("Could not write {0}: {1}", ConfigPath, error);
                return false;
            }

            return true;
        }

        // -----------------------------------------------------------------------------------
        // Zone list
        // -----------------------------------------------------------------------------------

        public static RestrictedZoneRecord Find(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            foreach (RestrictedZoneRecord record in _records)
            {
                if (Insensitive.Equals(record.Name, name))
                {
                    return record;
                }
            }

            return null;
        }

        public static bool Add(RestrictedZoneRecord record)
        {
            if (record == null || Find(record.Name) != null)
            {
                return false;
            }

            _records.Add(record);
            RegisterZone(record);

            string error;
            Save(out error);

            return true;
        }

        public static bool Remove(RestrictedZoneRecord record)
        {
            if (record == null || !_records.Remove(record))
            {
                return false;
            }

            UnregisterZone(record);

            string error;
            Save(out error);

            return true;
        }

        // -----------------------------------------------------------------------------------
        // Region registration
        // -----------------------------------------------------------------------------------

        private static void RegisterZone(RestrictedZoneRecord record)
        {
            UnregisterZone(record);

            if (record.Map == null)
            {
                return;
            }

            var region = new RestrictedZoneRegion(record);
            _regions[record] = region;

            // Register() walks the affected sectors and re-resolves every mobile standing in
            // them, so this fires OnEnter for players already inside. No manual sweep needed -
            // verified in Server/Sector.cs UpdateMobileRegions().
            region.Register();
        }

        private static void UnregisterZone(RestrictedZoneRecord record)
        {
            RestrictedZoneRegion region;

            if (_regions.TryGetValue(record, out region))
            {
                _regions.Remove(record);
                region.Unregister();
            }
        }

        private static void RegisterAll()
        {
            foreach (RestrictedZoneRecord record in _records)
            {
                RegisterZone(record);
            }

            Log.Info("Registered {0} restricted zone(s).", _records.Count);
        }

        private static void UnregisterAll()
        {
            // Iterate the regions, not the records: a reload replaces the record list, and any
            // region whose record is already gone still needs unregistering.
            foreach (RestrictedZoneRegion region in new List<RestrictedZoneRegion>(_regions.Values))
            {
                region.Unregister();
            }

            _regions.Clear();
        }

        // -----------------------------------------------------------------------------------
        // Countdown
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Per-player countdown state. Transient by design - a restart clears every pending
        /// countdown, so nobody is ever jailed by a timer they could not see. Never persisted.
        /// </summary>
        private sealed class ZoneCountdown
        {
            public string ZoneName;
            public int Remaining;
            public Timer Timer;
        }

        public static bool HasCountdown(Mobile m)
        {
            var pm = m as PlayerMobile;
            return pm != null && _countdowns.ContainsKey(pm);
        }

        public static void OnEnterZone(PlayerMobile pm, RestrictedZoneRegion region)
        {
            if (region == null || !ShouldWarn(pm))
            {
                return;
            }

            // Re-entering restarts the countdown rather than resuming it.
            CancelCountdown(pm);

            pm.SendMessage(
                0x22,
                String.Format(
                    "You have entered a restricted area: {0}. Leave within {1:F0} seconds or you will be jailed.",
                    region.Record.Name,
                    WarningDelay.TotalSeconds));

            pm.PlaySound(0x1F3);

            var countdown = new ZoneCountdown
            {
                ZoneName = region.Record.Name,
                Remaining = (int)WarningDelay.TotalSeconds
            };

            _countdowns[pm] = countdown;

            RestrictedZoneCountdownGump.DisplayTo(pm, countdown.ZoneName, countdown.Remaining);

            // count 0 = repeat forever.
            countdown.Timer = Timer.DelayCall(
                TimeSpan.FromSeconds(1.0),
                TimeSpan.FromSeconds(1.0),
                0,
                () => OnCountdownTick(pm));
        }

        public static void CancelCountdown(PlayerMobile pm)
        {
            if (pm == null)
            {
                return;
            }

            ZoneCountdown countdown;

            // net48 has no Dictionary.Remove(key, out value) - that overload is netstandard2.1.
            if (!_countdowns.TryGetValue(pm, out countdown))
            {
                return;
            }

            _countdowns.Remove(pm);

            // Always stop before dropping the reference, or the stale timer keeps firing and
            // jails someone who had already left.
            if (countdown.Timer != null)
            {
                countdown.Timer.Stop();
            }

            pm.CloseGump(typeof(RestrictedZoneCountdownGump));
        }

        private static void OnCountdownTick(PlayerMobile pm)
        {
            ZoneCountdown countdown;

            if (!_countdowns.TryGetValue(pm, out countdown))
            {
                return;
            }

            countdown.Remaining--;

            if (countdown.Remaining > 0)
            {
                // Also puts the gump back if the player somehow dismissed it.
                RestrictedZoneCountdownGump.DisplayTo(pm, countdown.ZoneName, countdown.Remaining);
                return;
            }

            CancelCountdown(pm);
            OnCountdownExpired(pm);
        }

        private static void OnCountdownExpired(PlayerMobile pm)
        {
            RestrictedZoneRegion region = GetZoneAt(pm);

            // Re-validate: 30 seconds is a long time. They may have died, become staff, or the
            // zone may have been removed out from under them.
            if (region == null || !ShouldWarn(pm))
            {
                return;
            }

            TryJail(pm, region.Record.Name);
        }

        /// <summary>
        /// Whether this mobile is somebody the zone should warn, and then sentence.
        ///
        /// THE ONE CHOKEPOINT, which is why the bot test belongs here: OnEnterZone asks it, and
        /// the 30-second timer asks it again before jailing, so OnEnter, OnResurrect and the jail
        /// itself are all covered by this one line.
        ///
        /// A BOT IS EXCLUDED BECAUSE IT CANNOT RECEIVE THE WARNING, AND FOR NO OTHER REASON.
        /// This whole system's output is a message and a countdown gump. A PlayerBot is an
        /// accountless PlayerMobile with Player = true and no NetState (12 September 2026's class
        /// swap), so every test below already passes for one: it would be counted down by a gump
        /// that goes nowhere and then jailed by a warning it was never shown. That is not a
        /// policy, it is a sentence with the notice undelivered.
        ///
        /// SO THIS IS NOT A RULING THAT BOTS ARE EXEMPT FROM RESTRICTED ZONES. Whether a bot
        /// should be subject to them - turned back at the boundary, or walked out, or jailed with
        /// some consequence that does not need a client - is an open owner question, and nothing
        /// here settles it. If the answer is yes, the place to express it is a bot-shaped
        /// consequence, not this predicate.
        ///
        /// Measured before the guard was written: restricted-zones.json holds an EMPTY zone list,
        /// so there is no zone for a bot to walk into and this has never fired. It is a guard
        /// against the day somebody authors one near a bot route, not a repair.
        /// </summary>
        private static bool ShouldWarn(PlayerMobile pm)
        {
            return pm != null
                && !pm.Deleted
                && !(pm is IBotActor)
                && pm.Player
                && pm.Alive
                && pm.AccessLevel <= AccessLevel.Player;
        }

        private static RestrictedZoneRegion GetZoneAt(PlayerMobile pm)
        {
            if (pm == null || pm.Deleted || pm.Map == null || pm.Map == Map.Internal)
            {
                return null;
            }

            Region region = Region.Find(pm.Location, pm.Map);

            if (region == null)
            {
                return null;
            }

            // ServUO has no generic GetRegion<T>() - only GetRegion(Type).
            return region.GetRegion(typeof(RestrictedZoneRegion)) as RestrictedZoneRegion;
        }

        // -----------------------------------------------------------------------------------
        // Jail handoff
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Hands a player to the jail service.
        ///
        /// The guard-then-assert shape is deliberate and carried over from the ModernUO shard:
        /// jailing an already-jailed player there overwrote the release timer without stopping
        /// it, and JailPlayer could silently no-op. Step 2's implementation is held to the same
        /// contract rather than being trusted.
        /// </summary>
        private static void TryJail(PlayerMobile pm, string zoneName)
        {
            IJailService jail = JailService.Provider;

            if (jail.IsPlayerJailed(pm))
            {
                return;
            }

            string reason = String.Format("Restricted zone: {0}", zoneName);

            // from: null - there is no staff member behind an automatic jail.
            jail.JailPlayer(null, pm, reason);

            if (!jail.IsPlayerJailed(pm))
            {
                Log.Error(
                    "Failed to jail {0} for '{1}': the jail service rejected the call silently.",
                    pm.Name,
                    reason);

                NotifyStaff(
                    String.Format(
                        "Could not jail {0} for '{1}' - the jail service rejected the call. Jail them manually.",
                        pm.Name,
                        reason));
                return;
            }

            Log.Info("Jailed {0} for '{1}'.", pm.Name, reason);
            NotifyStaff(String.Format("{0} was jailed automatically. Reason: {1}", pm.Name, reason));
        }

        // -----------------------------------------------------------------------------------
        // Events
        // -----------------------------------------------------------------------------------

        private static void OnDisconnected(DisconnectedEventArgs e)
        {
            var pm = e.Mobile as PlayerMobile;

            if (pm != null)
            {
                CancelCountdown(pm);
            }
        }

        private static void OnLogin(LoginEventArgs e)
        {
            var pm = e.Mobile as PlayerMobile;

            if (pm == null)
            {
                return;
            }

            RestrictedZoneRegion region = GetZoneAt(pm);

            if (region != null)
            {
                OnEnterZone(pm, region);
            }
        }

        // -----------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------

        private static void NotifyStaff(string message)
        {
            foreach (NetState ns in NetState.Instances)
            {
                var staff = ns.Mobile as PlayerMobile;

                if (staff != null && staff.AccessLevel >= AccessLevel.Counselor)
                {
                    staff.SendMessage(0x35, message);
                }
            }
        }

        private static string[] ToArray(IList<string> items)
        {
            if (items == null)
            {
                return new string[0];
            }

            var array = new string[items.Count];
            items.CopyTo(array, 0);
            return array;
        }

        private static HealthResult BuildHealthResult()
        {
            string loaded = _lastLoadUtc.HasValue
                ? _lastLoadUtc.Value.ToString("HH:mm:ss") + "Z"
                : "never";

            if (_lastError != null)
            {
                return HealthResult.Fail(
                    String.Format(
                        "{0} did not load: {1}. Running on {2} previously loaded zone(s), last good load {3}.",
                        ConfigPath,
                        _lastError,
                        _records.Count,
                        loaded));
            }

            if (_records.Count == 0)
            {
                return HealthResult.Warn("No zones configured. Last load " + loaded + ".");
            }

            return HealthResult.Ok(
                String.Format("{0} zone(s), {1} countdown(s) active, last load {2}",
                    _records.Count,
                    _countdowns.Count,
                    loaded));
        }
    }
}
