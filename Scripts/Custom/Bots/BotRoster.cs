// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// BotRoster.cs - who the named bots are: Data/Custom/bot-roster.json.
//
// Sean's decision of 22 September 2026 (CLASS-DECISION.md, "Named bots"): the fixed-role cast - every
// fixed post, twenty-one today - is a set of NAMED characters, each with its own locked Account,
// persisted by the world save like a player's and logged out rather than deleted. Everyone else is a
// throwaway, unchanged. This file is the data half: who, what class, which town, which post.
// NamedBots.cs is the half that makes them exist.
//
// UPSTREAM HAS NO ROSTER. Its one persistent kind of bot is a guild recruit (uo-offline
// BotGuildRecruits.cs; PlayerBot.GuildBound at :166, IsPermanent at :168), made permanent by a player
// recruiting it. Recruitment is deferred to 7g here, so the trigger is data instead - a named seam, and
// the reason this file exists at all.
//
// IDENTITY IS THE POOL NAME A BOT WAS BORN WITH. The account is named after it and the character
// carries it (PlayerBot.RosterName), so a rename is an edit to `name` with `renamedFrom` set to the pool
// name: the account and the character stay the same objects and only the display name moves.
//
// TWO STAGES OF VALIDATION, because half of it needs the nav graph and half does not.
//
//   AT LOAD (Validate, through JsonConfig.TryLoad): the shape - unique names, names in the pool, a
//   real class, a known town, a post named. Any error refuses the WHOLE file and keeps the previous
//   roster, which is the failure contract every custom config here follows.
//
//   AT INITIALIZE (PostError, against the graph): is the post a destination in that town, and is it a
//   bank or a station of this class's trade. A failure refuses THAT ENTRY only - the bot stays
//   offline and Bots.Named names it. Refusing the whole cast because somebody deleted one shop in the
//   editor would take twenty innocent bots offline to report one fault.

using System;
using System.Collections.Generic;

using Newtonsoft.Json;

using Server.Misc;

namespace Server.Custom
{
    /// <summary>One named bot, as the roster file describes it.</summary>
    public class BotRosterEntry
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>A BotClass name. A string, so an unknown class is a named error rather than a JSON fault.</summary>
        [JsonProperty("class")]
        public string Class { get; set; }

        [JsonProperty("home")]
        public string Home { get; set; }

        [JsonProperty("post")]
        public string Post { get; set; }

        [JsonProperty("female")]
        public bool Female { get; set; }

        /// <summary>The pool name this bot was born with, when it has since been renamed.</summary>
        [JsonProperty("renamedFrom", NullValueHandling = NullValueHandling.Ignore)]
        public string RenamedFrom { get; set; }

        /// <summary>The name that never changes: what the account is called and what the character carries.</summary>
        [JsonIgnore]
        public string Identity
        {
            get { return String.IsNullOrEmpty(RenamedFrom) ? Name : RenamedFrom; }
        }

        /// <summary>The parsed class. Only meaningful on an entry that passed Validate.</summary>
        [JsonIgnore]
        public BotClass ParsedClass { get; set; }

        [JsonConstructor]
        public BotRosterEntry()
        {
        }

        public override string ToString()
        {
            return String.Format("{0} ({1}, {2}, {3})", Name, Class, Home, Post);
        }
    }

    public class BotRosterStore : IValidatableConfig
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("comment", NullValueHandling = NullValueHandling.Ignore)]
        public string Comment { get; set; }

        [JsonProperty("pool")]
        public List<string> Pool { get; set; }

        [JsonProperty("bots")]
        public List<BotRosterEntry> Bots { get; set; }

        [JsonConstructor]
        public BotRosterStore()
        {
            SchemaVersion = BotRoster.SchemaVersion;
            Pool = new List<string>();
            Bots = new List<BotRosterEntry>();
        }

        /// <summary>
        /// The shape. Pure apart from BotHomeTowns, which reads bots.json - loaded first, at
        /// CallPriority 120 against this file's 125.
        /// </summary>
        public void Validate(ConfigErrors errors)
        {
            if (SchemaVersion > BotRoster.SchemaVersion)
            {
                errors.Add(
                    "schemaVersion {0} is newer than this build understands ({1}).",
                    SchemaVersion,
                    BotRoster.SchemaVersion);
            }

            if (Pool == null)
            {
                Pool = new List<string>();
            }

            if (Bots == null)
            {
                Bots = new List<BotRosterEntry>();
            }

            var pool = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in Pool)
            {
                if (!BotRoster.IsLegalName(name))
                {
                    errors.Add("pool name '{0}' is not a legal player name.", name);
                    continue;
                }

                if (!pool.Add(name))
                {
                    errors.Add("pool names '{0}' twice.", name);
                }
            }

            // Every name and every renamedFrom, across the whole file: two bots may not share a
            // display name, and a new name may not take the identity another bot was born with.
            var taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < Bots.Count; i++)
            {
                BotRosterEntry entry = Bots[i];
                string where = String.Format("bots[{0}]", i);

                if (entry == null)
                {
                    errors.Add("{0} is null.", where);
                    continue;
                }

                if (String.IsNullOrWhiteSpace(entry.Name))
                {
                    errors.Add("{0} has no name.", where);
                    continue;
                }

                where = String.Format("bots[{0}] '{1}'", i, entry.Name);

                if (!BotRoster.IsLegalName(entry.Name))
                {
                    errors.Add("{0}: not a legal player name.", where);
                }

                if (String.IsNullOrEmpty(entry.RenamedFrom))
                {
                    if (!pool.Contains(entry.Name))
                    {
                        errors.Add(
                            "{0}: not in the pool. A renamed bot keeps its pool name in renamedFrom.",
                            where);
                    }
                }
                else if (!pool.Contains(entry.RenamedFrom))
                {
                    errors.Add("{0}: renamedFrom '{1}' is not in the pool.", where, entry.RenamedFrom);
                }

                Take(taken, entry.Name, i, where, errors);

                if (!String.IsNullOrEmpty(entry.RenamedFrom)
                    && !Insensitive.Equals(entry.RenamedFrom, entry.Name))
                {
                    Take(taken, entry.RenamedFrom, i, where, errors);
                }

                BotClass cls;

                if (!BotRoster.TryParseClass(entry.Class, out cls))
                {
                    errors.Add("{0}: class '{1}' is not a bot class.", where, entry.Class);
                }
                else
                {
                    entry.ParsedClass = cls;
                }

                string town;

                if (!BotHomeTowns.TryParse(entry.Home, out town))
                {
                    errors.Add(
                        "{0}: home '{1}' is not a town in bots.json destinations.towns.",
                        where,
                        entry.Home);
                }
                else
                {
                    entry.Home = town;
                }

                if (String.IsNullOrWhiteSpace(entry.Post))
                {
                    errors.Add("{0}: no post.", where);
                }
            }
        }

        private static void Take(
            Dictionary<string, int> taken, string name, int index, string where, ConfigErrors errors)
        {
            int other;

            if (taken.TryGetValue(name, out other))
            {
                if (other != index)
                {
                    errors.Add("{0}: the name '{1}' is already bots[{2}]'s.", where, name, other);
                }

                return;
            }

            taken[name] = index;
        }
    }

    public static class BotRoster
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        public const string ConfigPath = "Data/Custom/bot-roster.json";

        public const int SchemaVersion = 1;

        private static BotRosterStore _store = new BotRosterStore();

        private static readonly HashSet<string> _reserved =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The live roster. Never null; empty until a load succeeds.</summary>
        public static BotRosterStore Store
        {
            get { return _store; }
        }

        /// <summary>Why the last load was refused, or null.</summary>
        public static string LastError { get; private set; }

        public static DateTime? LastLoadUtc { get; private set; }

        /// <summary>
        /// After bots.json (120), because a home town is checked against its towns list.
        /// </summary>
        [CallPriority(125)]
        public static void Configure()
        {
            string error;

            if (!TryLoad(out error))
            {
                Log.Error("Bot roster was not loaded ({0}): {1}", ConfigPath, error);
            }
        }

        public static bool TryLoad(out string error)
        {
            BotRosterStore store;
            IList<string> errors;

            if (!JsonConfig.TryLoad(ConfigPath, out store, out errors))
            {
                var parts = new string[errors == null ? 0 : errors.Count];

                if (errors != null)
                {
                    errors.CopyTo(parts, 0);
                }

                error = String.Join("; ", parts);
                LastError = error;
                return false;
            }

            _store = store;
            LastError = null;
            LastLoadUtc = DateTime.UtcNow;
            error = null;

            Reserve(store);

            Log.Info("Bot roster loaded: {0} named bot(s) from a pool of {1}.", store.Bots.Count, store.Pool.Count);

            return true;
        }

        /// <summary>
        /// Every roster name and every identity is withheld from the throwaway name pool.
        ///
        /// Additive across reloads, deliberately: a name taken out of the roster belongs to a
        /// retired bot that is still in the save, offline, and a throwaway wearing it would be a
        /// second person with the same name the day it came back.
        /// </summary>
        private static void Reserve(BotRosterStore store)
        {
            foreach (BotRosterEntry entry in store.Bots)
            {
                _reserved.Add(entry.Name);
                _reserved.Add(entry.Identity);
            }

            NamePool.Reserve(_reserved);
        }

        /// <summary>Is this a name a named bot wears or was born with?</summary>
        public static bool IsReserved(string name)
        {
            return !String.IsNullOrEmpty(name) && _reserved.Contains(name.Trim());
        }

        /// <summary>The entry whose identity this is, or null.</summary>
        public static BotRosterEntry FindByIdentity(string identity)
        {
            if (String.IsNullOrEmpty(identity))
            {
                return null;
            }

            foreach (BotRosterEntry entry in _store.Bots)
            {
                if (Insensitive.Equals(entry.Identity, identity))
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>The entry by display name or identity, for commands.</summary>
        public static BotRosterEntry Find(string name)
        {
            if (String.IsNullOrEmpty(name))
            {
                return null;
            }

            foreach (BotRosterEntry entry in _store.Bots)
            {
                if (Insensitive.Equals(entry.Name, name) || Insensitive.Equals(entry.Identity, name))
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>The player-name rule, exactly as CharacterCreation.SetName applies it (:362).</summary>
        public static bool IsLegalName(string name)
        {
            return !String.IsNullOrEmpty(name)
                && NameVerification.Validate(
                    name, 2, 16, true, false, true, 1, NameVerification.SpaceDashPeriodQuote);
        }

        /// <summary>A class name, never the legacy Crafter, which is no longer rolled and has no station of its own.</summary>
        public static bool TryParseClass(string text, out BotClass cls)
        {
            cls = BotClass.Warrior;

            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            BotClass parsed;

            if (!Enum.TryParse(text.Trim(), true, out parsed) || !Enum.IsDefined(typeof(BotClass), parsed))
            {
                return false;
            }

            // Enum.TryParse accepts "10" as well as "Smith"; a roster is read by people.
            int ignored;

            if (Int32.TryParse(text.Trim(), out ignored) || parsed == BotClass.Crafter)
            {
                return false;
            }

            cls = parsed;
            return true;
        }

        // ---- the posts, against the graph ----

        /// <summary>
        /// Why this entry cannot stand at its post, or null when it can.
        ///
        /// A bank post takes any class and several bots. A station post takes one bot, of the
        /// station's trade - the same CrafterProfiles and BotWorkSites.Available the recipe reads,
        /// so a post the roster accepts is a bench the recipe would have staffed.
        /// </summary>
        public static string PostError(BotRosterEntry entry, Map map)
        {
            if (entry == null)
            {
                return "no entry";
            }

            NavDestination post = Nav.Destination(entry.Post);

            if (post == null)
            {
                return String.Format("post '{0}' is not a destination on the graph", entry.Post);
            }

            if (!post.HasTag(entry.Home))
            {
                return String.Format("post '{0}' is not in {1}", entry.Post, entry.Home);
            }

            if (Insensitive.Equals(post.Type, "bank"))
            {
                return null;
            }

            BotClass? trade = StationTrade(map, post);

            if (trade == null)
            {
                return String.Format(
                    "post '{0}' is a {1}, which is neither a bank nor a craft station anyone works",
                    entry.Post,
                    post.Type);
            }

            if (trade.Value != entry.ParsedClass)
            {
                return String.Format(
                    "post '{0}' is a {1} station and {2} is a {3}",
                    entry.Post,
                    BotClassHelper.DisplayName(trade.Value),
                    entry.Name,
                    BotClassHelper.DisplayName(entry.ParsedClass));
            }

            return null;
        }

        /// <summary>The class whose bench this destination is, or null.</summary>
        public static BotClass? StationTrade(Map map, NavDestination destination)
        {
            if (destination == null)
            {
                return null;
            }

            foreach (CrafterProfile profile in CrafterProfiles.All)
            {
                var station = new BotStation(profile.StationType, profile.StationTag);

                if (station.IsNone)
                {
                    continue;
                }

                foreach (NavDestination bench in BotWorkSites.Available(map, station))
                {
                    if (Insensitive.Equals(bench.Id, destination.Id))
                    {
                        return profile.Type;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Every entry's post error, keyed by identity, with a second bot on a one-bot station
        /// refused as well. Empty when the whole cast can stand where the roster says.
        /// </summary>
        public static Dictionary<string, string> PostErrors(BotRosterStore store, Map map)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var stations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (BotRosterEntry entry in store.Bots)
            {
                string error = PostError(entry, map);

                if (error != null)
                {
                    result[entry.Identity] = error;
                    continue;
                }

                NavDestination post = Nav.Destination(entry.Post);

                if (post != null && !Insensitive.Equals(post.Type, "bank"))
                {
                    string holder;

                    if (stations.TryGetValue(post.Id, out holder))
                    {
                        result[entry.Identity] = String.Format(
                            "post '{0}' is a one-bot station and already {1}'s", post.Id, holder);
                        continue;
                    }

                    stations[post.Id] = entry.Name;
                }
            }

            return result;
        }
    }
}
