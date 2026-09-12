// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>How fast a bot should be moving right now.</summary>
    public enum BotPace
    {
        /// <summary>Lingering, sitting, working, standing about a bank.</summary>
        Walk,

        /// <summary>Going somewhere: a Traveler leg, or a commute to a work site.</summary>
        Run
    }

    /// <summary>
    /// How a bot moves: at a player's pace, and on a horse when it owns one.
    ///
    /// Bots walked everywhere at exactly half a second a step, which is the one speed nothing in
    /// UO actually moves at - a player walks at 0.4 and runs at 0.2, and on a mount 0.2 and 0.1.
    /// The half-second came from `SpeedInfo.MaxDelay`: `GetSpeedsNew` derives a creature's speed
    /// from its Dex, `PassiveSpeed` is twice `ActiveSpeed`, and `TransformMoveDelay` then clamps
    /// the result to 0.5. Every bot in the world was sitting on that clamp, which is why they all
    /// moved at the same speed regardless of Dex and why none of them ever played a run animation.
    ///
    /// The delay is not decoration. `BaseAI.DoMoveImpl` decides whether a step is a RUN from it:
    ///
    ///     running = CanRun &amp;&amp; (mounted ? delay &lt; Mobile.WalkMount : delay &lt; Mobile.WalkFoot)
    ///
    /// so 200ms on foot is a run and 400ms is a walk, and the numbers here are exactly the four
    /// the engine ships (`Mobile.WalkFoot` and friends) rather than four of our own that happen
    /// to look similar. Set 400 on foot and the bot walks; set 200 and it runs, animation and all.
    ///
    /// GAME THREAD ONLY.
    /// </summary>
    public static class BotMovement
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        /// <summary>
        /// A trip shorter than this is walked. uo-offline `TravelerBehavior.cs:1803`, used at
        /// `:1927` as `running = forceRunning || dist > RunThresholdTiles`.
        ///
        /// Upstream measures it per LEG, between two of its destination waypoints. We cannot: a
        /// leg here is one graph hop and `Custom.NavHopMaxTiles` caps those at twelve, so the
        /// same comparison would never once be true and no bot would ever run. Applied instead to
        /// the whole route, which is what one of their legs actually corresponds to and preserves
        /// what the number is for - a short errand is walked, a journey is run.
        /// </summary>
        public const int RunThresholdTiles = 25;

        /// <summary>
        /// The step delay, in seconds, for a pace and a mount state.
        ///
        /// Read off the engine's own constants so a shard that retunes movement retunes bots with
        /// it. They are 400 / 200 / 200 / 100 by default, walk and run, foot and mount - and they
        /// are the same four numbers upstream uses, because upstream reads them from the same
        /// place: `using MoveDelays = Server.Movement.Movement` (`TravelerBehavior.cs:23`), whose
        /// defaults are set in ModernUO's `Server/Mobiles/Movement.cs:33-36`. ServUO keeps them
        /// in `Server/Mobile.cs:3063-3071`. Nothing here is a number of our own.
        /// </summary>
        public static double DelayFor(BotPace pace, bool mounted)
        {
            int ms = mounted
                ? (pace == BotPace.Run ? Mobile.RunMount : Mobile.WalkMount)
                : (pace == BotPace.Run ? Mobile.RunFoot : Mobile.WalkFoot);

            return ms / 1000.0;
        }

        /// <summary>
        /// Move this bot at a pace, mounted or not.
        ///
        /// Both speeds are set, not just the active one. `BaseAI` picks between ActiveSpeed and
        /// PassiveSpeed by its own ActionType, and a Traveler crossing the map is "wandering" as
        /// far as the AI is concerned - so setting only ActiveSpeed left the bot on PassiveSpeed,
        /// which is twice as slow and clamped to the half-second again.
        ///
        /// Writing CurrentSpeed restarts the AI's think timer (`OnCurrentSpeedChanged`), so it is
        /// only written when it actually changes. Called every tick without that guard, the timer
        /// would be stopped and restarted ten times a second and the bot would never think.
        /// </summary>
        public static void SetPace(PlayerBot bot, BotPace pace)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            double delay = DelayFor(pace, bot.Mounted);

            // ONE WRITE, WHERE THERE WERE THREE AND A GUARD.
            //
            // ActiveSpeed and PassiveSpeed both had to be set because BaseAI chose between them by
            // its own ActionType and a commuting Traveler counted as wandering, so writing only
            // one left it to chance which was read. CurrentSpeed was the one DoMoveImpl actually
            // stepped on, and its write was guarded by an epsilon comparison because assigning it
            // fired OnCurrentSpeedChanged, which stopped and restarted the AI timer
            // (BaseAI.cs:3031-3037) - called every tick unguarded, the bot would never think.
            //
            // A PlayerMobile has neither the three properties nor the timer. There is one pace,
            // nothing is restarted by writing it, and NavPlayerActor reads it through IBotActor.
            bot.StepDelaySeconds = delay;
        }

        /// <summary>
        /// The mount pool, carried over verbatim from uo-offline's `BotMountHelper._mountTypes`
        /// (`CustomBots/BotMountHelper.cs:28-43`), weighting included: Horse appears four times
        /// because it is meant to be the common one.
        ///
        /// Pack animals are deliberately absent, and for their reason as well as ours: you cannot
        /// ride one, and `BotPackAnimals` already gives them to gatherers as cargo followers.
        /// </summary>
        private static readonly Type[] MountTypes =
        {
            typeof(Horse),
            typeof(Horse),
            typeof(Horse),
            typeof(Horse),
            typeof(ForestOstard),
            typeof(DesertOstard),
            typeof(FrenziedOstard),
            typeof(Llama)
        };

        /// <summary>
        /// The share of eligible bots that own a mount. uo-offline `PlayerBot.cs:751`.
        ///
        /// In `bots.json` `mounts.ownChance` since the disposition landed, because a mount roll and
        /// a mount disposition are two dials on one thing and splitting them across a const and a
        /// config file is how they drift. Still upstream's 70% by default.
        /// </summary>
        public static double MountChance
        {
            get { return BotSystem.Store.Mounts.OwnChance; }
        }

        /// <summary>
        /// Whether this class rides at all. uo-offline `PlayerBot.cs:749-750`.
        ///
        /// Class only - there is no tier component upstream, and inventing one here would make
        /// our bots and theirs disagree about who owns a horse for no reason anybody could point
        /// at. A Fisherman works the dock edge on foot because casting a line from horseback
        /// looks absurd; a gatherer's animal is the pack beast, which nobody can ride.
        /// </summary>
        public static bool CanOwnMount(BotClass cls)
        {
            return cls != BotClass.Fisherman && !BotClassHelper.IsGatherer(cls);
        }

        /// <summary>
        /// The spawn-time roll: most bots own a horse. uo-offline `PlayerBot.cs:743-753`.
        /// </summary>
        public static void RollMount(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || !CanOwnMount(bot.Class))
            {
                return;
            }

            if (Utility.RandomDouble() >= MountChance)
            {
                return;
            }

            TryMount(bot);
        }

        /// <summary>
        /// Puts a bot on a mount rolled from the pool, if it is not already riding.
        ///
        /// Five attempts, as upstream (`BotMountHelper.cs:66-100`), because a type that will not
        /// instantiate should cost one reroll rather than the whole mount.
        /// </summary>
        public static bool TryMount(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Mounted || bot.Map == null || bot.Map == Map.Internal)
            {
                return false;
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var mount = Activator.CreateInstance(
                        MountTypes[Utility.Random(MountTypes.Length)]) as BaseMount;

                    if (mount == null)
                    {
                        continue;
                    }

                    // 75% keep their default coat, 25% take a muted variation - upstream's
                    // RollHorseHue (`BotMountHelper.cs:50-54`), which exists to avoid electric
                    // greens that read as magical rather than as a horse.
                    if (mount is Horse && Utility.RandomDouble() >= 0.75)
                    {
                        mount.Hue = Utility.RandomNeutralHue();
                    }

                    // Rider= requires the mount to be in the world on the same map first.
                    mount.MoveToWorld(bot.Location, bot.Map);
                    mount.Rider = bot;

                    // The pace changes with the mount: the same "run" is 200ms on foot and 100ms
                    // in the saddle, and forgetting this leaves a mounted bot running at a walk.
                    SetPace(bot, CurrentPace(bot));

                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "{0} could not be mounted.", bot.Name);
                }
            }

            return false;
        }

        /// <summary>
        /// Walk a short trip, run a long one - upstream's rule, against the whole route rather
        /// than one hop. See RunThresholdTiles for why the unit had to change.
        /// </summary>
        public static BotPace PaceForRoute(NavRoute route)
        {
            return RouteTiles(route) > RunThresholdTiles ? BotPace.Run : BotPace.Walk;
        }

        /// <summary>
        /// The length of a route in tiles, summed hop by hop.
        ///
        /// Not the step count, which counts a twelve-tile hop and a one-tile hop the same, and not
        /// NavRoute.Cost, which is weighted by road tags for the SEARCH and so is not a distance
        /// at all - a road hop costs 0.9 of what it measures.
        /// </summary>
        public static int RouteTiles(NavRoute route)
        {
            if (route == null || route.Steps == null || route.Steps.Count < 2)
            {
                return 0;
            }

            int tiles = 0;

            for (int i = 1; i < route.Steps.Count; i++)
            {
                Point3D a = route.Steps[i - 1].Point;
                Point3D b = route.Steps[i].Point;

                tiles += Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
            }

            return tiles;
        }

        /// <summary>The pace a bot is already moving at, so remounting does not change it.</summary>
        private static BotPace CurrentPace(PlayerBot bot)
        {
            return bot.StepDelaySeconds <= DelayFor(BotPace.Run, false) + 0.0001
                ? BotPace.Run
                : BotPace.Walk;
        }

        /// <summary>
        /// Settle a bot where it has arrived: down to a walk, and off the horse if it is the sort
        /// that gets off.
        ///
        /// WAS AN UNCONDITIONAL DISMOUNT, AND BOTH ANSWERS WERE WRONG. Upstream never dismounts on
        /// arrival at all - its four dismount sites are death, deletion, a gatherer clocking in and
        /// the Tamer's stables ritual - so its bank sitters sit in the saddle, which is what this
        /// method was written to avoid: "a bank full of horses is not what a bank looks like". But
        /// ours dismounted everybody AND DELETED THE HORSE, with nothing anywhere that re-mounts, so
        /// the first bank visit put a bot on foot for its whole session. Measured over fifteen
        /// minutes: 70% own a horse at birth, 7.1% of travelling bots still had one, and 0.0% were
        /// mounted at arrival at every tier.
        ///
        /// So it is a disposition drawn at birth from tier and the Wealthy trait
        /// (`BotMountConfig`), with a small per-arrival chance for the riders - and the dismount is
        /// no longer destructive, so neither answer is permanent. A bank comes out mixed, which is
        /// what a bank looked like.
        ///
        /// `mustDismount` is for the one case that is not a matter of taste: mining refuses a
        /// mounted digger outright (`Mining.cs:500-502`).
        /// </summary>
        public static void Settle(PlayerBot bot, bool mustDismount)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            if (mustDismount || ShouldDismountOnArrival(bot))
            {
                Dismount(bot);
            }

            SetPace(bot, BotPace.Walk);
        }

        /// <summary>
        /// Does this bot get off here?
        ///
        /// The disposition decides, and a rider rolls the small chance on top - which is affordable
        /// only because the horse waits: with the old destructive dismount the same number was an
        /// attrition rate and converged the whole fleet onto its feet.
        ///
        /// EXCEPT FOR A FIXTURE, WHICH GETS NO SUCH ROLL. `Remount` fires on departure, and a
        /// fixed-role bot never departs: it is furniture, it holds one bench or one counter for the
        /// life of the shard. So for a fixture the "small per-arrival chance" is neither small nor
        /// per-arrival - it is a single coin flip whose result is permanent, and the roll belongs to
        /// a VISIT. A fixture has no visits, so its disposition alone decides, which is the whole
        /// point of the disposition being drawn at birth and persisted.
        ///
        /// IT IS NOT, HOWEVER, WHY THE STAFFED BENCHES READ 0% MOUNTED, which is what this guard was
        /// written to explain and what measuring it then disproved. `[BotInfo` on all ten crafters
        /// says eight rolled `Dismounts` outright and the one Expert that `rides` never owned a
        /// horse - the 30% who do not - so nine of the ten were correctly on foot and the tenth had
        /// its horse waiting beside it, which is this file working. The real cause is that the
        /// staffed benches are a small population skewed to low tiers (four Novices of ten), and a
        /// Novice rides 10% of the time by design. Kept anyway, because the reasoning above stands
        /// on its own and a permanent coin flip is a bad shape whether or not it has bitten yet.
        /// </summary>
        private static bool ShouldDismountOnArrival(PlayerBot bot)
        {
            if (!bot.Mounted)
            {
                return false;
            }

            if (bot.MountDisposition == MountDisposition.Dismounts)
            {
                return true;
            }

            if (bot.LifecycleExempt)
            {
                return false;
            }

            return Utility.RandomDouble() < BotSystem.Store.Mounts.DismountOnArrivalChance;
        }

        /// <summary>
        /// Back in the saddle before setting off, if this bot has a horse waiting.
        ///
        /// NOTHING IN THIS TREE EVER RE-MOUNTED, and nothing in upstream's does either - their
        /// TryMountRandom is reachable only from the spawn roll and the Tamer's stables scene. That
        /// was survivable for them because they never dismount on arrival; here it was the whole
        /// defect. Called on departure, which is the moment a horse standing about becomes a horse
        /// somebody wants.
        /// </summary>
        public static bool Remount(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || bot.Mounted || bot.Map == null || bot.Map == Map.Internal)
            {
                return false;
            }

            BaseMount held = bot.HeldMount;

            if (held == null || held.Deleted || held.Map != bot.Map)
            {
                bot.HeldMount = null;
                return false;
            }

            try
            {
                held.ControlTarget = null;
                held.Rider = bot;
                bot.HeldMount = null;

                // The same "run" is 200ms on foot and 100ms in the saddle; forgetting this leaves a
                // mounted bot running at a walk. TryMount has the same line for the same reason.
                SetPace(bot, CurrentPace(bot));

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{0} could not be re-mounted.", bot.Name);
                return false;
            }
        }

        /// <summary>
        /// Takes a bot off its mount. THE ANIMAL IS NOT DESTROYED - it waits beside the bot.
        ///
        /// WAS A DELETE, and that was the defect: "there is no stable record to keep" is true, but
        /// the conclusion does not follow, because a real player's horse simply stands where they
        /// got off it. Upstream deletes too (`BotMountHelper.cs:105-132`) and can afford to, because
        /// it never dismounts on arrival - the only things it destroys a horse for are a death, a
        /// deletion, and a gatherer about to swing a pick.
        ///
        /// So the beast is parked instead, and three things make that safe rather than a litter of
        /// loose horses:
        ///
        ///   IT IS MOVED OFF THE BOT'S OWN TILE. `BaseMount.Rider = null` puts the animal at the
        ///   RIDER'S location (`BaseMount.cs:111-124`), and a creature sharing a bot's tile is a
        ///   creature standing on an arrival point - the exact jam `PickScatteredHome` exists to
        ///   avoid. It goes one tile off, the way `BotPackAnimals.SpawnFor` already places a beast.
        ///
        ///   IT IS CONTROLLED AND TOLD TO STAY. A riderless `BaseMount` is an uncontrolled tamable
        ///   creature with its own AI: it would wander off from the bank it was left at, and
        ///   `BaseCreature.OnMoveOver` refuses every uncontrolled creature's tile to a bot, so a
        ///   loose horse is also a roadblock. Controlled, it holds its tile and pays the ordinary
        ///   player rule instead.
        ///
        ///   IT IS HELD ON THE BOT, so `Remount` can find it and `OnDelete` can reap it. Transient,
        ///   like `PackAnimal`, because both bot and beast are ephemeral across a restart - and
        ///   `SweepStrayMounts` is what catches the pair a save froze mid-visit.
        ///
        /// An ethereal goes to the pack rather than standing anywhere, because that is what an
        /// ethereal is. Nothing in this tree or upstream's gives a bot one today - upstream's own
        /// ethereal branch is dead code - so this is that branch made real rather than left as a
        /// comment, and it costs four lines.
        /// </summary>
        public static void Dismount(PlayerBot bot)
        {
            if (bot == null || bot.Deleted || !bot.Mounted)
            {
                return;
            }

            try
            {
                IMount mount = bot.Mount;

                if (mount != null)
                {
                    var beast = mount as BaseMount;

                    mount.Rider = null;

                    if (beast == null)
                    {
                        // An ethereal IMount is an Item, not a creature. It goes in the pack.
                        var item = mount as Item;

                        if (item != null && !item.Deleted)
                        {
                            bot.AddToBackpack(item);
                        }
                    }
                    else if (!beast.Deleted)
                    {
                        Park(bot, beast);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{0} could not be dismounted.", bot.Name);
            }

            SetPace(bot, BotPace.Walk);
        }

        /// <summary>Stand the horse beside its rider and keep it there. See Dismount for why.</summary>
        private static void Park(PlayerBot bot, BaseMount beast)
        {
            Map map = bot.Map;

            if (map != null && map != Map.Internal)
            {
                Point3D beside = Beside(bot);

                if (beside != bot.Location)
                {
                    beast.MoveToWorld(beside, map);
                }
            }

            // Follower slots are the only way this fails and a bot has five (see BotPackAnimal). An
            // unowned horse is worse than a bot that kept its saddle, so put the rider back if so.
            if (!beast.SetControlMaster(bot))
            {
                try
                {
                    beast.Rider = bot;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "{0}'s mount could not be owned or re-ridden.", bot.Name);
                    beast.Delete();
                }

                return;
            }

            beast.ControlTarget = null;
            beast.ControlOrder = OrderType.Stay;

            bot.HeldMount = beast;
        }

        /// <summary>
        /// A free tile next to the bot, or the bot's own tile when the crowd leaves nothing.
        ///
        /// Eight neighbours rather than BotPackAnimals' fixed +1,+1: a bank counter is exactly where
        /// one corner is a wall, and a horse dropped into a wall is a horse the engine puts somewhere
        /// less sensible than the tile beside its owner.
        /// </summary>
        private static Point3D Beside(PlayerBot bot)
        {
            Map map = bot.Map;

            if (map == null || map == Map.Internal)
            {
                return bot.Location;
            }

            int[] dx = { 1, 0, -1, 0, 1, 1, -1, -1 };
            int[] dy = { 0, 1, 0, -1, 1, -1, 1, -1 };

            for (int i = 0; i < dx.Length; i++)
            {
                int x = bot.X + dx[i];
                int y = bot.Y + dy[i];
                int z = NavWalker.ResolveZ(map, new Point3D(x, y, bot.Z));

                if (map.CanFit(x, y, z, 16, false, false, true))
                {
                    return new Point3D(x, y, z);
                }
            }

            return bot.Location;
        }

        /// <summary>
        /// The horse goes with its rider. For death and deletion, where nothing is coming back.
        ///
        /// The destructive half, kept separate now that Dismount is not: a bot that has died or been
        /// deleted leaves an orphan nothing owns and nothing reaps, whether it was in the saddle or
        /// standing beside it.
        /// </summary>
        public static void ReleaseMount(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }

            BaseMount held = bot.HeldMount;

            bot.HeldMount = null;

            try
            {
                IMount mount = bot.Mount;

                if (mount != null)
                {
                    mount.Rider = null;

                    var beast = mount as BaseMount;

                    if (beast != null && !beast.Deleted)
                    {
                        beast.Delete();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{0}'s mount could not be released.", bot.Name);
            }

            if (held != null && !held.Deleted)
            {
                held.Delete();
            }
        }

        /// <summary>
        /// Delete every bot mount left over from a previous run, at Initialize.
        ///
        /// A parked mount is now A MOBILE IN THE SAVE - controlled, told to stay, and outliving the
        /// boot - where before it was deleted the instant its rider got off. `BotPackAnimal` solved
        /// the same problem by subclassing so `SweepStrays` had a type to look for; a mount is a
        /// stock Horse, Llama or Ostard out of upstream's own pool, and subclassing five of them to
        /// win a type test would be a great deal of boilerplate for one sweep. The condition is just
        /// as specific: a BaseMount whose rider or master is a PlayerBot can only have come from
        /// here, because nothing else on this shard mounts one.
        ///
        /// Justified World.Mobiles walk (CLAUDE.md section 15): once, at Initialize, and there is no
        /// registry of mounts to consult - which is precisely what makes one a stray.
        /// </summary>
        public static void SweepStrayMounts()
        {
            var strays = new List<Mobile>();

            foreach (Mobile mobile in World.Mobiles.Values)
            {
                var beast = mobile as BaseMount;

                if (beast == null || beast.Deleted)
                {
                    continue;
                }

                if (beast.Rider is PlayerBot || beast.ControlMaster is PlayerBot)
                {
                    strays.Add(beast);
                }
            }

            if (strays.Count == 0)
            {
                return;
            }

            foreach (Mobile stray in strays)
            {
                stray.Delete();
            }

            Log.Info("Swept {0} stray bot mount(s) left over from a previous run.", strays.Count);
        }
    }
}
