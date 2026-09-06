// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// ChatTokens.cs — the one place that knows every {token} in the corpus.
//
// The corpus is 1,724 lines written for a shard that had an economy, a taming
// layer, dungeons and an event journal. This shard has none of those yet, so
// most of its token vocabulary has nothing behind it. The danger that creates is
// specific and ugly: a bot saying "WTS {item} 500gp" out loud.
//
// So every token is registered here with TWO things - the session that owns it,
// and a resolver that is null until that session lands. A line is then one of:
//
//   wired    every token has a live resolver             -> may be spoken
//   reserved every token is known, at least one unwired   -> counted, never spoken
//   unknown  a token nobody registered (a typo)           -> counted, Warn
//
// Reserved is a progress meter, not a fault: it falls as later sessions land,
// and Bots.Chat prints the breakdown by owner so you can see which layer is
// holding which lines back.
//
// CLASSIFICATION IS STATIC, THE GUARD IS NOT. Classify() answers from the token
// names alone. A wired token can still fail at runtime - {place} needs a
// destination, and a bot that has none resolves it to nothing - so TryResolve
// refuses any line that still holds a '{' when substitution finishes. Both are
// needed: the static pass keeps unspeakable lines out of the pools, the runtime
// guard catches a missing context.

using System;
using System.Collections.Generic;
using System.Text;

namespace Server.Custom
{
    /// <summary>
    /// Which session owns a token, and therefore who wires it. Ordering is not significant.
    /// </summary>
    public enum ChatTokenOwner
    {
        /// <summary>The navigation layer. Live since step 4a.</summary>
        Nav,

        /// <summary>Bot names. Live since step 7a.</summary>
        Identity,

        /// <summary>BotShop, BotBanking, BotWantAd. Not ported.</summary>
        Economy,

        /// <summary>BotTaming. Not ported.</summary>
        Taming,

        /// <summary>AdventurerBehavior and the dungeon registry. Not ported.</summary>
        Adventurer,

        /// <summary>BotEventJournal, which is also what makes Gossip/ reachable. Not ported.</summary>
        Journal,
    }

    public enum ChatLineClass
    {
        /// <summary>Every token resolves. Speakable.</summary>
        Wired,

        /// <summary>Every token is known; at least one has no resolver yet. Never spoken.</summary>
        Reserved,

        /// <summary>At least one token is not registered at all. Never spoken, and a warning.</summary>
        Unknown,
    }

    /// <summary>
    /// What a token is allowed to look at while resolving. Deliberately small: a token that needs
    /// more than this is a token whose session has not landed.
    /// </summary>
    public sealed class ChatTokenContext
    {
        /// <summary>The bot doing the talking. Never null in practice.</summary>
        public PlayerBot Bot { get; set; }

        /// <summary>Where the bot is, or is going - a nav destination id. May be null.</summary>
        public string DestinationId { get; set; }

        /// <summary>The other party, when a line is addressed at somebody. May be null.</summary>
        public Mobile Other { get; set; }
    }

    public sealed class ChatToken
    {
        /// <summary>The bare name, with no braces: "place", not "{place}".</summary>
        public string Name { get; private set; }

        public ChatTokenOwner Owner { get; private set; }

        /// <summary>Null until the owning session lands. Returning null means "cannot right now".</summary>
        public Func<ChatTokenContext, string> Resolver { get; private set; }

        public bool IsWired
        {
            get { return Resolver != null; }
        }

        public ChatToken(string name, ChatTokenOwner owner, Func<ChatTokenContext, string> resolver)
        {
            Name = name;
            Owner = owner;
            Resolver = resolver;
        }
    }

    public static class ChatTokens
    {
        /// <summary>
        /// Every token that appears anywhere in the corpus.
        ///
        /// The census that produced this list, over all 145 files:
        ///   {price} 141  {other} 121  {place} 113  {actor} 95  {when} 80  {item} 49
        ///   {dest}   32  {name}   16  {dungeon} 16  {pet}  10  {mat}  9  {short} 6
        ///
        /// A token missing from this table is a Warn, not a silent pass-through, because the only
        /// way one arises is a typo in a corpus edit - and a typo'd token is a line that can never
        /// be spoken, which is invisible from the outside.
        /// </summary>
        private static readonly Dictionary<string, ChatToken> _tokens =
            new Dictionary<string, ChatToken>(StringComparer.OrdinalIgnoreCase);

        static ChatTokens()
        {
            // ---- wired ----

            // Both name a place. Upstream distinguished them by case convention rather than by
            // meaning ({place} upper-cased in red_alert, {dest} lower in the convoy lines), so one
            // resolver serves both and the corpus keeps its own capitalisation.
            Register("place", ChatTokenOwner.Nav, ResolvePlace);
            Register("dest", ChatTokenOwner.Nav, ResolvePlace);

            // "Tessa Ravenwood" is greeted as "Tessa" - nobody hails a surname.
            Register("name", ChatTokenOwner.Identity, ResolveName);

            // ---- reserved: the token is real, the session that fills it is not here ----

            // Economy. {short} is COIN, not a short name: trade_short.txt reads "need {short}
            // more", the gap between an offer and a price, and it always travels with {price}.
            Register("price", ChatTokenOwner.Economy, null);
            Register("item", ChatTokenOwner.Economy, null);
            Register("mat", ChatTokenOwner.Economy, null);
            Register("short", ChatTokenOwner.Economy, null);

            Register("pet", ChatTokenOwner.Taming, null);

            Register("dungeon", ChatTokenOwner.Adventurer, null);

            // The gossip vocabulary. These appear ONLY in Gossip/, which is not loaded as ambient
            // chatter at all - so they are doubly unreachable, and both guards are deliberate.
            Register("actor", ChatTokenOwner.Journal, null);
            Register("other", ChatTokenOwner.Journal, null);
            Register("when", ChatTokenOwner.Journal, null);
        }

        private static void Register(string name, ChatTokenOwner owner, Func<ChatTokenContext, string> resolver)
        {
            _tokens[name] = new ChatToken(name, owner, resolver);
        }

        public static IEnumerable<ChatToken> All
        {
            get { return _tokens.Values; }
        }

        public static bool IsKnown(string name)
        {
            return name != null && _tokens.ContainsKey(name);
        }

        // ---- resolvers ----

        /// <summary>
        /// A destination's human-readable name - "Britain Bank", "The Blue Boar".
        ///
        /// NavDestination.Name, not its id: the ids read like database keys
        /// ("brit-tavern-blue-boar") and these lines are sentences. Null when the bot is not
        /// associated with a destination, which the runtime guard turns into "do not say this
        /// line" rather than a sentence with a hole in it.
        /// </summary>
        private static string ResolvePlace(ChatTokenContext context)
        {
            if (context == null || String.IsNullOrEmpty(context.DestinationId))
            {
                return null;
            }

            NavDestination destination = Nav.Destination(context.DestinationId);

            return destination == null ? null : destination.Name;
        }

        private static string ResolveName(ChatTokenContext context)
        {
            if (context == null || context.Other == null)
            {
                return null;
            }

            return FirstName(context.Other.Name);
        }

        /// <summary>"Tessa Ravenwood" -> "Tessa". Public because the responder matches on it too.</summary>
        public static string FirstName(string name)
        {
            if (String.IsNullOrEmpty(name))
            {
                return name;
            }

            int space = name.IndexOf(' ');

            return space > 0 ? name.Substring(0, space) : name;
        }

        // ---- classification ----

        /// <summary>
        /// Which of the three classes this line falls into, from its token names alone.
        ///
        /// Called once per line at load, never at speak time.
        /// </summary>
        public static ChatLineClass Classify(string line)
        {
            ChatTokenOwner ignored;
            return Classify(line, out ignored);
        }

        /// <summary>
        /// As Classify, and for a Reserved line also reports WHICH session is holding it back -
        /// the first unwired owner found, which is what Bots.Chat groups the reserved count by.
        /// </summary>
        public static ChatLineClass Classify(string line, out ChatTokenOwner blockedBy)
        {
            blockedBy = ChatTokenOwner.Nav;

            if (String.IsNullOrEmpty(line))
            {
                return ChatLineClass.Wired;
            }

            bool reserved = false;
            int index = 0;

            while (true)
            {
                string name;

                if (!NextToken(line, ref index, out name))
                {
                    break;
                }

                ChatToken token;

                // Unknown beats reserved: a typo is a fault to fix, a reserved line is just early.
                if (!_tokens.TryGetValue(name, out token))
                {
                    return ChatLineClass.Unknown;
                }

                if (!token.IsWired && !reserved)
                {
                    reserved = true;
                    blockedBy = token.Owner;
                }
            }

            return reserved ? ChatLineClass.Reserved : ChatLineClass.Wired;
        }

        /// <summary>Every unregistered token in the line, for the warning that names them.</summary>
        public static List<string> UnknownTokensIn(string line)
        {
            var unknown = new List<string>();

            if (String.IsNullOrEmpty(line))
            {
                return unknown;
            }

            int index = 0;

            while (true)
            {
                string name;

                if (!NextToken(line, ref index, out name))
                {
                    break;
                }

                if (!_tokens.ContainsKey(name) && !unknown.Contains(name))
                {
                    unknown.Add(name);
                }
            }

            return unknown;
        }

        // ---- substitution ----

        /// <summary>
        /// Substitute every token, or refuse the line.
        ///
        /// THE REFUSAL IS THE POINT. False means "do not say this", and every caller must honour
        /// it: a half-substituted line is the one outcome worse than silence, because "WTS {item}"
        /// in a bot's speech hue is unmistakably a machine talking.
        ///
        /// A line with no tokens at all - which is every line in every category wired today -
        /// short-circuits without allocating.
        /// </summary>
        public static bool TryResolve(string line, ChatTokenContext context, out string resolved)
        {
            resolved = line;

            if (String.IsNullOrEmpty(line))
            {
                return false;
            }

            if (line.IndexOf('{') < 0)
            {
                return true;
            }

            var builder = new StringBuilder(line.Length + 16);
            int index = 0;

            while (index < line.Length)
            {
                int open = line.IndexOf('{', index);

                if (open < 0)
                {
                    builder.Append(line, index, line.Length - index);
                    break;
                }

                int close = line.IndexOf('}', open + 1);

                if (close < 0)
                {
                    // An unbalanced brace. Not a token, but not something to say either.
                    return false;
                }

                builder.Append(line, index, open - index);

                string name = line.Substring(open + 1, close - open - 1);

                ChatToken token;

                if (!_tokens.TryGetValue(name, out token) || !token.IsWired)
                {
                    return false;
                }

                string value = token.Resolver(context);

                if (String.IsNullOrEmpty(value))
                {
                    // Wired, but not resolvable for THIS bot right now - no destination, no
                    // listener. Refusing is right; the alternative is a gap in the sentence.
                    return false;
                }

                builder.Append(value);

                index = close + 1;
            }

            resolved = builder.ToString();

            // Belt and braces against a resolver that returns a token of its own.
            return resolved.IndexOf('{') < 0;
        }

        /// <summary>
        /// Scan forward to the next {token}, returning its bare name.
        ///
        /// No regex: this runs over every line of a 1,724-line corpus at every load and reload.
        /// </summary>
        private static bool NextToken(string line, ref int index, out string name)
        {
            name = null;

            if (index >= line.Length)
            {
                return false;
            }

            int open = line.IndexOf('{', index);

            if (open < 0)
            {
                index = line.Length;
                return false;
            }

            int close = line.IndexOf('}', open + 1);

            if (close < 0)
            {
                index = line.Length;
                return false;
            }

            name = line.Substring(open + 1, close - open - 1);
            index = close + 1;

            return true;
        }
    }
}
