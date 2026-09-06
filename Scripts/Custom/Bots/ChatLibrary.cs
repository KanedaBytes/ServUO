// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// ChatLibrary.cs — the chat corpus, one category per file.
//
// Scans Data/Custom/BotChat for .txt files. Each file is one category, named by
// its stem; each non-comment, non-blank line is one utterance. Editing a .txt
// and running [BotsReload changes what bots say with no restart and no compile.
//
// THE SCAN IS NON-RECURSIVE, AND THAT IS LOAD-BEARING. Gossip/ is a
// subdirectory precisely so its files are never served as ambient chatter: they
// are {actor}/{other}/{place}/{when} templates for the event journal, and a
// recursive scan would have a bot say "{other} killed {actor} at {place}" out
// loud, verbatim. Upstream used a flat Directory.EnumerateFiles for this exact
// reason and so do we. Do not "fix" it to recurse.
//
// EQUAL WEIGHT PER CATEGORY, then uniform within it - upstream's explicit
// choice, kept: flattening the pools instead would let a 40-line file drown an
// 8-line one, and bank_actions is only eight lines precisely because "bank" and
// "withdraw 1000" are meant to punctuate rather than fill.
//
// Every line is classified once at load by ChatTokens. Only WIRED lines enter a
// pool, so a line whose tokens belong to a session that has not landed cannot be
// picked at all - the reserved count is carried for the health check and nothing
// else.

using System;
using System.Collections.Generic;
using System.IO;

namespace Server.Custom
{
    public static class ChatLibrary
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// Resolved against Core.BaseDirectory rather than baked in, matching how the rest of the
        /// tree locates Data files.
        /// </summary>
        public static readonly string ChatDirectory =
            Path.Combine(Core.BaseDirectory, "Data", "Custom", "BotChat");

        /// <summary>The subdirectory that is deliberately NOT scanned. See the header.</summary>
        public const string GossipDirectoryName = "Gossip";

        // ---- the categories this session actually rotates ----
        //
        // Named constants rather than string literals at each call site, so the probe can assert
        // over exactly the set the behaviours speak from and a rename cannot silently mute a bot.

        public const string SmallTalk = "small_talk";
        public const string BankActions = "bank_actions";
        public const string Wtb = "wtb";
        public const string Lfg = "lfg";
        public const string Shopping = "shopping";
        public const string Traveling = "traveling";
        public const string NightTalk = "night_talk";
        public const string Emotes = "emotes";
        public const string RespondName = "respond_name";
        public const string RespondGreet = "respond_greet";
        public const string RespondQuestion = "respond_question";
        public const string RespondWhat = "respond_what";

        /// <summary>
        /// Every category any behaviour or the responder can draw from this session.
        ///
        /// The probe asserts that every one of these loaded lines and that none of those lines is
        /// reserved or unknown. A category missing from the corpus is a silently mute bot, which
        /// is why an empty one is a health warning rather than a shrug.
        ///
        /// NOT wired this session, and each for a reason recorded at its would-be call site:
        /// wts and the shop_/haggle_/trade_/buy_ families (economy), friend_greet
        /// (BotSocialGraph), the tame_ family (taming), dungeon_ and combat_ (adventurer and
        /// combat), and everything in Gossip/ (the event journal).
        /// </summary>
        public static readonly string[] SessionCategories =
        {
            SmallTalk,
            BankActions,
            Wtb,
            Lfg,
            Shopping,
            Traveling,
            NightTalk,
            Emotes,
            RespondName,
            RespondGreet,
            RespondQuestion,
            RespondWhat,
        };

        private static readonly Dictionary<string, List<string>> _categories =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<ChatTokenOwner, int> _reserved =
            new Dictionary<ChatTokenOwner, int>();

        /// <summary>Unknown tokens, each with the file it was found in. For the warning.</summary>
        private static readonly List<string> _unknownTokens = new List<string>();

        /// <summary>Lines that begin with '*'. See RejectedEmotes.</summary>
        private static readonly List<string> _rejectedEmotes = new List<string>();

        /// <summary>category -> lines dropped at load, reserved and unknown together.</summary>
        private static readonly Dictionary<string, int> _dropped =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;
        private static string _lastError;
        private static DateTime? _lastLoadUtc;

        private static int _filesScanned;
        private static int _gossipFiles;
        private static int _totalLines;
        private static int _wiredLines;
        private static int _reservedLines;
        private static int _unknownLines;
        private static int _linesSpoken;

        // ---- census, all read by Bots.Chat ----

        public static bool Loaded { get { return _loaded; } }

        public static string LastError { get { return _lastError; } }

        public static DateTime? LastLoadUtc { get { return _lastLoadUtc; } }

        /// <summary>Top-level .txt files read. Excludes Gossip/ by design.</summary>
        public static int FilesScanned { get { return _filesScanned; } }

        /// <summary>Files present in Gossip/ - counted so the census adds up, never loaded.</summary>
        public static int GossipFiles { get { return _gossipFiles; } }

        public static int TotalLines { get { return _totalLines; } }

        public static int WiredLines { get { return _wiredLines; } }

        public static int ReservedLines { get { return _reservedLines; } }

        public static int UnknownLines { get { return _unknownLines; } }

        public static int CategoryCount { get { return _categories.Count; } }

        /// <summary>Lines actually said by a bot since boot. The proof the layer is alive.</summary>
        public static int LinesSpoken { get { return _linesSpoken; } }

        /// <summary>
        /// The last thing any bot said. Diagnostics only - the probe asserts no token survived
        /// into it, and it is the first thing to look at when a bot says something odd.
        /// </summary>
        public static string LastSpokenLine { get; private set; }

        /// <summary>
        /// How many lines were dropped from a category at load, reserved and unknown together.
        ///
        /// The probe asserts this is zero for every category this session speaks from: a wired
        /// category losing lines means bots are drawing from a pool with holes in it.
        /// </summary>
        public static int DroppedIn(string category)
        {
            int dropped;

            return category != null && _dropped.TryGetValue(category, out dropped) ? dropped : 0;
        }

        public static IList<string> UnknownTokens { get { return _unknownTokens; } }

        /// <summary>
        /// Lines starting with '*'.
        ///
        /// Upstream routed these to Mobile.Emote. This shard has no emote path - every line goes
        /// through Say in the bot's own hue - so an asterisk line would be spoken with its
        /// asterisks showing. Rejecting it at load and reporting it beats saying it.
        /// </summary>
        public static IList<string> RejectedEmotes { get { return _rejectedEmotes; } }

        public static IEnumerable<string> KnownCategories { get { return _categories.Keys; } }

        /// <summary>How many lines each session is holding back, for the health line.</summary>
        public static IDictionary<ChatTokenOwner, int> ReservedByOwner { get { return _reserved; } }

        /// <summary>
        /// Record that a line was said. Every path that calls Mobile.Say on a bot ends here, so
        /// the count is the whole layer's output and not one behaviour's.
        /// </summary>
        public static void NoteSpoken(string line)
        {
            _linesSpoken++;
            LastSpokenLine = line;
        }

        // ---- loading ----

        /// <summary>
        /// Read the corpus. Idempotent, so [BotsReload can call it on a live shard.
        ///
        /// Unlike the JSON configs this does NOT keep the previous corpus on failure. There is no
        /// half-applied state to protect against: a line either parses or is skipped, and the
        /// worst case is a quieter shard rather than a wrong one. A missing directory is reported
        /// by Bots.Chat as a failure, because silent bots have no other symptom.
        /// </summary>
        public static void Load()
        {
            _categories.Clear();
            _reserved.Clear();
            _unknownTokens.Clear();
            _rejectedEmotes.Clear();
            _dropped.Clear();

            _filesScanned = 0;
            _gossipFiles = 0;
            _totalLines = 0;
            _wiredLines = 0;
            _reservedLines = 0;
            _unknownLines = 0;

            _loaded = true;

            if (!Directory.Exists(ChatDirectory))
            {
                _lastError = "directory not found: " + ChatDirectory;
                Log.Error("Chat corpus not found at {0}. Bots will be silent.", ChatDirectory);
                return;
            }

            _lastError = null;

            try
            {
                // Flat, never recursive. See the header.
                foreach (string path in Directory.EnumerateFiles(ChatDirectory, "*.txt"))
                {
                    string category = Path.GetFileNameWithoutExtension(path);
                    List<string> lines = ParseFile(path, category);

                    _filesScanned++;

                    if (lines.Count > 0)
                    {
                        _categories[category] = lines;
                    }
                }

                string gossip = Path.Combine(ChatDirectory, GossipDirectoryName);

                if (Directory.Exists(gossip))
                {
                    // Counted, never read. The census should account for every file on disk, or
                    // the next person to look wonders where the other 22 went.
                    foreach (string path in Directory.EnumerateFiles(gossip, "*.txt"))
                    {
                        _gossipFiles++;
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name + ": " + ex.Message;
                Log.Error(ex, "Chat corpus failed to load from {0}.", ChatDirectory);
                return;
            }

            _lastLoadUtc = DateTime.UtcNow;

            Log.Info(
                "Chat corpus loaded: {0} line(s) across {1} categor(ies) from {2} file(s) "
                + "({3} wired, {4} reserved, {5} unknown; {6} gossip file(s) held back).",
                _totalLines,
                _categories.Count,
                _filesScanned,
                _wiredLines,
                _reservedLines,
                _unknownLines,
                _gossipFiles);
        }

        /// <summary>
        /// One file into one pool, classifying as it goes.
        ///
        /// Only wired lines are returned. Reserved and unknown ones are counted and dropped, so
        /// there is no path by which PickRandom can hand out a line with a token in it.
        /// </summary>
        private static List<string> ParseFile(string path, string category)
        {
            var result = new List<string>();

            string[] raw;

            try
            {
                raw = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                Log.Error("Chat corpus: could not read {0}: {1}", path, ex.Message);
                return result;
            }

            for (int i = 0; i < raw.Length; i++)
            {
                string line = raw[i].Trim();

                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                _totalLines++;

                if (line[0] == '*')
                {
                    // No emote path on this shard; saying it would show the asterisks.
                    if (_rejectedEmotes.Count < 20)
                    {
                        _rejectedEmotes.Add(category + ".txt: " + line);
                    }

                    NoteDropped(category);
                    continue;
                }

                ChatTokenOwner blockedBy;
                ChatLineClass classification = ChatTokens.Classify(line, out blockedBy);

                if (classification == ChatLineClass.Unknown)
                {
                    _unknownLines++;
                    NoteDropped(category);

                    foreach (string token in ChatTokens.UnknownTokensIn(line))
                    {
                        string entry = "{" + token + "} in " + category + ".txt";

                        if (!_unknownTokens.Contains(entry) && _unknownTokens.Count < 20)
                        {
                            _unknownTokens.Add(entry);
                        }
                    }

                    continue;
                }

                if (classification == ChatLineClass.Reserved)
                {
                    _reservedLines++;
                    NoteDropped(category);

                    int count;
                    _reserved.TryGetValue(blockedBy, out count);
                    _reserved[blockedBy] = count + 1;

                    continue;
                }

                _wiredLines++;
                result.Add(line);
            }

            return result;
        }

        private static void NoteDropped(string category)
        {
            int count;
            _dropped.TryGetValue(category, out count);
            _dropped[category] = count + 1;
        }

        // ---- picking ----

        /// <summary>
        /// Every line in one category, for a caller that must CHOOSE rather than take pot luck.
        /// </summary>
        public static IList<string> LinesIn(string category)
        {
            List<string> list;

            if (!_loaded || category == null || !_categories.TryGetValue(category, out list))
            {
                return new string[0];
            }

            return list;
        }

        public static int LineCount(string category)
        {
            List<string> list;

            return category != null && _categories.TryGetValue(category, out list) ? list.Count : 0;
        }

        /// <summary>
        /// A random line from the union of these categories, or null when none of them has
        /// content.
        ///
        /// Each category gets an equal chance and then a line is drawn uniformly from within it -
        /// deliberately not a flattened pool. Upstream's note is worth keeping with the code: it
        /// "keeps small categories (like bank_actions) audible alongside larger ones (like wts)".
        /// </summary>
        public static string PickRandom(params string[] categories)
        {
            if (!_loaded || categories == null || categories.Length == 0)
            {
                return null;
            }

            List<List<string>> nonEmpty = null;

            for (int i = 0; i < categories.Length; i++)
            {
                List<string> list;

                if (categories[i] != null
                    && _categories.TryGetValue(categories[i], out list)
                    && list.Count > 0)
                {
                    if (nonEmpty == null)
                    {
                        nonEmpty = new List<List<string>>();
                    }

                    nonEmpty.Add(list);
                }
            }

            if (nonEmpty == null)
            {
                return null;
            }

            List<string> pool = nonEmpty[Utility.Random(nonEmpty.Count)];

            return pool[Utility.Random(pool.Count)];
        }

        // ---- health ----

        /// <summary>
        /// Bots.Chat - what loaded, what is held back and by whom, and whether anyone has spoken.
        ///
        /// The reserved breakdown is the interesting part. It is not a fault list: it names, per
        /// session, how many lines of already-copied content are waiting on that session to land.
        /// It should fall over the next few sessions and reach zero when the event journal
        /// arrives, and if it ever RISES, a corpus edit has introduced lines nothing can say.
        /// </summary>
        public static HealthResult BuildHealthResult()
        {
            string loaded = _lastLoadUtc.HasValue
                ? _lastLoadUtc.Value.ToString("HH:mm:ss") + "Z"
                : "never";

            if (!_loaded || _lastError != null)
            {
                return HealthResult.Fail(String.Format(
                    "corpus did not load ({0}): {1}. Bots are silent.",
                    ChatDirectory,
                    _lastError ?? "never attempted"));
            }

            string detail = String.Format(
                "corpus {0} file(s) + {1} gossip held back, {2} line(s): {3} wired, {4} reserved{5}, "
                + "{6} unknown. {7} categor(ies) loaded. spoken since boot: {8} ({9} repl(ies)). "
                + "Last load {10}",
                _filesScanned,
                _gossipFiles,
                _totalLines,
                _wiredLines,
                _reservedLines,
                DescribeReserved(),
                _unknownLines,
                _categories.Count,
                _linesSpoken,
                BotSpeechResponder.Replies,
                loaded);

            // Nothing at all. Every bot on the shard is mute and there is no other symptom.
            if (_wiredLines == 0)
            {
                return HealthResult.Fail("no speakable lines loaded. " + detail);
            }

            // A typo'd token. The line it is in can never be spoken, and nothing else would ever
            // tell you - which is exactly the class of bug this registry exists to surface.
            if (_unknownTokens.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "{0} unrecognised token(s): {1} - those lines can never be spoken. {2}",
                    _unknownTokens.Count,
                    String.Join(", ", ToArray(_unknownTokens)),
                    detail));
            }

            // An asterisk line. Upstream would have emoted it; here it would be said with the
            // asterisks showing, so it is dropped and named.
            if (_rejectedEmotes.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "{0} line(s) start with '*' and were rejected (this shard has no emote path): "
                    + "{1}. {2}",
                    _rejectedEmotes.Count,
                    String.Join("; ", ToArray(_rejectedEmotes)),
                    detail));
            }

            // A category a behaviour draws from that has no lines. From the outside this is
            // indistinguishable from a broken bot: it rolls, picks nothing, and stands there mute.
            var empty = new List<string>();

            for (int i = 0; i < SessionCategories.Length; i++)
            {
                if (LineCount(SessionCategories[i]) == 0)
                {
                    empty.Add(SessionCategories[i]);
                }
            }

            if (empty.Count > 0)
            {
                return HealthResult.Warn(String.Format(
                    "{0} category(ies) this session speaks from are empty ({1}) - bots drawing on "
                    + "them are silent. {2}",
                    empty.Count,
                    String.Join(", ", empty.ToArray()),
                    detail));
            }

            return HealthResult.Ok(detail);
        }

        /// <summary>" (economy 214, journal 54)", or empty when nothing is held back.</summary>
        private static string DescribeReserved()
        {
            if (_reserved.Count == 0)
            {
                return String.Empty;
            }

            var parts = new List<string>();

            foreach (KeyValuePair<ChatTokenOwner, int> entry in _reserved)
            {
                parts.Add(String.Format(
                    "{0} {1}",
                    entry.Key.ToString().ToLowerInvariant(),
                    entry.Value));
            }

            parts.Sort(StringComparer.Ordinal);

            return " (" + String.Join(", ", parts.ToArray()) + ")";
        }

        private static string[] ToArray(List<string> list)
        {
            var array = new string[list.Count];
            list.CopyTo(array, 0);

            return array;
        }
    }
}
