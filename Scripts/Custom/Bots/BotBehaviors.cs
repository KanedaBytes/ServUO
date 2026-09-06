using System;
using System.Collections.Generic;

namespace Server.Custom
{
    /// <summary>
    /// Behaviour name to brain.
    ///
    /// The lifecycle model is "the state IS the behaviour name" - a bot stores a string and is
    /// reconstituted from it - so this registry is the seam the lifecycle session grows into.
    /// Deliberately tiny for now: two behaviours and a fallback.
    ///
    /// An unknown name falls back to Idle with a warning rather than throwing. Upstream's reason
    /// is the right one: the name comes from a save, and a behaviour removed in a later build must
    /// not make the world fail to load.
    /// </summary>
    public static class BotBehaviors
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        private static readonly Dictionary<string, Func<PlayerBotBehavior>> _factories =
            new Dictionary<string, Func<PlayerBotBehavior>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Idle", () => new IdleBehavior() },
                { "Traveler", () => new TravelerBehavior() },
                { "BankSitter", () => new BankSitterBehavior() },
                { "Shopper", () => new ShopperBehavior() },
            };

        public static PlayerBotBehavior Create(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                return new IdleBehavior();
            }

            Func<PlayerBotBehavior> factory;

            if (_factories.TryGetValue(name.Trim(), out factory))
            {
                return factory();
            }

            Log.Warn("Unknown bot behaviour '{0}'; falling back to Idle.", name);

            return new IdleBehavior();
        }

        public static bool IsKnown(string name)
        {
            return !String.IsNullOrWhiteSpace(name) && _factories.ContainsKey(name.Trim());
        }

        /// <summary>Every registered name, sorted, for command usage messages.</summary>
        public static string[] Names()
        {
            var names = new List<string>(_factories.Keys);

            names.Sort(StringComparer.Ordinal);

            return names.ToArray();
        }
    }
}
