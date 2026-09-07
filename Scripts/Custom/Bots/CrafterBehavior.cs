// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// CrafterBehavior.cs — a Smith, Tailor or Carpenter at its station, making real things.
//
// Upstream's structure, kept: this is the ENGINE, CrafterProfiles is the data,
// and the behaviour does three things — anchor at the station, work on a
// cadence, and talk about it. There is no state machine, deliberately; a crafter
// that has arrived is simply standing there working until its visit expires.
//
// WHAT IS DIFFERENT, and it is the substance of this session:
//
//   Upstream        CrafterProduction.TryProduce: roll MakeChance by tier (2%
//                   to 25%), roll a band, call `new Dagger()`, consume a flat
//                   6-20 "materials", stamp the maker's mark by hand at GM.
//
//   Here            pick a band by the same tier weights, look the type up in
//                   ServUO's CraftSystem, and hand the whole attempt to
//                   CraftItem.Craft. The engine does the skill check, consumes
//                   the real ingots through ConsumeRes, decides exceptional,
//                   applies the maker's mark, and fails when it fails.
//
// Their MakeChance roll is GONE — a real skill check is a better version of the
// same idea, and keeping both would have squared the odds. What survives is
// their CADENCE: a work cycle every 6-14 seconds. That is now the only rate
// limiter, and it is needed, because ServUO's craft delay is 1.25s for all three
// systems and a Grandmaster left to run flat out would bury the shard in daggers.
//
// THREE ENGINE FACTS THAT SHAPE THE CODE:
//
//   1. Craft is ASYNCHRONOUS. It starts CraftItem.InternalTimer and returns; the
//      item appears ~1.25s later in CompleteCraft. So nothing here can read a
//      result directly — the ledger counts what is in the pack instead.
//   2. Craft takes `from.BeginAction(typeof(CraftSystem))` and releases it in
//      the timer. Two overlapping attempts are refused, which is another reason
//      the cadence is not optional.
//   3. AutoCraftTimer — the engine's own "make N" loop — bails outright on
//      `NetState == null` (AutoCraft.cs:110). A bot must never be handed to it.

using System;
using System.Collections.Generic;

using Server.Engines.Craft;
using Server.Items;

namespace Server.Custom
{
    public class CrafterBehavior : PlayerBotBehavior
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>Upstream's work-cycle cadence. The only thing rate-limiting production now.</summary>
        private static readonly TimeSpan CraftMin = TimeSpan.FromSeconds(6.0);
        private static readonly TimeSpan CraftMax = TimeSpan.FromSeconds(14.0);

        /// <summary>
        /// How many finished pieces a crafter keeps before it stops making more.
        ///
        /// Upstream's number, but not upstream's mechanism. Theirs deleted the oldest and paid
        /// the bot gold for it — a sell-off — and selling is 7f. Here the cap simply STOPS the
        /// work: the goods stay in the pack where the brief wants them, and a full crafter
        /// standing idle at a full bench is a truthful picture of one.
        /// </summary>
        public const int PackCap = 12;

        /// <summary>How long a dry spell runs before the bot starts saying so.</summary>
        private static readonly TimeSpan DryQuiet = TimeSpan.FromSeconds(20.0);

        private static readonly string[] SmithChat = { "smith_talk", "craft_talk" };
        private static readonly string[] TailorChat = { "tailor_talk", "craft_talk" };
        private static readonly string[] CarpenterChat = { "carpenter_talk", "craft_talk" };
        private static readonly string[] CraftChat = { "craft_talk" };

        private CrafterProfile _profile;
        private NavWalker _walker;
        private bool _walkingToStation;
        private Point3D _anchor;
        private long _nextCraft;
        private long _nextNeedLine;
        private DateTime? _drySince;
        private int _madeSeen;
        private int _exceptionalSeen;

        /// <summary>Where it is stationed, as a nav destination id. Set by the arrival handoff.</summary>
        public string DestinationId { get; set; }

        /// <summary>Attempts started since attaching, for [BotBehavior and the probe.</summary>
        public int Attempts { get; private set; }

        /// <summary>Pieces that appeared in the pack since attaching.</summary>
        public int Made { get; private set; }

        /// <summary>
        /// Why the last attempt could not even be started, or null. A cliloc number, in words.
        ///
        /// This means SOMETHING IS WRONG — no tool, the wrong tool, not beside an anvil. It
        /// deliberately does not cover the two ordinary reasons a crafter is idle, being out of
        /// materials and having a full bench, because those are the system working. Folding them
        /// in here once made the work probe fail on a successful run.
        /// </summary>
        public string Blocked { get; private set; }

        /// <summary>The bench holds all it is going to. Not a fault: the designed resting state.</summary>
        public bool IsFull { get; private set; }

        /// <summary>True while the bot has nothing left to work with.</summary>
        public bool IsDry
        {
            get { return _drySince != null; }
        }

        public CrafterBehavior()
        {
            ChatCategories = CraftChat;
            ChatChance = 0.18;
            MinChatCooldown = TimeSpan.FromSeconds(20.0);
            MaxChatCooldown = TimeSpan.FromSeconds(50.0);
        }

        /// <summary>The walker steering this bot mid-journey, or null. See PlayerBotBehavior.</summary>
        public override NavWalker Walker
        {
            get { return _walker; }
        }

        public override string SerializableName
        {
            get { return "Crafter"; }
        }

        public override string CurrentDestinationId
        {
            get { return DestinationId; }
        }

        /// <summary>Late-night lines at the bench, upstream's own category.</summary>
        protected override string NightTalkCategory
        {
            get { return "craft_night"; }
        }

        /// <summary>
        /// A crafter is never too busy to be moved on.
        ///
        /// It is not mid-walk and it holds no engine lock between cycles, so unlike the Traveler
        /// it has nothing to protect. Its visit window does the holding: 180 to 360 minutes, which
        /// is upstream's, and long enough that the lifecycle almost never gets the chance.
        /// </summary>
        public override bool CanTransition(PlayerBot bot)
        {
            // Free once settled - it holds no engine lock between cycles. Not while it is walking
            // to its bench, though: that is a walker mid-route, exactly as for a Traveler.
            return !_walkingToStation;
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);

            // Working a forge or a loom, on foot.
            BotMovement.Settle(bot);

            _profile = CrafterProfiles.For(bot);
            _anchor = bot.Location;

            // TradeClass, not Class: a legacy Crafter-class bot still talks like the smith,
            // tailor or carpenter its sub-type makes it.
            ChatCategories = bot.TradeClass == BotClass.Smith ? SmithChat
                           : bot.TradeClass == BotClass.Tailor ? TailorChat
                           : bot.TradeClass == BotClass.Carpenter ? CarpenterChat
                           : CraftChat;

            // A Crafter reached by the arrival handoff is already standing on a validated arrival
            // tile. One attached by hand - [BotBehavior Crafter - is standing wherever it happened
            // to be, which for a Smith means "inside the shop, four tiles from the anvil, unable
            // to work and unable to say why". So: find the station, and walk to it.
            if (String.IsNullOrEmpty(DestinationId))
            {
                DestinationId = FindStation(bot);
            }

            if (!AtStation(bot))
            {
                if (BeginWalkToStation(bot))
                {
                    return;
                }

                Blocked = DestinationId != null
                    ? "it cannot reach its station"
                    : CrafterProfiles.For(bot) == null
                        ? BotClassHelper.DisplayName(bot.Class) + " has no crafting station"
                        : "there is no " + BotClassHelper.StationFor(bot) + " on this facet";
            }

            Settle(bot);
        }

        /// <summary>The station this bot's trade works, as a destination id, or null.</summary>
        private static string FindStation(PlayerBot bot)
        {
            List<NavDestination> usable =
                BotWorkSites.Available(bot.Map, BotClassHelper.StationFor(bot));

            NavDestination best = null;
            int bestDistance = Int32.MaxValue;

            foreach (NavDestination candidate in usable)
            {
                int distance = NavGraph.Chebyshev(candidate.Location, bot.Location);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return best == null ? null : best.Id;
        }

        /// <summary>Is the bot standing on one of its station's authored arrival tiles?</summary>
        private bool AtStation(PlayerBot bot)
        {
            NavDestination station = DestinationId == null ? null : Nav.Destination(DestinationId);

            if (station == null || station.ArrivalList == null)
            {
                return false;
            }

            foreach (NavArrival arrival in station.ArrivalList)
            {
                if (bot.Location == arrival.Location)
                {
                    return true;
                }
            }

            return false;
        }

        private bool BeginWalkToStation(PlayerBot bot)
        {
            if (String.IsNullOrEmpty(DestinationId))
            {
                return false;
            }

            NavRoute route;
            string error;

            if (!Nav.TryRouteFrom(bot.Location, bot.Map, DestinationId, bot, out route, out error))
            {
                Log.Debug("{0} cannot route to its station '{1}': {2}", bot.Name, DestinationId, error);
                return false;
            }

            if (_walker == null)
            {
                _walker = new NavWalker(bot);
                _walker.Arrived = OnReachedStation;
                LogWalker(bot, _walker);
            }

            bot.Commuting = true;
            _walkingToStation = true;
            _walker.Follow(route);

            return true;
        }

        private void OnReachedStation(NavWalker walker)
        {
            var bot = walker.Mobile as PlayerBot;

            if (bot == null || bot.Deleted)
            {
                return;
            }

            bot.Commuting = false;
            _walkingToStation = false;

            Settle(bot);
        }

        /// <summary>
        /// Take up the bench: anchor here, face the work, and start the cadence.
        ///
        /// Hold the tile with RangeHome 0 and a non-zero Home, which makes BaseAI.WalkRandomInHome
        /// walk straight back whenever the bot is not standing on it (BaseAI.cs:2573-2578) -
        /// upstream's drift-back, done by the engine instead of by hand. It matters more for a
        /// Smith than for anyone else: the arrival tile was chosen because it is within two tiles
        /// of both the forge and the anvil, and one step off is one step too far.
        /// </summary>
        private void Settle(PlayerBot bot)
        {
            _anchor = bot.Location;
            bot.Home = _anchor;
            bot.RangeHome = 0;

            if (bot.TradeClass == BotClass.Smith)
            {
                FaceNearestFixture(bot);
            }

            if (_profile == null)
            {
                // Reachable from [BotBehavior Crafter on a class with no trade. Everything below
                // reads the profile's bands, so stop rather than throwing inside a staff command;
                // Tick's own guard leaves it standing there and GetStatusLine says why.
                return;
            }

            // The ledger's baseline has to be taken HERE and not in OnAttached: a bot that walks
            // to its bench first would otherwise bank its starting kit as things it had made.
            _madeSeen = CountMade(bot);
            _exceptionalSeen = CountExceptional(bot);

            ScheduleNextCraft();
        }

        /// <summary>
        /// A behaviour that sets Home MUST clear it.
        ///
        /// PlayerBot's constructor zeroes Home and everything downstream assumes that: a leftover
        /// Home would quietly leash the next Traveler back to this bench whenever it stopped
        /// commuting. BankSitterBehavior carries the same note for the same reason.
        /// </summary>
        public override void OnDetached(PlayerBot bot)
        {
            base.OnDetached(bot);

            if (bot == null)
            {
                return;
            }

            bot.Home = Point3D.Zero;
            bot.RangeHome = 0;
            bot.Commuting = false;
            _walkingToStation = false;

            if (_walker != null)
            {
                _walker.Stop();
                _walker.Arrived = null;
                _walker.RungFired = null;
                _walker = null;
            }
        }

        public override string GetStatusLine(PlayerBot bot)
        {
            NavDestination destination = DestinationId == null ? null : Nav.Destination(DestinationId);
            string where = destination == null ? "its station" : destination.Name;

            if (_walkingToStation)
            {
                return String.Format("walking to {0}", where);
            }

            if (Blocked != null)
            {
                return String.Format("cannot work at {0}: {1}", where, Blocked);
            }

            if (_profile == null)
            {
                return String.Format("standing at {0} with no trade", where);
            }

            if (IsDry)
            {
                return String.Format("out of {0} at {1}", _profile.MaterialNoun, where);
            }

            if (IsFull)
            {
                return String.Format("bench full at {0} - {1} made, {2} on it", where, Made, _madeSeen);
            }

            return String.Format(
                "working at {0} - {1} attempt(s), {2} made, {3} on the bench",
                where,
                Attempts,
                Made,
                _madeSeen);
        }

        public override void Tick(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            if (CheckVisitExpired(bot))
            {
                return;
            }

            if (_walkingToStation)
            {
                // The same watchdog every walking behaviour here carries: NavWalker calls Stop()
                // rather than Finish() when a mobile is deleted or lands off-facet, so waiting
                // only on the callback would leave the bot standing with Commuting still true.
                if (_walker == null || !_walker.Active)
                {
                    BotTickManager.NoteAbandoned();
                    bot.Commuting = false;
                    _walkingToStation = false;
                    Settle(bot);
                }

                return;
            }

            TrySpeak(bot);

            if (_profile == null)
            {
                return;
            }

            // Notice anything the craft timer finished since the last tick. This is how the
            // ledger works at all: Craft is asynchronous, so the only honest way to know whether
            // something was made is to look at the pack.
            NoticeFinished(bot);

            if (Core.TickCount - _nextCraft < 0)
            {
                return;
            }

            ScheduleNextCraft();

            // Let the engine walk it home before working. Standing one tile off the anvil is the
            // difference between smithing and 1044267.
            if (bot.Location != _anchor)
            {
                return;
            }

            IsFull = _madeSeen >= PackCap;

            if (IsFull)
            {
                return;
            }

            if (_profile.Materials.Length > 0 && CrafterStock.Count(bot, _profile) < 1)
            {
                DrySpell(bot);
                return;
            }

            // Having SOMETHING is not the same as having enough - see CostOf, below.


            _drySince = null;

            WorkCycle(bot);
        }

        /// <summary>
        /// One swing: the animation, the sound, and one real craft attempt.
        /// </summary>
        private void WorkCycle(PlayerBot bot)
        {
            try
            {
                bot.Animate(_profile.Action, 5, 1, true, false, 0);
            }
            catch
            {
                // Cosmetic. Never let it break the tick - upstream's note, and a good one.
            }

            if (_profile.Sound > 0)
            {
                try
                {
                    bot.PlaySound(_profile.Sound);
                }
                catch
                {
                }
            }

            TryCraft(bot);
        }

        /// <summary>
        /// Attempt one item through ServUO's own craft system.
        ///
        /// typeRes is passed as null on purpose. It selects BETWEEN sub-resources — which colour
        /// of ingot, which kind of leather — and null means the entry's own default, which is
        /// what a bot working from plain stock wants. It is not the resource itself: that comes
        /// from the CraftItem's declared resource list, which is why a leather craft consumes
        /// leather even though the profile's first material is cloth.
        /// </summary>
        private void TryCraft(PlayerBot bot)
        {
            Type[] band = CrafterProfiles.PickBand(_profile, bot.SkillTier);

            if (band == null || band.Length == 0)
            {
                Blocked = "nothing in its bands is makeable";
                return;
            }

            Type type = band[Utility.Random(band.Length)];
            CraftSystem system = _profile.CraftSystem;

            if (system == null)
            {
                Blocked = "no craft system bound";
                return;
            }

            CraftItem entry = system.CraftItems.SearchFor(type);

            if (entry == null)
            {
                Blocked = "no craft entry for " + type.Name;
                return;
            }

            // Can it AFFORD this? CanCraft answers "is the bench right", never "is there enough
            // metal" - that check lives inside Craft, where a shortfall just fails quietly. A
            // smith holding two ingots and reaching for a three-ingot dagger therefore burns an
            // attempt every cycle and reports nothing; the first fixed work probe caught it doing
            // that thirty-five times in a row. Ask up front, and go dry instead.
            int cost = CostOf(entry);

            if (cost > 0 && CrafterStock.Count(bot, _profile) < cost)
            {
                DrySpell(bot);
                return;
            }

            ITool tool = FindTool(bot);

            if (tool == null)
            {
                // CanCraft would answer 1044038 for a null tool, and CompleteCraft dereferences
                // the tool unconditionally at CraftItem.cs:1915 - so never hand it one.
                Blocked = "has no " + _profile.ToolType.Name;
                return;
            }

            int refusal = system.CanCraft(bot, tool, type);

            if (refusal != 0)
            {
                Blocked = DescribeRefusal(refusal);
                return;
            }

            Blocked = null;
            Attempts++;

            entry.Craft(bot, system, null, tool);
        }

        /// <summary>How much material one of these costs, across every resource it names.</summary>
        private static int CostOf(CraftItem entry)
        {
            int cost = 0;

            for (int i = 0; i < entry.Resources.Count; i++)
            {
                cost += entry.Resources.GetAt(i).Amount;
            }

            return cost;
        }

        /// <summary>
        /// The refusals a bot can actually hit, in words rather than cliloc numbers.
        ///
        /// 1044267 is the one that matters and the one worth being able to read at a glance: it
        /// means the Smith is not standing where it thinks it is, which is a navigation-data
        /// problem wearing a crafting costume.
        /// </summary>
        private static string DescribeRefusal(int cliloc)
        {
            switch (cliloc)
            {
                case 1044038: return "its tool is worn out";
                case 1044263: return "its tool is not on its person";
                case 1048146: return "it has the wrong tool equipped";
                case 1044267: return "it is not beside both a forge and an anvil";
                default: return "the craft system refused it (" + cliloc + ")";
            }
        }

        /// <summary>The trade's tool, equipped or in the pack. BaseTool.CheckAccessible accepts either.</summary>
        private ITool FindTool(PlayerBot bot)
        {
            Item held = bot.FindItemOnLayer(Layer.OneHanded) ?? bot.FindItemOnLayer(Layer.TwoHanded);

            if (held is ITool && _profile.ToolType.IsInstanceOfType(held))
            {
                return (ITool)held;
            }

            if (bot.Backpack == null)
            {
                return null;
            }

            return bot.Backpack.FindItemByType(_profile.ToolType) as ITool;
        }

        /// <summary>
        /// Count what has appeared since the last look, and react to it.
        ///
        /// Made things are identified by type rather than tracked in a list, which is both simpler
        /// and more robust than upstream's `_made` collection: nothing goes stale when an item is
        /// moved, traded or destroyed, and a starter prop the bot was spawned holding counts as
        /// something it has made, which is exactly the fiction those props exist to sell.
        /// </summary>
        private void NoticeFinished(PlayerBot bot)
        {
            int made = CountMade(bot);
            int exceptional = CountExceptional(bot);

            if (made > _madeSeen)
            {
                Made += made - _madeSeen;

                BotLog.Note(bot, BotLogKind.Work, "finished {0} piece(s) - {1} made, {2} on the bench",
                    made - _madeSeen, Made, made);
            }

            if (exceptional > _exceptionalSeen && Utility.RandomDouble() < 0.60)
            {
                string line = ChatLibrary.PickRandom("craft_masterwork");

                if (!String.IsNullOrEmpty(line) && IsPlayerNearby(bot))
                {
                    bot.Say(line);
                }
            }

            _madeSeen = made;
            _exceptionalSeen = exceptional;
        }

        private int CountMade(PlayerBot bot)
        {
            Container pack = bot.Backpack;

            if (pack == null)
            {
                return 0;
            }

            int count = 0;

            foreach (Item item in pack.Items)
            {
                if (IsBandItem(item))
                {
                    count++;
                }
            }

            return count;
        }

        private int CountExceptional(PlayerBot bot)
        {
            Container pack = bot.Backpack;

            if (pack == null)
            {
                return 0;
            }

            int count = 0;

            foreach (Item item in pack.Items)
            {
                if (IsBandItem(item) && IsExceptional(item))
                {
                    count++;
                }
            }

            return count;
        }

        private bool IsBandItem(Item item)
        {
            if (item == null || item.Deleted)
            {
                return false;
            }

            Type type = item.GetType();

            return Contains(_profile.Common, type)
                || Contains(_profile.Minor, type)
                || Contains(_profile.Rare, type);
        }

        private static bool Contains(Type[] band, Type type)
        {
            for (int i = 0; i < band.Length; i++)
            {
                if (band[i] == type)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Upstream tested three separate quality enums; ServUO has consolidated them behind
        /// IQuality, which BaseWeapon, BaseArmor and BaseClothing all implement. One test where
        /// they needed a three-way switch, and it picks up anything else craftable for free.
        /// </summary>
        private static bool IsExceptional(Item item)
        {
            var quality = item as IQuality;

            return quality != null && quality.Quality == ItemQuality.Exceptional;
        }

        /// <summary>
        /// Out of materials.
        ///
        /// Upstream waited three minutes and then bought a restock over the counter for gold.
        /// There is no gold in this session, so a dry crafter STAYS dry until a gatherer brings
        /// it something — which is not a gap, it is the motive the whole loop is built around.
        /// The line it says is what makes that legible from outside: "need iron ingots" is a bot
        /// telling you why it is standing still.
        ///
        /// SEAM, restored by the economy session (7f): the counter restock, and the gold that
        /// pays for it.
        /// </summary>
        private void DrySpell(PlayerBot bot)
        {
            if (_drySince == null)
            {
                _drySince = CustomTime.Now;
            }

            if (CustomTime.Now - _drySince.Value < DryQuiet)
            {
                return;
            }

            if (Core.TickCount - _nextNeedLine < 0)
            {
                return;
            }

            _nextNeedLine = Core.TickCount + (Utility.RandomMinMax(45, 100) * 1000L);

            if (!IsPlayerNearby(bot))
            {
                return;
            }

            string line = ChatLibrary.PickRandom("craft_need");

            if (String.IsNullOrEmpty(line))
            {
                return;
            }

            var context = new ChatTokenContext { Bot = bot, DestinationId = DestinationId };
            string resolved;

            if (ChatTokens.TryResolve(line, context, out resolved))
            {
                bot.Say(resolved);
            }
        }

        /// <summary>
        /// Take material a gatherer has just handed over. Returns how much was accepted.
        /// </summary>
        public int Accept(PlayerBot bot, int amount)
        {
            if (_profile == null)
            {
                return 0;
            }

            int accepted = CrafterStock.Add(bot, _profile, amount);

            if (accepted > 0)
            {
                _drySince = null;
            }

            return accepted;
        }

        /// <summary>What raw good this crafter buys, for the delivery hook to match against.</summary>
        public Type RawGood
        {
            get { return _profile == null ? null : _profile.RawGood; }
        }

        private void ScheduleNextCraft()
        {
            int seconds = Utility.RandomMinMax((int)CraftMin.TotalSeconds, (int)CraftMax.TotalSeconds);

            _nextCraft = Core.TickCount + (seconds * 1000L);
        }

        /// <summary>
        /// Turn to face the anvil, so a smith is swinging at something.
        ///
        /// Upstream's 7x7 static scan, with its item-id set: 0x0FAF and 0x0FB0 are the two anvil
        /// facings, 0x0FB1 the small forge, and 0x197A-0x19A9 the large multi-tile forge range.
        /// Extended to look at world items as well as statics, because on this shard the Britain
        /// fixtures are addons placed by the decoration system rather than baked into the map.
        /// </summary>
        private static void FaceNearestFixture(PlayerBot bot)
        {
            Map map = bot.Map;

            if (map == null || map == Map.Internal)
            {
                return;
            }

            Point3D best = Point3D.Zero;
            int bestDistance = Int32.MaxValue;

            IPooledEnumerable eable = map.GetItemsInRange(bot.Location, 3);

            try
            {
                foreach (Item item in eable)
                {
                    if (!IsWorkFace(item.ItemID))
                    {
                        continue;
                    }

                    int distance = Math.Max(Math.Abs(item.X - bot.X), Math.Abs(item.Y - bot.Y));

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = item.Location;
                    }
                }
            }
            finally
            {
                eable.Free();
            }

            for (int dx = -3; dx <= 3; dx++)
            {
                for (int dy = -3; dy <= 3; dy++)
                {
                    StaticTile[] tiles = map.Tiles.GetStaticTiles(bot.X + dx, bot.Y + dy, true);

                    for (int i = 0; i < tiles.Length; i++)
                    {
                        if (!IsWorkFace(tiles[i].ID))
                        {
                            continue;
                        }

                        int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));

                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = new Point3D(bot.X + dx, bot.Y + dy, tiles[i].Z);
                        }
                    }
                }
            }

            if (bestDistance != Int32.MaxValue)
            {
                bot.Direction = bot.GetDirectionTo(best);
            }
        }

        private static bool IsWorkFace(int id)
        {
            return id == 0x0FAF || id == 0x0FB0 || id == 0x0FB1 || (id >= 0x197A && id <= 0x19A9);
        }

        /// <summary>Every crafter currently at a station, for Bots.Work.</summary>
        public static List<CrafterBehavior> Live()
        {
            var crafters = new List<CrafterBehavior>();

            foreach (Mobile mobile in LiveRegistry.Snapshot())
            {
                var bot = mobile as PlayerBot;

                if (bot == null || bot.Deleted)
                {
                    continue;
                }

                var crafter = bot.Behavior as CrafterBehavior;

                if (crafter != null)
                {
                    crafters.Add(crafter);
                }
            }

            return crafters;
        }
    }
}
