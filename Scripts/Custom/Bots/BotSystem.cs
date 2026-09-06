using System;
using System.Collections.Generic;

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
            HealthCheck.Register("Bots.Population", BuildHealthResult);
            HealthCheck.Register("Bots.Chat", ChatLibrary.BuildHealthResult);

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
