using System;
using System.Collections.Generic;
using System.Text;

namespace Server.Custom
{
    /// <summary>
    /// Owns Data/Custom/bots.json and the resolved caps every bot template is built against.
    ///
    /// GAME THREAD ONLY, like every other custom system.
    ///
    /// Failure contract, matching NavigationSystem and DailyLifeSystem: on ANY load failure the
    /// live config is kept. A bad edit must never leave the caps at some half-applied value - a
    /// bot built against a broken cap is worse than one built against the previous good one,
    /// because the damage is baked into its skills rather than visible in a log line.
    /// </summary>
    public static class BotSystem
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        public const string ConfigPath = "Data/Custom/bots.json";

        public const int SchemaVersion = 1;

        private static BotStore _store = new BotStore();
        private static BotCaps _caps = BotCaps.Resolve(null);

        private static string _lastError;
        private static DateTime? _lastLoadUtc;

        /// <summary>The live config. Never null.</summary>
        public static BotStore Store
        {
            get { return _store; }
        }

        /// <summary>
        /// The caps every bot is built against. Never null: with no config at all this resolves
        /// straight from Config/PlayerCaps.cfg, which is the intended default.
        /// </summary>
        public static BotCaps Caps
        {
            get { return _caps; }
        }

        /// <summary>
        /// CallPriority 120 puts this above NavigationSystem (100) and DailyLifeSystem (110).
        /// Nothing here depends on either yet, but the bot layer will read nav ids the moment
        /// travel lands, and the ladder is easier to keep than to rediscover.
        /// </summary>
        [CallPriority(120)]
        public static void Configure()
        {
            // The chat corpus is plain text on disk, not a bound config, so it has no failure
            // contract to honour and nothing depends on it being loaded first. It is read here
            // rather than in Initialize only so that a bot spawned by anything early already has
            // a voice. Its own failure is reported through Bots.Chat.
            ChatLibrary.Load();

            string error;

            if (!TryLoad(out error))
            {
                Log.Error("Bot config was not loaded ({0}): {1}", ConfigPath, error);
                Log.Warn("Bots are running on caps read straight from PlayerCaps.cfg.");
                return;
            }
        }

        public static void Initialize()
        {
            // Bind the crafting profiles to ServUO's CraftSystem singletons. It has to happen
            // here and not in a static constructor: CraftContext.Configure is what builds those
            // eleven singletons, and reaching for one earlier would construct a second, parallel
            // copy that nothing else in the shard shares.
            CrafterProfiles.Bind();

            // Validate the work sites against the loaded graph and the real map. Initialize
            // rather than Configure, because this asks questions about world items - the forge
            // and anvil are addons the decoration system places - and there is no world during
            // Configure.
            BotWorkSites.Validate(Map.Trammel);

            // The reach check lives in Nav.Data, not in Bots.Work: a thin site is a fact about the
            // navigation data, and the person who needs to hear it is whoever just authored a site
            // in the editor.
            NavigationSystem.RegisterAuditor(() => BotWorkSites.ThinSites(Map.Trammel));

            HealthCheck.Register("Bots.Population", BuildHealthResult);
            HealthCheck.Register("Bots.Chat", ChatLibrary.BuildHealthResult);
            HealthCheck.Register("Bots.Work", BuildWorkHealthResult);

            BotTickManager.Initialize();

            if (Config.Get("Custom.BotSmokeOnStart", false))
            {
                // Deferred to ServerStarted: the audit spawns real mobiles, and spawning during
                // Initialize would put them in the world before the map and regions have settled.
                EventSink.ServerStarted += () => BotSmoke.Run(null);
            }
        }

        // ---- loading ----

        public static bool TryLoad(out string error)
        {
            IList<string> ignored;
            return TryLoad(out error, out ignored);
        }

        public static bool TryLoad(out string error, out IList<string> errors)
        {
            BotStore store;

            if (!JsonConfig.TryLoad(ConfigPath, out store, out errors))
            {
                error = String.Join("; ", ToArray(errors));
                _lastError = error;
                return false;
            }

            // Only now is it safe to swap.
            _store = store;
            _caps = BotCaps.Resolve(store.Caps);

            error = null;
            _lastError = null;
            _lastLoadUtc = DateTime.UtcNow;

            Log.Info("Bot config loaded. {0}", _caps.Describe());

            return true;
        }

        public static bool TryReload(out string error)
        {
            IList<string> ignored;
            return TryReload(out error, out ignored);
        }

        public static bool TryReload(out string error, out IList<string> errors)
        {
            // The corpus reloads either way. It is independent of bots.json - a bad edit to one
            // has nothing to do with the other - and re-reading it is the whole point of running
            // [BotsReload after editing a .txt.
            ChatLibrary.Load();

            if (!TryLoad(out error, out errors))
            {
                Log.Error("Bot config NOT reloaded: {0}", error);
                return false;
            }

            Log.Info(
                "Bot config reloaded. {0}. Chat corpus: {1} line(s) across {2} categor(ies).",
                _caps.Describe(),
                ChatLibrary.WiredLines,
                ChatLibrary.CategoryCount);

            return true;
        }

        private static string[] ToArray(IList<string> errors)
        {
            if (errors == null)
            {
                return new string[0];
            }

            var array = new string[errors.Count];
            errors.CopyTo(array, 0);

            return array;
        }

        // ---- health ----

        /// <summary>
        /// Live bot count, the class and tier spread, and - always - which caps are in force.
        ///
        /// The caps clause is never omitted. A shard whose bots are quietly built to different
        /// limits than its players is a thing you want to find out from a health check, not from
        /// a player asking why the bots hit harder than they do.
        /// </summary>
        public static HealthResult BuildHealthResult()
        {
            string loaded = _lastLoadUtc.HasValue
                ? _lastLoadUtc.Value.ToString("HH:mm:ss") + "Z"
                : "never";

            string caps = _caps.Describe();

            if (_lastError != null)
            {
                return HealthResult.Fail(String.Format(
                    "{0} did not load: {1}. Running on PlayerCaps.cfg; last good load {2}. {3}",
                    ConfigPath,
                    _lastError,
                    loaded,
                    caps));
            }

            var byClass = new Dictionary<BotClass, int>();
            var byTier = new Dictionary<BotSkillTier, int>();
            int count = 0;
            int travelling = 0;
            int lingering = 0;
            int clockless = 0;
            Map facet = null;
            var byBehaviour = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // The live list, not World.Mobiles. LiveRegistry already tracks exactly the mobiles
            // this shard cares about (CLAUDE.md section 15).
            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot == null)
                {
                    continue;
                }

                count++;

                if (bot.PhaseClockUnset)
                {
                    clockless++;
                }

                int classCount;
                byClass.TryGetValue(bot.Class, out classCount);
                byClass[bot.Class] = classCount + 1;

                int tierCount;
                byTier.TryGetValue(bot.SkillTier, out tierCount);
                byTier[bot.SkillTier] = tierCount + 1;

                if (facet == null && bot.Map != null && bot.Map != Map.Internal)
                {
                    facet = bot.Map;
                }

                PlayerBotBehavior behaviour = bot.Behavior;

                if (behaviour != null)
                {
                    int behaviourCount;
                    byBehaviour.TryGetValue(behaviour.SerializableName, out behaviourCount);
                    byBehaviour[behaviour.SerializableName] = behaviourCount + 1;
                }

                var traveler = behaviour as TravelerBehavior;

                if (traveler != null)
                {
                    if (traveler.IsTravelling)
                    {
                        travelling++;
                    }
                    else if (traveler.IsLingering)
                    {
                        lingering++;
                    }
                }
            }

            string detail = String.Format(
                "{0} bot(s) live ({1} travelling, {2} lingering, {3} idle; {4} name(s) claimed){5}{6}. "
                + "recovery: {7}. {8}. Last load {9}",
                count,
                travelling,
                lingering,
                count - travelling - lingering,
                NamePool.InUseCount,
                Describe(byClass, BotClassHelper.DisplayName),
                Describe(byTier, BotSkillTierHelper.DisplayName),
                NavWalker.DescribeRungTotals(),
                caps,
                loaded);

            if (BotTickManager.NoRouteLastTick > 0 || BotTickManager.NoDestinationLastTick > 0)
            {
                detail += String.Format(
                    ". Last tick: {0} could not route, {1} found no destination",
                    BotTickManager.NoRouteLastTick,
                    BotTickManager.NoDestinationLastTick);
            }

            if (byBehaviour.Count > 0)
            {
                var parts = new List<string>();

                foreach (var entry in byBehaviour)
                {
                    parts.Add(String.Format("{0} {1}", entry.Value, entry.Key));
                }

                parts.Sort(StringComparer.Ordinal);

                detail += ". behaviours: " + String.Join(", ", parts.ToArray());
            }

            List<string> belowFloor = BotCrowds.BelowFloor(facet ?? Map.Trammel);

            if (belowFloor.Count > 0)
            {
                detail += String.Format(
                    ". banks below floor: {0} ({1})",
                    belowFloor.Count,
                    String.Join(", ", belowFloor.ToArray()));
            }

            if (BotTickManager.AbandonedTotal > 0)
            {
                detail += String.Format(
                    ". {0} walk(s) ended without arriving since boot",
                    BotTickManager.AbandonedTotal);
            }

            if (BotTickManager.GaveUpTotal > 0)
            {
                detail += String.Format(
                    ". {0} gatherer(s) gave up getting into a site since boot",
                    BotTickManager.GaveUpTotal);
            }

            // A class that can never travel is a config bug that looks exactly like a walker bug:
            // the bot asks for a destination every tick, gets nothing, and stands still.
            List<BotClass> starved = BotDestinations.StarvedClasses(facet ?? Map.Trammel);

            if (starved.Count > 0)
            {
                var names = new List<string>();

                foreach (BotClass cls in starved)
                {
                    names.Add(BotClassHelper.DisplayName(cls));
                }

                names.Sort(StringComparer.Ordinal);

                return HealthResult.Warn(String.Format(
                    "{0} class(es) have no destination they may visit ({1}) - check destinations weights in {2}. {3}",
                    starved.Count,
                    String.Join(", ", names.ToArray()),
                    ConfigPath,
                    detail));
            }

            List<string> overcrowded = _store.Life.OvercrowdedDestinations();

            if (overcrowded.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "{0} destination(s) want a bigger crowd than they have arrival points ({1}). {2}",
                    overcrowded.Count,
                    String.Join("; ", overcrowded.ToArray()),
                    detail));
            }

            List<string> unknownPhases = _store.Life.UnknownPhaseKeys();

            if (unknownPhases.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "{0} phase clamp(s) name a behaviour that does not exist ({1}) - they do nothing. {2}",
                    unknownPhases.Count,
                    String.Join(", ", unknownPhases.ToArray()),
                    detail));
            }

            List<string> unknown = _store.Destinations.UnknownKeys();

            if (unknown.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "{0} destination weight key(s) match nothing in the nav graph ({1}) - they do nothing. {2}",
                    unknown.Count,
                    String.Join(", ", unknown.ToArray()),
                    detail));
            }

            // A BOT WITH AN UNSET PHASE CLOCK IS PERMANENTLY OVERDUE, and this check exists
            // because that went unnoticed for three sessions.
            //
            // PhaseStartedAt was never set in the constructor, so `CustomTime.Now - MinValue` came
            // out at two thousand years - greater than any phase length - and the roller wanted to
            // transition every bot on every pass. The visible symptom was that any behaviour set by
            // hand was overwritten within seconds, which reads as a broken command rather than a
            // broken clock. It should now be impossible; that is exactly why it is worth counting.
            if (clockless > 0)
            {
                return HealthResult.Warn(String.Format(
                    "{0} of {1} bot(s) have an UNSET PHASE CLOCK - they are permanently overdue and "
                    + "the lifecycle will overwrite anything set on them. {2}",
                    clockless,
                    count,
                    detail));
            }

            // A claimed name with no live bot behind it is a leak in the census the population
            // manager will later depend on, so it is worth saying out loud now.
            if (NamePool.InUseCount > count)
            {
                return HealthResult.Warn(String.Format(
                    "{0} claimed name(s) but only {1} live bot(s) - names leaked on delete. {2}",
                    NamePool.InUseCount,
                    count,
                    detail));
            }

            if (_caps.AnyOverridden)
            {
                return HealthResult.Warn(detail);
            }

            return HealthResult.Ok(detail);
        }

        /// <summary>
        /// Bots.Work - the working-class census.
        ///
        /// Separate from Bots.Population for the same reason Bots.Chat is: it answers a different
        /// question with a different failure mode. Population asks "are there bots and are they
        /// built correctly"; this asks "is there anywhere for them to work, and are they working".
        ///
        /// The verdict ladder, strictest first:
        ///
        ///   FAIL   a class has a station and the graph has none of it. That class can never
        ///          work at all, and nothing else in the system would say so.
        ///   WARN   a site was excluded at load (no route, or nothing harvestable there), or a
        ///          crafter is blocked at its bench, or a capacity entry names a site that does
        ///          not exist.
        ///   OK     everything else, including a shard with no gatherers alive right now.
        /// </summary>
        public static HealthResult BuildWorkHealthResult()
        {
            var crafters = CrafterBehavior.Live();
            var gatherers = GathererBehavior.Live();

            int atStation = 0;
            int dry = 0;
            int full = 0;
            int made = 0;
            var blocked = new List<string>();

            foreach (CrafterBehavior crafter in crafters)
            {
                atStation++;
                made += crafter.Made;

                if (crafter.IsDry)
                {
                    dry++;
                }

                if (crafter.IsFull)
                {
                    full++;
                }

                if (crafter.Blocked != null)
                {
                    blocked.Add(crafter.Blocked);
                }
            }

            int working = 0;
            int walkingIn = 0;

            foreach (GathererBehavior gatherer in gatherers)
            {
                if (gatherer.IsWorking)
                {
                    working++;
                }
                else
                {
                    walkingIn++;
                }
            }

            int hauling = 0;

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot != null && bot.HaulPending)
                {
                    hauling++;
                }
            }

            var text = new StringBuilder(320);

            List<string> sites = BotWorkSites.Census(Map.Trammel);

            text.Append(sites.Count == 0 ? "no work sites on the graph" : "sites: " + String.Join(", ", ToArray(sites)));

            text.AppendFormat(
                ". {0} crafter(s) at station ({1} dry, {2} bench-full, {3} made since attaching), "
                + "{4} gatherer(s) out ({5} working, {6} walking in), {7} hauling.",
                atStation,
                dry,
                full,
                made,
                gatherers.Count,
                working,
                walkingIn,
                hauling);

            text.AppendFormat(
                " {0} unit(s) mined, {1} load(s) delivered carrying {2} unit(s). "
                + "{3} pack animal(s) live, {4} reaped, {5} released.",
                BotWorkSites.Mined,
                BotWorkSites.Deliveries,
                BotWorkSites.Delivered,
                BotPackAnimals.LiveCount(),
                BotPackAnimals.Reaped,
                BotPackAnimals.Released);

            var excluded = new List<string>();

            foreach (var entry in BotWorkSites.Excluded)
            {
                excluded.Add(entry.Key + " (" + entry.Value + ")");
            }

            if (excluded.Count > 0)
            {
                excluded.Sort(StringComparer.Ordinal);

                return HealthResult.Warn(String.Format(
                    "{0} work site(s) excluded at load: {1}. {2}",
                    excluded.Count,
                    String.Join("; ", ToArray(excluded)),
                    text));
            }

            // A class with no station is a WARN, not a Fail. The Fisherman is the only one, it is
            // a deliberately severed seam rather than a fault - there is no `dock` destination
            // because the fishing half is a later session - and failing a health check for a
            // planned gap trains people to ignore it.
            IList<string> stationless = BotWorkSites.Stationless;

            if (stationless.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "{0} class(es) have nowhere to work: {1}. {2}",
                    stationless.Count,
                    String.Join("; ", ToArray(stationless)),
                    text));
            }

            List<string> unknownCapacity = _store.Life.UnknownCapacityKeys();

            if (unknownCapacity.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "capacity names {0} destination(s) that are not on the graph: {1}. {2}",
                    unknownCapacity.Count,
                    String.Join(", ", ToArray(unknownCapacity)),
                    text));
            }

            if (blocked.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "{0} crafter(s) cannot work: {1}. {2}",
                    blocked.Count,
                    String.Join("; ", ToArray(blocked)),
                    text));
            }

            return HealthResult.Ok(text.ToString());
        }

        private static string Describe<T>(Dictionary<T, int> counts, Func<T, string> name)
        {
            if (counts.Count == 0)
            {
                return String.Empty;
            }

            var parts = new List<string>();

            foreach (var pair in counts)
            {
                parts.Add(String.Format("{0} {1}", pair.Value, name(pair.Key)));
            }

            parts.Sort(StringComparer.Ordinal);

            return " - " + String.Join(", ", parts.ToArray());
        }
    }
}
