using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Server.Custom.MapExport
{
    /// <summary>One row of the shard's world-item snapshot: enough to draw it like a static.</summary>
    internal struct WorldItem
    {
        public int ID;
        public int Hue;
        public int X;
        public int Y;
        public int Z;
    }

    /// <summary>
    /// The shard's furniture, indexed by the column it stands in.
    ///
    /// The renderer draws the world as the CLIENT FILES have it, which is the world as it shipped.
    /// Everything [Decorate places - forges, anvils, signs, doors, benches - lives in the shard's
    /// own save and in none of those files, so without this the smithy yard at brit-forge renders
    /// as empty paving. Scripts/Custom/Core/Bridge/WorldItemSnapshot.cs is the other half; it
    /// writes Data/Live/world-items.json on request and this reads it.
    ///
    /// PARSED BY HAND, deliberately. The tool has no JSON dependency and the whole point of
    /// PngWriter's eighty lines was not to acquire one; this is our own format, every row is the
    /// same five integer fields, and a scanner for that is shorter than the argument for adding
    /// Newtonsoft to a build that currently has nothing. It is strict about the shape and says so
    /// rather than guessing, because the only thing that writes this file is us.
    ///
    /// INDEXED BY COLUMN, not by block. The renderer asks "what items stand on (x, y)" once per
    /// column it paints, exactly as it asks TileMatrix for the statics there, so the items sort
    /// into the same painter's order by the same key and need no separate pass. Thirty thousand
    /// rows in a dictionary is nothing; blocks would only add a second lookup.
    /// </summary>
    internal sealed class WorldItems
    {
        private readonly Dictionary<int, List<WorldItem>> _byColumn = new Dictionary<int, List<WorldItem>>();
        private readonly int _facetHeight;

        private WorldItems(int facetHeight)
        {
            _facetHeight = facetHeight;
        }

        /// <summary>The snapshot's own identity, which the item tile cache is keyed by.</summary>
        public string Id { get; private set; }

        public string Facet { get; private set; }

        public int Count { get; private set; }

        /// <summary>What the file's header said it holds, for comparison with what was parsed.</summary>
        public int DeclaredCount { get; private set; }

        /// <summary>An empty set, so "no snapshot" needs no null check anywhere downstream.</summary>
        public static WorldItems None(int facetHeight)
        {
            return new WorldItems(facetHeight) { Id = null, Facet = null, Count = 0 };
        }

        public bool Any
        {
            get { return Count > 0; }
        }

        /// <summary>The items standing on one column, or null. Null rather than an empty list
        /// because the overwhelming majority of columns have nothing and allocating for them would
        /// dwarf the data.</summary>
        public List<WorldItem> At(int x, int y)
        {
            List<WorldItem> found;
            return _byColumn.TryGetValue(Key(x, y), out found) ? found : null;
        }

        private int Key(int x, int y)
        {
            return (x * _facetHeight) + y;
        }

        public static WorldItems Load(string path, string facet, int facetHeight, out string error)
        {
            error = null;

            if (!File.Exists(path))
            {
                error = "no snapshot at " + path;
                return None(facetHeight);
            }

            string text;

            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return None(facetHeight);
            }

            var items = new WorldItems(facetHeight);

            items.Id = Field(text, "\"id\":");
            items.Facet = Field(text, "\"facet\":");

            if (items.Id == null)
            {
                error = "the snapshot has no id";
                return None(facetHeight);
            }

            if (!String.Equals(items.Facet, facet, StringComparison.OrdinalIgnoreCase))
            {
                // Loading Felucca's furniture onto Trammel would be silently, plausibly wrong -
                // the two facets share terrain and most decoration, so it would look almost right.
                error = String.Format(
                    "the snapshot is for {0} and this renderer serves {1}", items.Facet, facet);
                return None(facetHeight);
            }

            int declared;
            items.DeclaredCount =
                Int32.TryParse(Number(text, "\"count\":"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out declared) ? declared : -1;

            items.Parse(text);

            // A snapshot that parses to nothing while claiming thousands is the failure that looks
            // like a correctly empty town. Said out loud rather than rendered.
            if (items.DeclaredCount > 0 && items.Count == 0)
            {
                error = String.Format(
                    CultureInfo.InvariantCulture,
                    "the snapshot declares {0} item(s) and none could be parsed", items.DeclaredCount);
                return None(facetHeight);
            }

            return items;
        }

        /// <summary>Pulls a bare number field out of the header.</summary>
        private static string Number(string text, string key)
        {
            int at = text.IndexOf(key, StringComparison.Ordinal);

            if (at < 0)
            {
                return null;
            }

            at += key.Length;

            while (at < text.Length && (text[at] == ' ' || text[at] == '	'))
            {
                at++;
            }

            int start = at;

            while (at < text.Length && text[at] >= '0' && text[at] <= '9')
            {
                at++;
            }

            return at == start ? null : text.Substring(start, at - start);
        }

        /// <summary>Pulls a quoted string field out of the header. Header only - the rows have no
        /// strings in them, so there is nothing further along to collide with.</summary>
        private static string Field(string text, string key)
        {
            int at = text.IndexOf(key, StringComparison.Ordinal);

            if (at < 0)
            {
                return null;
            }

            int open = text.IndexOf('"', at + key.Length);

            if (open < 0)
            {
                return null;
            }

            int close = text.IndexOf('"', open + 1);

            return close < 0 ? null : text.Substring(open + 1, close - open - 1);
        }

        /// <summary>
        /// Scans the rows. Every one carries the same five integer fields in the same order, so the
        /// parser reads five numbers after five keys and does not pretend to be general. A row that
        /// does not match is skipped rather than thrown on: one malformed line should cost one
        /// bench, not the whole town.
        ///
        /// WHITESPACE AFTER A COLON IS TOLERATED, and that is not politeness. The first version
        /// matched `{"i":` literally, which the shard's writer produces and every JSON formatter on
        /// earth does not - so a file that had been through a pretty-printer parsed as zero items
        /// and rendered as an empty town, with no error anywhere. Skipping space is two lines;
        /// finding that silence again would not be.
        /// </summary>
        private void Parse(string text)
        {
            int at = text.IndexOf("\"items\":", StringComparison.Ordinal);

            if (at < 0)
            {
                return;
            }

            while (true)
            {
                at = text.IndexOf("\"i\":", at, StringComparison.Ordinal);

                if (at < 0)
                {
                    return;
                }

                var item = new WorldItem();
                int cursor = at;

                if (!ReadField(text, ref cursor, "\"i\":", out item.ID) ||
                    !ReadField(text, ref cursor, "\"h\":", out item.Hue) ||
                    !ReadField(text, ref cursor, "\"x\":", out item.X) ||
                    !ReadField(text, ref cursor, "\"y\":", out item.Y) ||
                    !ReadField(text, ref cursor, "\"z\":", out item.Z))
                {
                    at += 4;
                    continue;
                }

                Add(item);
                at = cursor;
            }
        }

        private static bool ReadField(string text, ref int cursor, string key, out int value)
        {
            value = 0;

            int at = text.IndexOf(key, cursor, StringComparison.Ordinal);

            if (at < 0)
            {
                return false;
            }

            at += key.Length;

            while (at < text.Length && (text[at] == ' ' || text[at] == '	'))
            {
                at++;
            }

            int start = at;

            if (at < text.Length && text[at] == '-')
            {
                at++;
            }

            while (at < text.Length && text[at] >= '0' && text[at] <= '9')
            {
                at++;
            }

            if (at == start)
            {
                return false;
            }

            if (!Int32.TryParse(
                text.Substring(start, at - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            cursor = at;
            return true;
        }

        private void Add(WorldItem item)
        {
            int key = Key(item.X, item.Y);

            List<WorldItem> column;

            if (!_byColumn.TryGetValue(key, out column))
            {
                column = new List<WorldItem>(2);
                _byColumn[key] = column;
            }

            column.Add(item);
            Count++;
        }
    }
}
