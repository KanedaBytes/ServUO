// BotOrphans.cs — who actually owns the items ServUO's boot cleanup reaps.
//
// THE CLAIM THIS EXISTS TO TEST. Every boot, Scripts/Misc/Cleanup.cs prints a line like
//
//     Cleanup: Detected 136 inaccessible items, removing..
//
// followed by one line per item, and the suspicion was that these are the packs and purses of
// PlayerBots, which delete themselves at the tail of Deserialize because nothing about a bot
// survives a restart. If so, the fix would be for the bot's delete to take its items with it.
//
// TWO THINGS SAY OTHERWISE, AND BOTH ARE CHEAPER THAN A FIX.
//
//   1. Mobile.Delete ALREADY cascades. Server/Mobile.cs:3747-3817 runs OnDelete, then
//      m_Items[i].OnParentDeleted(this) for every item, and Item.OnParentDeleted (Item.cs:4365)
//      is Delete(), which recurses into its own contents. Backpack, pack contents, every equipped
//      layer and the bank box, already. There is no drop-to-ground path on mobile deletion - that
//      happens on DEATH, through corpse construction.
//
//   2. The composition is wrong for a bot. Six boots of this shard reaped 30, 288, 50, 240, 240
//      and 136 items, and EVERY ONE was exactly half Backpack and half Gold, one to one, with no
//      other type at all and with adjacent serials in unbroken runs. A PlayerBot cannot produce
//      that: its pack is made at PlayerBot.cs:455 and its gold only after RollClassLook,
//      RollUniversalAccessories and EnsureFootwear have allocated six to twenty items
//      (EquipmentTable.cs:42-48, gold at :77), so a bot's orphans would carry shoes, a robe, a
//      tool and a bag. One backpack and one gold pile with nothing in between is the signature of
//      BaseCreature.AddLoot / PackGold (BaseCreature.cs:5448-5459, :5689-5710), which allocate
//      exactly those two and nothing else.
//
// So this does not fix anything. It NAMES the owner, which is the one thing the console line
// cannot: Item.ToString is serial plus class name (Item.cs:6151) and says nothing about whose it
// was. It reads Item.Parent, which is still set on an item whose Map has gone null - the only
// branch of Cleanup's rule that can catch a Backpack at all (Cleanup.cs:26-30) - and reports the
// owning mobile by type.
//
// WHEN IT RUNS, and the window is narrow on purpose. Cleanup.Run is a Timer.DelayCall of 2.5
// seconds from Initialize (Cleanup.cs:11-14). A bot's own Timer.DelayCall(Delete) has fired well
// before that, so scanning at Initialize would see the bots still alive and answer a different
// question. Two seconds is after the bots have gone and before Cleanup takes the evidence away.
//
// Scripts/Misc/Cleanup.cs is UPSTREAM and is not edited. This reads the same world with the same
// rule and writes its own answer.

using System;
using System.Collections.Generic;
using System.Text;

using Server.Commands;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    public static class BotOrphans
    {
        private static readonly CustomLogger Log = CustomLogger.For("Core");

        public const string SnapshotPath = "Data/Live/bot-orphans.json";

        /// <summary>Half a second before Cleanup.Run, which is at 2.5s. See the header.</summary>
        private static readonly TimeSpan ScanDelay = TimeSpan.FromSeconds(2.0);

        public static void Initialize()
        {
            CommandSystem.Register("BotOrphans", AccessLevel.Administrator, OnCommand);

            if (Config.Get("Custom.BotOrphanScanOnStart", false))
            {
                Timer.DelayCall(ScanDelay, delegate { Report(null); });
            }
        }

        [Usage("BotOrphans")]
        [Description("Names the owner of every item ServUO's boot cleanup would reap, and says how many came from a PlayerBot.")]
        private static void OnCommand(CommandEventArgs e)
        {
            Report(e.Mobile);
        }

        /// <summary>
        /// Cleanup's own selection rule, with the owner attached.
        ///
        /// Only the first branch is reproduced - `item.Map == null` - because it is the only one
        /// that can catch a container, and a container is what the question is about. The rest of
        /// Cleanup's rule (IsBuggable, bank boxes, Point3D.Zero) selects loose decoration and is a
        /// different investigation.
        /// </summary>
        public static IList<string> Scan(out int total, out int fromBots)
        {
            var byOwner = new Dictionary<string, int>(StringComparer.Ordinal);
            var byType = new Dictionary<string, int>(StringComparer.Ordinal);

            total = 0;
            fromBots = 0;

            // World.Items, and the justification the conventions ask for: there is no other way to
            // ask "which items have no facet". A sector list is indexed BY facet, so an item whose
            // map is null is in none of them - which is the whole shape of the fault.
            var items = new List<Item>(World.Items.Values);

            foreach (Item item in items)
            {
                if (item == null || item.Deleted || item.Map != null)
                {
                    continue;
                }

                total++;

                object parent = item.Parent;
                object root = item.RootParent;

                string owner = Describe(root ?? parent);

                Bump(byOwner, owner);
                Bump(byType, item.GetType().Name);

                if (root is PlayerBot || parent is PlayerBot)
                {
                    fromBots++;
                }
            }

            var lines = new List<string>();

            lines.Add(String.Format(
                "{0} item(s) with no facet - what Cleanup will reap in a moment. {1} belong to a PlayerBot.",
                total, fromBots));

            foreach (KeyValuePair<string, int> pair in Sorted(byType))
            {
                lines.Add(String.Format("  type  {0,-24} {1}", pair.Key, pair.Value));
            }

            foreach (KeyValuePair<string, int> pair in Sorted(byOwner))
            {
                lines.Add(String.Format("  owner {0,-24} {1}", pair.Key, pair.Value));
            }

            return lines;
        }

        private static void Report(Mobile from)
        {
            int total, fromBots;

            IList<string> lines = Scan(out total, out fromBots);

            var builder = new StringBuilder(512);

            builder.Append("{\n");
            builder.Append("  \"utc\": ").Append(Json.Quote(DateTime.UtcNow.ToString("o"))).Append(",\n");
            builder.Append("  \"total\": ").Append(total).Append(",\n");
            builder.Append("  \"fromBots\": ").Append(fromBots).Append(",\n");
            builder.Append("  \"lines\": [");

            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(Json.Quote(lines[i]));
            }

            builder.Append("]\n}\n");

            string error;

            if (!AtomicFile.Write(SnapshotPath, builder.ToString(), out error))
            {
                Log.Error("Could not write {0}: {1}", SnapshotPath, error);
            }

            foreach (string line in lines)
            {
                Log.Info(line);

                if (from != null)
                {
                    from.SendMessage(0x40, line);
                }
            }
        }

        /// <summary>
        /// What kind of thing owned this item, as a type name rather than a name.
        ///
        /// A type is the answer the question needs - "a Backpack whose owner was an Orc" settles
        /// it, where "Grunk's backpack" does not - and by the time this runs the owner is usually
        /// deleted, so its Name may already be gone while its type never is.
        /// </summary>
        private static string Describe(object owner)
        {
            if (owner == null)
            {
                return "(no parent)";
            }

            var mobile = owner as Mobile;

            if (mobile != null)
            {
                return mobile.GetType().Name + (mobile.Deleted ? " (deleted)" : "");
            }

            var item = owner as Item;

            return item != null ? item.GetType().Name : owner.GetType().Name;
        }

        private static void Bump(Dictionary<string, int> counts, string key)
        {
            int already;

            counts[key] = counts.TryGetValue(key, out already) ? already + 1 : 1;
        }

        private static List<KeyValuePair<string, int>> Sorted(Dictionary<string, int> counts)
        {
            var list = new List<KeyValuePair<string, int>>(counts);

            list.Sort(delegate(KeyValuePair<string, int> x, KeyValuePair<string, int> y)
            {
                return y.Value.CompareTo(x.Value);
            });

            return list;
        }
    }
}
