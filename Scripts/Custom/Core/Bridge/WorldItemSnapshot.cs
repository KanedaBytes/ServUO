using System;
using System.Text;

using Server.Commands;

namespace Server.Custom
{
    /// <summary>
    /// Every item the isometric art view should draw, to Data/Live/world-items.json.
    ///
    /// WHY THE ART VIEW NEEDS THIS AT ALL. tools/MapExport renders from the client's map and
    /// statics files, so it draws the world as it shipped. Everything [Decorate places - forges,
    /// anvils, signs, doors, benches, bookcases - is a runtime item in the shard's own save, in
    /// none of those files, and so the smithy yard at brit-forge rendered as empty paving with the
    /// nav markers floating on it. This is the missing half: the shard says where its own furniture
    /// is, and the renderer draws it exactly as it draws a static.
    ///
    /// WHAT COUNTS, and why it is a shape rather than a list of types:
    ///
    ///     Parent  == null      on the ground, not in a pack and not on a mobile
    ///     Map     == a facet   and not Map.Internal
    ///     Movable == false     decoration, doors, signs, addon components, benches
    ///     Visible == true
    ///     Spawner == null      not spawner output
    ///     ItemID  &gt; 1          an addon HOST is ItemID 1 and invisible; belt and braces
    ///
    /// Six items in seven are gone before anything else looks at them: the world holds 217,129
    /// items and 80,356 of them are on a facet at all. In the Britain box (1380-1745, 1495-1840 on
    /// Trammel) that leaves 2,947, of which 2,830 are drawable - the 117 excluded are 90
    /// XmlSpawners and the invisible addon hosts, and exactly one item in the box is movable.
    ///
    /// A TYPE ALLOWLIST WAS THE OTHER OPTION AND IS WORSE. It is a list to maintain, and anything
    /// the shard adds later is silently missing until somebody notices a hole in a render. There is
    /// also a real decoration marker available - WeakEntityCollection keyed 'deco'/'door'/'sign',
    /// 53,791 entries - and it is deliberately unused for the same reason: it would miss the 143
    /// items in the Britain box that belong to no collection, including hand-placed ones.
    ///
    /// ADDONS NEED NO SPECIAL HANDLING. BaseAddon.AddComponent calls
    /// c.MoveToWorld(new Point3D(X + x, Y + y, Z + z), Map) - Scripts/Items/Addons/BaseAddon.cs:59 -
    /// so every component is already an Item in World.Items at real world coordinates with its own
    /// ItemID and Hue. Offset exists so the host can drag the set, not so anyone can compute a draw
    /// position. A plain walk sees them all.
    ///
    /// ON AN EXPLICIT REQUEST ONLY, NEVER A TIMER. Finding items means walking World.Items, and
    /// this shard reserves that for explicit commands (CLAUDE.md section 15). SpawnerSnapshot is
    /// the one timer-driven exception and argues its own case; this is not it, and it does not need
    /// to be - decoration does not move, so a snapshot is good until somebody decorates again.
    /// </summary>
    public static class WorldItemSnapshot
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bridge");

        public const string OutputPath = "Data/Live/world-items.json";

        /// <summary>An addon host is ItemID 1 and invisible; nothing real is below this.</summary>
        private const int MinDrawableItemId = 2;

        private static long _sequence;
        private static DateTime? _lastUtc;
        private static int _lastCount;
        private static string _lastError;

        public static void Initialize()
        {
            CommandSystem.Register("WorldItems", AccessLevel.Administrator, WorldItems_OnCommand);
            HealthCheck.Register("Bridge.WorldItems", BuildHealthResult);
        }

        [Usage("WorldItems [facet]")]
        [Description("Writes the art view's world-item snapshot. Defaults to the shard's primary facet.")]
        private static void WorldItems_OnCommand(CommandEventArgs e)
        {
            string facet = e.Arguments.Length > 0 ? e.Arguments[0] : null;

            string message;

            if (!TryWrite(facet, out message))
            {
                e.Mobile.SendMessage(0x22, message);
                return;
            }

            e.Mobile.SendMessage(0x40, message);
        }

        /// <summary>
        /// Writes the snapshot for one facet. Returns false with a reason rather than throwing, so
        /// the request token and the command can both say what went wrong.
        /// </summary>
        public static bool TryWrite(string facetName, out string message)
        {
            Map map;

            if (!TryResolveFacet(facetName, out map, out message))
            {
                return false;
            }

            try
            {
                _sequence++;

                int count;
                string contents = Build(map, out count);

                string error;

                if (!AtomicFile.Write(OutputPath, contents, out error))
                {
                    _lastError = error;
                    message = "could not write " + OutputPath + ": " + error;
                    Log.Error("Could not write {0}: {1}", OutputPath, error);
                    return false;
                }

                _lastError = null;
                _lastUtc = DateTime.UtcNow;
                _lastCount = count;

                message = String.Format("{0} world item(s) on {1} written to {2}",
                    count, map.Name, OutputPath);

                Log.Info(message);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                message = "world item snapshot failed: " + ex.Message;
                Log.Error(ex, "World item snapshot failed.");
                return false;
            }
        }

        /// <summary>
        /// The facet to snapshot. Defaults to Custom.PrimaryFacet, which is Trammel on this shard -
        /// SHARD.md says every custom system targets it unless stated otherwise.
        /// </summary>
        private static bool TryResolveFacet(string facetName, out Map map, out string message)
        {
            map = null;
            message = null;

            string wanted = facetName;

            if (String.IsNullOrEmpty(wanted))
            {
                wanted = Config.Get("Custom.PrimaryFacet", "Trammel");
            }

            // Map.Parse THROWS on an unknown name (CLAUDE.md section 14), so the lookup is done by
            // hand against the registered maps rather than caught.
            foreach (Map candidate in Map.AllMaps)
            {
                if (candidate == null || candidate == Map.Internal)
                {
                    continue;
                }

                if (String.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    map = candidate;
                    return true;
                }
            }

            message = "unknown facet '" + wanted + "'";
            return false;
        }

        private static string Build(Map map, out int count)
        {
            var builder = new StringBuilder(1024 * 512);
            var rows = new StringBuilder(1024 * 512);

            count = 0;

            // WALKING World.Items, on an explicit request only (CLAUDE.md section 15). There is no
            // index of immovable world items and nowhere of ours to hang one - Item is upstream, so
            // a hook would need an entry in MODIFICATIONS.md. The alternative, a Map.GetItemsInBounds
            // over the whole facet, walks the same items through the sector lists and adds a bounds
            // test per item; for a whole facet that is strictly more work, not less.
            foreach (Item item in World.Items.Values)
            {
                if (!Qualifies(item, map))
                {
                    continue;
                }

                if (count > 0)
                {
                    rows.Append(",\n");
                }

                AppendItem(rows, item);
                count++;
            }

            builder.Append("{\n");
            builder.Append("  \"sequence\": ").Append(_sequence).Append(",\n");
            builder.Append("  \"utc\": \"").Append(DateTime.UtcNow.ToString("o")).Append("\",\n");

            // The identity the art view's item tile layer is keyed by. A new snapshot is a new id
            // is a new directory, so a stale render can never be served as a current one.
            builder.Append("  \"id\": ").Append(Json.Quote(BuildId(map, count))).Append(",\n");
            builder.Append("  \"facet\": ").Append(Json.Quote(map.Name)).Append(",\n");
            builder.Append("  \"count\": ").Append(count).Append(",\n");
            builder.Append("  \"items\": [\n");
            builder.Append(rows);
            builder.Append("\n  ]\n}\n");

            return builder.ToString();
        }

        private static string BuildId(Map map, int count)
        {
            return String.Format(
                "{0}-{1}-{2:yyyyMMddHHmmss}-{3}",
                map.Name.ToLowerInvariant(), _sequence, DateTime.UtcNow, count);
        }

        /// <summary>The filter, in the order that discards the most for the least work.</summary>
        private static bool Qualifies(Item item, Map map)
        {
            if (item == null || item.Deleted)
            {
                return false;
            }

            // Parent == null is exactly the set that lives in the map's sectors: Item.OnParentChanged
            // drives Map.OnEnter/OnLeave off this very condition. Anything in a pack or on a mobile
            // fails here, which is most of the world.
            if (item.Parent != null)
            {
                return false;
            }

            if (item.Map != map)
            {
                return false;
            }

            if (item.Movable || !item.Visible)
            {
                return false;
            }

            if (item.ItemID < MinDrawableItemId)
            {
                return false;
            }

            // Spawner output. Immovable spawned items are rare but real, and a snapshot of the
            // furniture should not include whatever a spawn point happens to be holding right now.
            return item.Spawner == null;
        }

        /// <summary>
        /// One row, and no more than the renderer draws with. It positions the sprite from the art
        /// files by ItemID, tints it by Hue and sorts it by Z against the statics around it, so
        /// there is nothing else to send. Compact on purpose: 34,579 rows for Trammel.
        /// </summary>
        private static void AppendItem(StringBuilder builder, Item item)
        {
            builder.Append("    {\"i\":").Append(item.ItemID);
            builder.Append(",\"h\":").Append(item.Hue);
            builder.Append(",\"x\":").Append(item.X);
            builder.Append(",\"y\":").Append(item.Y);
            builder.Append(",\"z\":").Append(item.Z);
            builder.Append('}');
        }

        private static HealthResult BuildHealthResult()
        {
            if (_lastError != null)
            {
                return HealthResult.Fail("World item snapshot: " + _lastError);
            }

            if (_lastUtc == null)
            {
                // Not an error. The art view's item layer is simply off until somebody asks for a
                // snapshot, and the editor says so rather than quietly rendering without furniture.
                return HealthResult.Ok("No world item snapshot taken yet.");
            }

            return HealthResult.Ok(String.Format(
                "{0} item(s), written {1:F0}s ago.",
                _lastCount, (DateTime.UtcNow - _lastUtc.Value).TotalSeconds));
        }
    }
}
