// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// PlayerBot.cs — a fake player.
//
// Upstream this is a PlayerMobile. Here it is a BaseCreature, and that is the
// single largest deliberate divergence in the port. The reasons, in order of
// how much they cost to work around otherwise:
//
//  1. NavWalker takes a BaseCreature (Core/Navigation/NavWalker.cs:63). It
//     drives BaseAI.DoMove and works around ForceStayHome and Home. There is
//     one walker on this shard on purpose, and a PlayerMobile cannot use it.
//
//  2. Doors. ServUO's FastAStarAlgorithm sets MoveImpl.AlwaysIgnoreDoors from
//     bc.CanOpenDoors (FastAStarAlgorithm.cs:93) and only for a BaseCreature -
//     a PlayerMobile bot treats every closed door as a wall. CanOpenDoors is
//     true by default for a humanoid body (BaseCreature.cs:1924), so as a
//     BaseCreature this is free. Upstream had to patch the pathfinder.
//
//  3. The PlayerMobile dependency was shallow: 15 overrides upstream, and
//     OpenTrade, ApplyNameSuffix, CheckShove and ShouldCheckStatTimers are all
//     virtual on Mobile anyway.
//
//  4. It matches the actor pattern already running here - DailyLifeTownsfolk is
//     a BaseCreature with a stand-aside AI.
//
// What that costs is documented at the two places it costs anything: Player and
// OnDeath, below.
//
// PERSISTENCE. Bots do not survive a restart. Upstream got that for free -
// ModernUO writes players through their account, so an accountless bot was
// never in the save at all. ServUO's StandardSaveStrategy.SaveMobiles writes
// every mobile in World.Mobiles with no account filter, so bots ARE saved here.
// The ephemeral idiom answers it: Timer.DelayCall(Delete) at the tail of
// Deserialize, exactly as DailyLifePatron and DailyLifeTownsfolk do.

using System;
using System.Collections.Generic;

using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    public class PlayerBot : BaseCreature, IBotActor, IBotMover
    {
        private static readonly CustomLogger Log = CustomLogger.For("Bots");

        private PlayerBotBehavior _behavior;

        // ---- identity, rolled once and persisted ----

        [CommandProperty(AccessLevel.GameMaster)]
        public BotClass Class { get; set; }

        [CommandProperty(AccessLevel.GameMaster)]
        public BotSkillTier SkillTier { get; set; }

        [CommandProperty(AccessLevel.GameMaster)]
        public CrafterType CrafterSpec { get; set; }

        /// <summary>
        /// Whether this bot stays in the saddle where it stops.
        ///
        /// Rolled once at birth from its tier and its Wealthy trait and then never re-rolled, which
        /// is the point: a bot that rides is a bot that rides every time you see it, where a
        /// per-arrival coin flip would just be noise. The curve is in `bots.json` `mounts`, and
        /// `BotMountConfig` records what is upstream's here and what is ours - ownership is theirs
        /// verbatim, the disposition is ours, and they have no tier or wealth component at all.
        /// </summary>
        [CommandProperty(AccessLevel.GameMaster)]
        public MountDisposition MountDisposition { get; set; }

        /// <summary>Inclination toward each behaviour. Read by BotLifecycle when a phase expires.</summary>
        public BotPersonality Personality { get; set; }

        /// <summary>
        /// When the current lifecycle phase began.
        ///
        /// Written ONLY by BotLifecycle, deliberately - not by the Behavior setter. Upstream reset
        /// it on every swap anywhere, which meant a bot that visited a bank or a shop restarted its
        /// phase clock each time and never accrued enough to roll at all. See the deviations list
        /// in Scripts/Custom/Bots/README.md.
        /// </summary>
        public DateTime PhaseStartedAt { get; set; }

        /// <summary>
        /// True when the phase clock has never been set — which should now be impossible.
        ///
        /// Kept as a named check rather than a bare comparison because it is the symptom of a bug
        /// that hid for three sessions: a bot with an unset clock is permanently overdue for a
        /// lifecycle roll, so anything you set its behaviour to is overwritten within seconds.
        /// Bots.Population counts these and [BotInfo refuses to print an elapsed time from one.
        /// </summary>
        public bool PhaseClockUnset
        {
            get { return PhaseStartedAt == DateTime.MinValue; }
        }

        /// <summary>
        /// The class whose TRADE this bot works.
        ///
        /// Identical to Class for everyone except the legacy BotClass.Crafter, which is one class
        /// with a CrafterSpec sub-type behind it. Asking Class alone is why a "Class Crafter" bot
        /// standing at the forge was told it had no station on the facet: StationFor and
        /// CrafterProfiles are both keyed on the real trade classes, and Crafter is not one.
        /// </summary>
        public BotClass TradeClass
        {
            get
            {
                return Class == BotClass.Crafter ? CrafterTypeHelper.ToBotClass(CrafterSpec) : Class;
            }
        }

        /// <summary>How many times this bot's brain has changed since it was created. Transient.</summary>
        public int BehaviorChanges { get; private set; }

        /// <summary>
        /// The town this bot calls home - a town tag from bots.json, or null when the graph has
        /// no towns. Rolled once at creation, weighted by how many destinations each town has;
        /// transient, exactly as upstream's HomeCity is (uo-offline PlayerBot.cs:196-201).
        ///
        /// It does ONE thing: every destination carrying this tag weighs `homeBias` times more
        /// in the roll (BotDestinations.Pick), so the same faces keep turning up at the same bank
        /// and forge. It is a multiplier and never a fallback - a resident of a town with no
        /// station of its kind simply rolls like anyone else, which is upstream's rule and the
        /// only one there is. Deliberately not a City column on the destination: the geography
        /// of this graph is tags, and this reads the ones the editor already writes.
        /// </summary>
        public string HomeTown { get; set; }

        // ---- the spawner seed, and what a spawner is allowed to say ----
        //
        // Four properties rather than upstream's one string, because upstream packs the forced
        // class into the behaviour name as "Crafter:Smith" (uo-offline PlayerBot.cs:614-623) and a
        // colon cannot survive an <Objects2> entry - XmlSpawner splits on ":MX=" and friends and
        // silently DISCARDS an entry containing one (XmlSpawner2.cs:12701-12704). So each part of
        // the seed is its own property and the separator is the slash the spawn string already uses:
        //
        //     PlayerBot/Role/Fixed/SeedClass/Smith/SeedHome/trinsic/Seed/Crafter
        //
        // Every setter is order-independent. The work is deferred to a zero-delay timer, so it runs
        // once, after the whole property string has been applied, however it was ordered - which
        // matters because the class has to be re-derived BEFORE the brain attaches (a Crafter's
        // OnAttached reads TradeClass to pick its profile) and nothing should depend on an author
        // getting two fields the right way round.

        /// <summary>Furniture or a session. See BotRole.</summary>
        [CommandProperty(AccessLevel.GameMaster)]
        public BotRole Role { get; set; }

        /// <summary>
        /// Never re-rolled, never logged out, never counted toward the curve.
        ///
        /// Upstream keeps this as its own serialized bool alongside the spawner type test. Here it
        /// is derived, so there is exactly one thing to be wrong: a bot is exempt because it is a
        /// fixture, and it is a fixture because its Role says so.
        /// </summary>
        public bool LifecycleExempt
        {
            get { return Role == BotRole.Fixed; }
        }

        private string _seed;
        private string _seedHome;
        private string _seedStation;
        private BotClass? _seedClass;
        private bool _seedPending;

        /// <summary>
        /// The behaviour this bot should wake up in - a BotBehaviors name. Applied next tick.
        /// </summary>
        [CommandProperty(AccessLevel.GameMaster)]
        public string Seed
        {
            get { return _seed; }
            set { _seed = value; ScheduleSeed(); }
        }

        /// <summary>
        /// The nav destination a seeded Crafter or Gatherer works, when the spawner knows which.
        ///
        /// Without it CrafterBehavior.FindStation picks the nearest station by Chebyshev distance
        /// (CrafterBehavior.cs:344-364), which is usually right - a spawner sits on its own
        /// station's arrival point - but "usually right" is not a thing a probe can assert. Naming
        /// it makes the recipe deterministic: the smith at trinsic-forge is at trinsic-forge
        /// because it was told to be, and Bots.Recipe can say so.
        /// </summary>
        [CommandProperty(AccessLevel.GameMaster)]
        public string SeedStation
        {
            get { return _seedStation; }
            set { _seedStation = value; ScheduleSeed(); }
        }

        /// <summary>
        /// Force the class, replacing whatever the constructor rolled - skills, stats, title and
        /// outfit with it. This is what makes a forge spawner produce a Smith rather than whoever
        /// turned up, and it is upstream's ReinitializeAsClass by another name.
        /// </summary>
        [CommandProperty(AccessLevel.GameMaster)]
        public BotClass SeedClass
        {
            get { return _seedClass ?? Class; }
            set { _seedClass = value; ScheduleSeed(); }
        }

        /// <summary>
        /// Force the home town, replacing the birth roll.
        ///
        /// A NAMED DEVIATION from uo-offline, and the population layer's own idea. Upstream leaves
        /// HomeCity exactly as the constructor rolled it even for a pinned crafter - its
        /// ReinitializeAsClass resets class, gear and skills and deliberately does not touch home
        /// (uo-offline PlayerBot.cs:447-457) - so a Crafter:Smith pinned at a Vesper forge may be a
        /// Yew resident who will never once go home. The generator sets this on every FIXED record
        /// so the smith at trinsic-forge is a Trinsic resident, and leaves it alone on a lifecycle
        /// seed, because a traveller genuinely should be from somewhere else.
        /// </summary>
        [CommandProperty(AccessLevel.GameMaster)]
        public string SeedHome
        {
            get { return _seedHome ?? HomeTown; }
            set { _seedHome = value; ScheduleSeed(); }
        }

        // ---- the session, when one is running ----

        /// <summary>
        /// When this bot's play session ends and it says goodbye, or MinValue before BotSession has
        /// stamped it. Transient, like everything else about a bot.
        /// </summary>
        public DateTime SessionEndsAt { get; set; }

        /// <summary>Between "gtg" and vanishing. Nothing may draft a bot in this state.</summary>
        public bool LoggingOut { get; set; }

        /// <summary>
        /// The phase expired while the behaviour was refusing to be interrupted; roll as soon as
        /// it stops refusing.
        ///
        /// Without this a bot that is almost always busy never transitions at all. A Traveler
        /// declines mid-walk, and on a large graph a Traveler is mid-walk most of the time - so
        /// the roller would look at it, find it busy, and simply forget, every pass, for ever.
        /// Deferring instead of dropping is what makes the lifecycle reach a working bot.
        /// </summary>
        public bool TransitionPending { get; set; }

        /// <summary>True while a walker is steering. Transient - see IBotActor.</summary>
        public bool Commuting { get; set; }

        /// <summary>
        /// How far this bot may stand from Home before the engine walks it back. Transient.
        ///
        /// Read by CheckIdle, written by whichever behaviour pinned Home, and cleared with Home -
        /// a leftover tolerance is as wrong as a leftover Home. It lives on the mobile rather than
        /// on the behaviour because the thing that consults it is an AI override, which cannot see
        /// the behaviour, and because the two values only make sense together: a sitter wants one
        /// tile of slack for the shove it just took, and a crafter wants none because its tile was
        /// chosen for crafting reach.
        /// </summary>
        public int IdleTolerance { get; set; }

        /// <summary>
        /// This gatherer has a load on its back and is walking it to town. Transient.
        ///
        /// Upstream's flag, and it lives on the mobile for the same reason PartyAcceptPending
        /// does: the behaviour that sets it (Gatherer, at the end of a shift) is not the one that
        /// reads it (Traveler, on arriving somewhere that will take the load), and a deleted bot
        /// takes the flag with it rather than leaving an entry in a table to sweep.
        /// </summary>
        public bool HaulPending { get; set; }

        /// <summary>
        /// The pack beast trailing this gatherer, or null. Transient.
        ///
        /// Deliberately not serialized, and it would be wrong if it were: both bot and beast are
        /// ephemeral and delete themselves on load, so a persisted reference could only ever be
        /// to something already gone.
        /// </summary>
        public BaseCreature PackAnimal { get; set; }

        /// <summary>
        /// The horse standing beside this bot while it is off it, or null. Transient.
        ///
        /// Not serialized, for `PackAnimal`'s reason: both bot and beast are ephemeral and the bot
        /// deletes itself on load, so a persisted reference could only ever point at something
        /// already gone. `BotMovement.SweepStrayMounts` is what handles the mount a save froze
        /// mid-visit, since a parked mount - unlike the old deleted one - does reach the save file.
        /// </summary>
        public BaseMount HeldMount { get; set; }

        /// <summary>
        /// True between noticing a party invitation and answering it. Transient, and deliberately
        /// a flag on the bot rather than a static table in BotParty: a deleted bot takes it with
        /// it, so there is nothing to leak or sweep.
        /// </summary>
        public bool PartyAcceptPending { get; set; }

        /// <summary>
        /// How many lines this bot has said since it was created.
        ///
        /// Transient and not serialized - a bot does not survive a restart. It is here rather than
        /// only as a fleet total because "which bot is doing the talking?" is the first question
        /// asked of a crowd that sounds wrong. [BotBehavior reports it.
        /// </summary>
        public int SpeechLines { get; set; }

        /// <summary>
        /// The current brain. Never null; falls back to Idle. Swapping fires OnDetached on the
        /// old and OnAttached on the new.
        /// </summary>
        public PlayerBotBehavior Behavior
        {
            get { return _behavior; }
            set { SetBehavior(value, null); }
        }

        /// <summary>
        /// Change the brain, and say why.
        ///
        /// This is the ONE choke point for a behaviour change - the property setter above is a
        /// thin wrapper on it - which is what makes a complete event log possible at all. The
        /// setter cannot know the reason, so it passes null and the log reads "unspecified";
        /// every caller that knows should use this overload instead.
        ///
        /// The reason is a short lower-case phrase, not a sentence: it is printed inside brackets
        /// in a fixed line format and read by eye down a column.
        /// </summary>
        public void SetBehavior(PlayerBotBehavior value, string reason)
        {
            PlayerBotBehavior next = value ?? new IdleBehavior();

            if (ReferenceEquals(_behavior, next))
            {
                return;
            }

            string from = _behavior == null ? "none" : _behavior.SerializableName;
            string why = String.IsNullOrEmpty(reason) ? "unspecified" : reason;

            if (_behavior != null)
            {
                _behavior.OnDetached(this);
            }

            _behavior = next;

            // Logged BEFORE OnAttached, because OnAttached is entitled to change the behaviour
            // again - GathererBehavior walks a bot with no work zone straight back to Traveler
            // from inside it. Logging afterwards would record those two changes in the wrong
            // order and lose the one that explains the other.
            // Deliberately the single-argument Info overload with an already-formatted string:
            // CustomLogger.Safe returns the template untouched when there are no args, so a stray
            // brace in a bot name cannot become a FormatException here.
            Log.Info(String.Format(
                "{0} {1} -> {2} ({3}) @ {4},{5}", Name, from, next.SerializableName, why, X, Y));

            BotLog.Note(this, BotLogKind.Behavior, "{0} -> {1} ({2}) @ {3},{4}",
                from, next.SerializableName, why, X, Y);

            _behavior.OnAttached(this);

            // Counts every real change of brain, whatever caused it - a lifecycle roll, an
            // arrival handoff, a visit ending. The phase clock deliberately does NOT move on a
            // handoff, so it cannot answer "has this bot done anything?"; this can.
            BehaviorChanges++;
        }

        // ---- construction ----

        /// <summary>
        /// [Constructable] IS LOAD-BEARING, and its absence was a silent failure.
        ///
        /// XmlSpawner will not construct a type whose constructor is not marked with it:
        /// CreateObject defaults requireconstructable to true (XmlSpawner2.cs:11263-11265) and
        /// IsConstructable is a bare attribute test (:2283-2286). A spawner pointed at a type
        /// without it sets status_str to "invalid type specification" and RETURNS TRUE - so the
        /// spawner reports success, its timer keeps rescheduling, and its count sits at zero for
        /// ever with nothing in any log.
        ///
        /// Verified by reproduction: thirty-seven bot spawners imported cleanly, ran for eight
        /// minutes, and produced no bots at all.
        ///
        /// Note this is NOT the rule VocabularySnapshot filters on - its comment says
        /// [Constructable] "gates [Add, not the spawner", which is true of the stock Spawner and
        /// false of XmlSpawner2. See the tech-debt note in SHARD.md.
        /// </summary>
        [Constructable]
        public PlayerBot()
            : this(BotClassHelper.RollRandom(), BotSkillTierHelper.RollRandom())
        {
        }

        public PlayerBot(BotClass cls, BotSkillTier tier)
            : base(AIType.AI_Vendor, FightMode.None, 2, 1, 0.5, 2.0)
        {
            Class = cls;
            SkillTier = tier;
            CrafterSpec = CrafterTypeHelper.RollRandom();
            Personality = BotPersonality.RollRandom();

            // AFTER Personality, and that order is load-bearing: the disposition reads the Wealthy
            // trait, and rolling it first would have read a default-constructed personality with no
            // traits at all - which fails silently, as a bot that simply never rides.
            MountDisposition = BotSystem.Store.Mounts.Roll(SkillTier, Personality);

            // THE PHASE CLOCK STARTS NOW, and forgetting this was a real bug rather than a tidy-up.
            //
            // BotLifecycle has a "first sight" branch that assigns a personality and starts the
            // clock together - but the constructor above already assigns one, so that branch never
            // fired and PhaseStartedAt stayed at DateTime.MinValue for the life of the bot. Every
            // bot was therefore permanently overdue: `CustomTime.Now - MinValue` is two thousand
            // years, which is greater than any phase length, so the roller wanted to transition it
            // on every single pass. [BotInfo showed it as "63924344915s of 14160s elapsed".
            PhaseStartedAt = CustomTime.Now;

            // Where it lives. Upstream rolls this in the constructor too (PlayerBot.cs:301-303),
            // independent of where the bot is about to be placed: born in Britain and living in
            // Trinsic is a real thing, and the bias is what walks it home.
            HomeTown = BotHomeTowns.Roll();

            _behavior = new IdleBehavior();

            Race = Race.Human;
            Female = Utility.RandomBool();
            Body = Female ? 0x191 : 0x190;
            Hue = Race.RandomSkinHue();

            // A bot is flagged as a player. See the note on CanBeRenamedBy below for what this
            // buys (party invites) and what it costs (combat timer priority, and ghosting on
            // death instead of vanishing - handled in OnDeath).
            Player = true;

            Name = NamePool.PickUnique(Female);
            Title = BuildTitle();

            // SpeechHue is Mobile's own and is persisted by Mobile. Deliberately not redeclared
            // on this class - shadowing it would break Say()'s colour lookup.
            SpeechHue = SpeechHues.PickRandom();

            Utility.AssignRandomHair(this);

            if (Utility.RandomDouble() < 0.35 && !Female)
            {
                Utility.AssignRandomFacialHair(this, HairHue);
            }

            ApplySkills();
            ApplyStats();

            AddItem(new Backpack());
            EquipmentTable.RollOutfit(this);

            // A bot must be attackable for notoriety to mean anything, so unlike the daily-life
            // actors it is NOT IsInvulnerable. Nothing attacks it in this session; the point is
            // that its name reads the right colour when a player looks at it.

            // No home tether. WalkRandomInHome would otherwise drag it back to wherever it was
            // created every time it paused - the same reason DailyLifeTownsfolk clears Home.
            Home = Point3D.Zero;
            RangeHome = 0;
            IdleTolerance = 0;

            LiveRegistry.Register(this);

            // A bot owns its horse from the moment it exists (upstream PlayerBot.cs:743-753), but
            // it cannot be given one here: Rider= needs the mount in the world on the same map,
            // and in the constructor there is no map yet.
            //
            // Deferred by one tick rather than hung off OnAfterSpawn, which only fires for the
            // spawner path - [SpawnBot, the probes and the population all call MoveToWorld
            // directly and would never have been given a mount at all. Same idiom the ephemeral
            // NPCs use for the same reason: the world is ready on the next tick, not this one.
            Timer.DelayCall(TimeSpan.Zero, BotMovement.RollMount, this);
        }

        public PlayerBot(Serial serial)
            : base(serial)
        {
        }

        /// <summary>"the Grandmaster Swordsman". Rebuilt whenever class or tier changes.</summary>
        public string BuildTitle()
        {
            return String.Format(
                "the {0} {1}",
                BotSkillTierHelper.DisplayName(SkillTier),
                BotClassHelper.DisplayName(Class));
        }

        /// <summary>
        /// Lay down the class template at this bot's tier, scaled against the configured caps.
        ///
        /// Every skill is set from BotSkillTemplate, which knows no absolute numbers - the caps
        /// come from BotCaps, which resolves from Config/PlayerCaps.cfg unless bots.json
        /// deliberately overrides. Changing the shard's era is a config edit, not a code edit.
        /// </summary>
        public void ApplySkills()
        {
            BotCaps caps = BotSystem.Caps;
            SkillTemplate template = BotSkillTemplates.RollTemplate(Class);

            // Clear first, so a bot's skills are EXACTLY its template and nothing else.
            //
            // This is not defensive tidying - the engine really does add skills behind us.
            // BaseCreature's ChangeAIType stamps Focus (2-20) and DetectHidden (10 or 60) onto
            // every non-vendor creature when PetTrainingHelper is on (BaseCreature.cs:625-632),
            // and PetTrainingHelper.Enabled is Core.TOL, so on this EJ shard it is always on.
            // That is the pet-training system deciding a bot is a trainable animal. It is wrong
            // in flavour and it silently cost up to 80 points of the skill budget - the boot
            // audit caught it as four classes over cap.
            //
            // Clearing is better than subtracting the two known skills: any future engine or
            // upstream-merge addition lands in the same trap, and this closes the whole class of
            // it rather than the two instances of it that exist today.
            for (int i = 0; i < Skills.Length; i++)
            {
                Skills[i].Base = 0.0;
            }

            SetSkill(template.Primary, BotSkillTemplates.PrimarySkillTarget(SkillTier, caps), caps);

            foreach (SkillName skill in template.Secondary)
            {
                SetSkill(skill, BotSkillTemplates.SecondarySkillTarget(SkillTier, caps), caps);
            }

            foreach (SkillName skill in template.Utility)
            {
                SetSkill(skill, BotSkillTemplates.UtilitySkillTarget(SkillTier, caps), caps);
            }
        }

        private void SetSkill(SkillName skill, double target, BotCaps caps)
        {
            double value = target + BotSkillTemplates.RollJitter(caps);

            if (value < 0.0)
            {
                value = 0.0;
            }

            if (value > caps.SkillCap)
            {
                value = caps.SkillCap;
            }

            Skills[skill].Base = value;
        }

        public void ApplyStats()
        {
            var (str, dex, intel) = BotSkillTemplates.StatTargets(Class, SkillTier, BotSystem.Caps);

            InitStats(str, dex, intel);
        }

        // ---- what being a Player costs, and why it is worth it ----

        /// <summary>
        /// Bots are flagged as players.
        ///
        /// The reason is the party gate: AddPartyTarget.cs:30-32 refuses a human-bodied mobile
        /// whose Player flag is false with "Nay, I would rather stay here and watch a nail rust."
        /// A bot that cannot be invited to a party is not a fake player.
        ///
        /// The costs are two, and both are bounded:
        ///
        ///   - Combat timer priority. Mobile's combat timer is only created when Combatant is set
        ///     and is destroyed when it clears (Mobile.cs:2215-2245), so an idle bot has no timer
        ///     at all. While fighting, Player=true puts it in the EveryTick bucket rather than
        ///     FiftyMS (Timer.cs:136). Note the stock condition is `!m_Player && m_Dex <= 100`,
        ///     so a bot with Dex above 100 lands in EveryTick regardless of this flag.
        ///
        ///   - Death. Mobile.OnDeath deletes a dying mobile only when the Player flag is false
        ///     (Mobile.cs:4229). See OnDeath below.
        /// </summary>
        public override bool CanBeRenamedBy(Mobile from)
        {
            // A bot is not a pet. Stock BaseCreature would let a control master rename it; there
            // is no control master here, but the intent should be explicit.
            return from != null && from.AccessLevel >= AccessLevel.GameMaster;
        }

        /// <summary>
        /// A bot reads blue.
        ///
        /// Without this, Notoriety.cs:441-443 falls through to CanBeAttacked for anything that is
        /// not InitialInnocent, and stock BaseCreature.InitialInnocent (BaseCreature.cs:1786)
        /// answers from an XmlAttach lookup that a bot has no reason to carry. Murderer, Criminal
        /// and guild notoriety all still work from here - they live on Mobile.
        /// </summary>
        public override bool InitialInnocent
        {
            get { return true; }
        }

        /// <summary>Bots are not tameable, commandable or teachable, so their context menu is a player's.</summary>
        public override bool CanTeach
        {
            get { return false; }
        }

        /// <summary>
        /// A bot walks through crowds - uo-offline PlayerBot.cs:512, and its reason: the engine's
        /// full-stamina shove rule jammed their bank plazas. This is the MOVER's half; the shoved
        /// side has to consent first, because a BaseCreature is refused before this is ever
        /// asked. See BotShove for the seam and for what a bot still yields to.
        ///
        /// It is also the whole of IBotMover, which this class declares so that BotShove can key
        /// its mover branch on the RULE rather than on this type. The walk audit's probe is the
        /// other implementer, and it was refused by every occupant a bot walks through until it
        /// had one.
        /// </summary>
        public override bool CheckShove(Mobile shoved)
        {
            return true;
        }

        /// <summary>
        /// The shoved half: another bot may walk through this one. Anything else - a player, a
        /// stock NPC, a pet - gets BaseCreature's own answer, which for a player is the stamina
        /// rule and for an uncontrolled creature is a refusal.
        /// </summary>
        public override bool OnMoveOver(Mobile m)
        {
            return BotShove.OnMoveOver(this, m) ?? base.OnMoveOver(m);
        }

        /// <summary>
        /// Every tile this bot moves, whatever moved it - the step census's only caller.
        ///
        /// DOWNSTREAM OF EVERYTHING. Four different things move a bot (the walker's MoveTo, the
        /// ladder's NudgeAway and its rescue teleport, a gatherer's own bot.Move, and
        /// BaseAI.DoMoveImpl's wander) and this is the one place all four pass through, so the
        /// census needs no hook in NavWalker - which is Core and is not allowed to know that bots
        /// exist. The cause is read off Commuting and Home rather than passed in; see
        /// BotStepCensus for how each of the four buckets is decided.
        /// </summary>
        protected override void OnLocationChange(Point3D oldLocation)
        {
            base.OnLocationChange(oldLocation);

            BotStepCensus.Note(this, oldLocation);
        }

        /// <summary>
        /// Stand still.
        ///
        /// THIS IS THE FIDGET FIX, and what it turns off was never ours. BotAI.DoActionWander stands
        /// aside only while Commuting (BotAI.cs:48-58), so an ARRIVED bot falls through to
        /// BaseAI.DoActionWander -> WalkRandomInHome(2,2,1) (BaseAI.cs:1061-1067, :2509-2582) on the
        /// AI timer, whose interval is CurrentSpeed - which BotMovement.Settle drops to
        /// Mobile.WalkFoot, 400ms. WalkRandom's own gate is Utility.Random(16) &lt;= 8, about 56%
        /// (:2241), and BaseCreature.CheckIdle damps only 5% of calls into a 15-25 second pause
        /// (BaseCreature.cs:4754-4759). Measured on a live window of 61 bots before this override
        /// existed: 21.0 idle steps per bot-minute for a Shopper, 30.1 for a BankSitter, 34.3 for a
        /// lingering Traveler - and 0.00 for a Crafter, which is the only one of the four that pins
        /// with RangeHome 0.
        ///
        /// Upstream has none of this layer, because their bot is a PlayerMobile with no BaseAI under
        /// it: their BankSitter steps only when further than HomeRadius 1 from its spot
        /// (BankSitterBehavior.cs:586-617), and their Shopper does not move at all - its own header
        /// says "does NOT path anywhere... No movement = no wall-grinding" (ShopperBehavior.cs:1-14).
        /// Our idle texture already matched theirs; only the engine underneath it did not.
        ///
        /// CheckIdle rather than DoActionWander, because CheckIdle is the ONE thing DoActionWander
        /// asks before it wanders and nothing else consults it - overriding DoActionWander would
        /// also skip herding, navpoints, waypoints and the combatant-facing tail. It is also the
        /// lever this tree already proves: NavWalkAudit's probe overrides exactly this, and the
        /// three measured variants are tabulated in Navigation/README.md.
        ///
        /// Beyond the tolerance it returns FALSE rather than base, so the walk-back is immediate and
        /// certain: base would enter a 15-25 second pause one call in twenty and leave a shoved
        /// sitter standing where it was shoved to, which upstream's unconditional walk-back does not.
        /// The walk back itself is still the engine's - Home with RangeHome 0 is the pin idiom
        /// (BaseAI.cs:2573-2578) - so nothing here re-implements movement.
        /// </summary>
        public override bool CheckIdle()
        {
            if (Home == Point3D.Zero)
            {
                return true;
            }

            int off = Math.Max(Math.Abs(X - Home.X), Math.Abs(Y - Home.Y));

            return off <= IdleTolerance;
        }

        /// <summary>The paperdoll title is the class and tier; a click-title would double it up.</summary>
        public override bool ClickTitle
        {
            get { return false; }
        }

        public override void OnDeath(Container c)
        {
            base.OnDeath(c);

            // The mount does not survive its rider. Upstream dismounts in OnBeforeDeath
            // (PlayerBot.cs:779) and so could we: ServUO HAS that hook, public virtual on Mobile
            // at Mobile.cs:4186, called from Kill at Mobile.cs:3995. The comment here said it did
            // not, which was wrong, and the real reason to release the mount in OnDeath instead is
            // the hook's signature: it returns bool, and a false from it - or from
            // Region.OnBeforeDeath one line above it - CANCELS the death outright. Releasing there
            // would strip the mount off a bot that then does not die.
            //
            // The cost of that choice is real and is REVIEW.md F2, not a hypothetical: the murder
            // report throws inside base.OnDeath above, so a reportable death never reaches this
            // line at all and the mount is left parked (BotDeathProbe cleans up after it on
            // purpose). OnBeforeDeath runs BEFORE that throw. So if the cast is ever the thing
            // that has to be lived with rather than fixed, moving cleanup there is the move - and
            // it needs the cancellation case handled, which is why it is written down rather than
            // done quietly.
            //
            // ReleaseMount, not Dismount: Dismount now PARKS the animal, and a horse standing
            // patiently beside its rider's corpse waiting for an order is not the picture.
            BotMovement.ReleaseMount(this);

            // Player=true mobiles ghost rather than vanish (Mobile.cs:4229), and there is no
            // death layer yet to haunt, walk to a healer and run back for the corpse. Delete on a
            // short delay rather than inline, so the death packets and the corpse have finished
            // being built before the mobile goes.
            //
            // The corpse is deliberately kept: it is the thing a player would loot, and the death
            // behaviour needs it when it lands.
            //
            // Replaced by the death behaviour session.
            Timer.DelayCall(TimeSpan.FromSeconds(1.0), Delete);
        }

        public override void OnAfterSpawn()
        {
            base.OnAfterSpawn();

            // Already registered from the constructor, and Register no-ops on a duplicate. This
            // is here for the spawner path that the population session adds, so a bot placed by a
            // spawner rather than by [SpawnBot is on the live map too.
            LiveRegistry.Register(this);

            // UNTETHER, AGAIN. The constructor cleared Home and RangeHome (see the note there) so
            // that BaseAI.DoActionWander cannot drag a bot back to wherever it was created every
            // time it pauses. Every spawner on this engine then sets them right back, after the
            // constructor and before this call: XmlSpawner at XmlSpawner2.cs:9317-9326 and stock
            // Spawner at Scripts/Services/Spawner/Spawner.cs:498-508 both assign RangeHome from
            // their home range and Home from the spawner's tile.
            //
            // So the whole population would be on a leash the length of its spawner's <Range>, and
            // the symptom would be bots that walk out of town and turn round. This is the only
            // place that can undo it, because it is the only hook that runs after both.
            Home = Point3D.Zero;
            RangeHome = 0;
            IdleTolerance = 0;
        }

        // ---- applying a spawner's seed ----

        /// <summary>
        /// Queue the seed for the next tick, once, however many setters were written.
        ///
        /// Zero delay is not a hedge: XmlSpawner applies the whole property string inside one call
        /// (BaseXmlSpawner.ApplyObjectStringProperties), so anything scheduled from a setter runs
        /// after every setter in the string has run. That is what makes the fields order-free.
        /// </summary>
        private void ScheduleSeed()
        {
            if (_seedPending || Deleted)
            {
                return;
            }

            _seedPending = true;
            Timer.DelayCall(TimeSpan.Zero, ApplySeed);
        }

        private void ApplySeed()
        {
            _seedPending = false;

            if (Deleted)
            {
                return;
            }

            // Class first, always. A Crafter's OnAttached reads TradeClass to choose its profile
            // and its chat categories, so attaching the brain to a bot that is about to become a
            // Smith would pick the wrong ones and never look again.
            if (_seedClass.HasValue && _seedClass.Value != Class)
            {
                ReinitializeAsClass(_seedClass.Value);
            }

            if (!String.IsNullOrEmpty(_seedHome))
            {
                string town;

                if (BotHomeTowns.TryParse(_seedHome, out town))
                {
                    HomeTown = town;
                }
                else
                {
                    Log.Warn(
                        "{0} was seeded home town '{1}', which is not in bots.json destinations.towns; keeping {2}.",
                        Name,
                        _seedHome,
                        HomeTown ?? "none");
                }
            }

            if (String.IsNullOrEmpty(_seed))
            {
                return;
            }

            if (!BotBehaviors.IsKnown(_seed))
            {
                // Loudly, and then Idle - which is what BotBehaviors.Create would give anyway.
                // A silent fallback here would look exactly like a spawner that works.
                Log.Warn("{0} was seeded behaviour '{1}', which no behaviour answers to.", Name, _seed);
            }

            PlayerBotBehavior brain = BotBehaviors.Create(_seed);

            // A SEED IS A TRANSITION, and owes what a hand switch owes - the window, the station
            // and the clock. BotCommands.TryHandSwitch is the same three obligations written out,
            // and the comment there records what each one cost to discover.
            TimeSpan? window = BotBehaviors.VisitWindowFor(brain);

            // ...except that a FIXTURE gets no window, and that is the whole difference between
            // the two roles. Upstream stamps one on a spawned Shopper precisely so it breaks off
            // and travels on, and skips the fixed-role ones for the same reason (uo-offline
            // PlayerBot.cs:647-661). Here it would be worse than pointless: a window that lapses
            // hands the brain back to Traveler (PlayerBotBehavior.CheckVisitExpired), so a smith
            // stamped with one walks away from its forge three hours into the shift.
            if (window != null && Role != BotRole.Fixed)
            {
                brain.VisitExpiresAt = CustomTime.Now + window.Value;
            }

            if (!String.IsNullOrEmpty(_seedStation))
            {
                var crafter = brain as CrafterBehavior;

                if (crafter != null)
                {
                    crafter.DestinationId = _seedStation;
                }

                var gatherer = brain as GathererBehavior;

                if (gatherer != null)
                {
                    gatherer.DestinationId = _seedStation;
                }

                // AND A BANK SITTER, which was the third and was missing. A seeded fixture reached
                // BotCrowds.CountFor with a null DestinationId - the census matches on exactly that
                // field, and BankSitterBehavior.DestinationId's own doc comment says "read by the
                // crowd census" - so the twelve permanent sitters counted toward no floor at all and
                // every bank read under its floor for ever. The garrison was there the whole time,
                // measured at exactly 3.00 per bank across 449 samples; only the count was blind.
                var sitter = brain as BankSitterBehavior;

                if (sitter != null)
                {
                    sitter.DestinationId = _seedStation;
                }

                // AND A SHOPPER, which was the fourth and the last. The bank fix above named the
                // rule and this is the other half of the same defect: a seeded Shopper reached
                // ShopperBehavior.MoveToAnotherSpot with a null DestinationId, which is the first
                // thing that method tests, so it paused and re-paused for its whole visit and the
                // bot never left the tile BotPopulation.SpawnPoint put it on - arrival #0 of its
                // shop, because the shop slot's Spread is 0. Measured as two of the walk audit's
                // short-of-goal arrivals: a fixture standing on the tile another walk was aiming at.
                var shopper = brain as ShopperBehavior;

                if (shopper != null)
                {
                    shopper.DestinationId = _seedStation;
                }
            }

            SetBehavior(brain, "spawner seed");

            // The phase clock starts when the brain does. Without this a seeded bot carries the
            // constructor's clock, is already part-way through its first phase, and is rolled off
            // it early - the same fault the hand switch had, and it hid for three sessions.
            PhaseStartedAt = CustomTime.Now;
            TransitionPending = false;

            // THE CURVE'S WAY UP, and the last thing the seed does.
            //
            // Upstream refuses the spawn before construction, in an override of Spawner.Spawn
            // (uo-offline PlayerBotSpawner.cs:75-82). XmlSpawner has no such hook, and until the
            // seed has been applied nothing knows whether this bot is a fixture or a session - so
            // the refusal has to happen here, after the fact. See BotSession.AllowSpawn for what
            // that costs and why it is worth paying.
            //
            // A fixture is never refused: the bank crowd exists at 05:00 as it does at noon.
            if (Role != BotRole.Fixed && Spawner != null && !BotSession.AllowSpawn())
            {
                Delete();
            }
        }

        /// <summary>
        /// Become another class outright: skills, stats, title, gear and pack.
        ///
        /// Upstream's ReinitializeAsClass (uo-offline PlayerBot.cs:447-457), with one addition and
        /// one deliberate omission. The addition is Title, which upstream leaves reading as the old
        /// class. The omission is HomeTown, which upstream also leaves alone - but here that is a
        /// decision taken one level up, in SeedHome, rather than an oversight: re-deriving a class
        /// says nothing about where the bot lives, and the generator is the thing that knows.
        /// </summary>
        public void ReinitializeAsClass(BotClass cls)
        {
            Class = cls;

            StripGearAndPack();

            // ApplySkills zeroes every skill before laying down the template, so the old class
            // cannot bleed through - see the note on that method.
            ApplySkills();
            ApplyStats();

            Title = BuildTitle();

            EquipmentTable.RollOutfit(this);
        }

        /// <summary>
        /// Delete the constructor's outfit and pack contents, keeping the backpack itself.
        ///
        /// The bank layer is left alone: BaseCreature puts nothing there, but a bot that has been
        /// to a bank might have, and losing it to a re-derive would be a silent theft.
        /// </summary>
        private void StripGearAndPack()
        {
            var equipped = new List<Item>();

            foreach (Item item in Items)
            {
                if (item != Backpack && item.Layer != Layer.Bank)
                {
                    equipped.Add(item);
                }
            }

            foreach (Item item in equipped)
            {
                item.Delete();
            }

            if (Backpack != null)
            {
                foreach (Item item in new List<Item>(Backpack.Items))
                {
                    item.Delete();
                }
            }
        }

        /// <summary>
        /// Called by the AI timer, and only while a player is in the sector
        /// (`BaseAI.cs:3072-3082`). That gating is why the party check lives here rather than in
        /// a sweep of its own: an invitation can only arrive from somebody standing next to the
        /// bot, which is precisely when this runs.
        /// </summary>
        public override void OnThink()
        {
            base.OnThink();

            BotParty.CheckInvite(this);
        }

        /// <summary>
        /// Required, not belt-and-braces: without it a bot cannot hear you from three tiles away.
        ///
        /// BaseCreature.HandlesOnSpeech (BaseCreature.cs:4605-4615) ANDs the AI's answer with
        /// from.InRange(this, RangePerception), and this class's constructor passes a perception
        /// of 2. The responder needs ten tiles, because your NAME carries across a room. The same
        /// trap is documented at Scripts/Custom/Mobiles/README.md:76-85, where OldMarta hit it.
        ///
        /// It lives on the mobile rather than on BotAI on purpose. Seam 5 keeps BotAI thin so it
        /// can become a MeleeAI or a MageAI when combat lands; hearing is not a property of how a
        /// bot fights, and putting it here means it survives that swap untouched.
        /// </summary>
        public override bool HandlesOnSpeech(Mobile from)
        {
            return from.InRange(Location, BotSpeechResponder.ListenRange) || base.HandlesOnSpeech(from);
        }

        /// <summary>
        /// Somebody spoke nearby. Answer if it was meant for us.
        ///
        /// Note what does NOT reach here: a bot's own Say(). Mobile.Say goes to
        /// PublicOverheadMessage (Mobile.cs:7236-7239), which only sends packets, while the
        /// listener pipeline hangs off Mobile.DoSpeech - and DoSpeech is invoked from exactly two
        /// places, both real client speech packets (PacketHandlers.cs:1547, :1628). So bots cannot
        /// hear each other, cannot echo, and cannot have "withdraw 1000" read as a command by a
        /// passing Banker. That is structural rather than something this code arranges, which is
        /// why the chat probe asserts it: the day somebody routes bot speech through DoSpeech to
        /// make bots hear each other, every bank_actions line becomes a live command at once.
        /// </summary>
        public override void OnSpeech(SpeechEventArgs e)
        {
            BotSpeechResponder.OnSpeech(this, e);

            // Always chain: BaseCreature's own handler does the AI and speech-type work, and
            // swallowing it here would break anything that lands on the bot later.
            base.OnSpeech(e);
        }

        public override void OnDelete()
        {
            // Detach the brain first. A Traveler's OnDetached stops its walker and clears
            // Commuting; without it, NavWalker would keep a reference to a deleted mobile until
            // its own next tick noticed.
            if (_behavior != null)
            {
                _behavior.OnDetached(this);
            }

            BotParty.OnBotDeleted(this);

            // The beast goes with its owner. Its own OnThink reaper would get there within ten
            // seconds anyway, but leaving a llama standing in a field for ten seconds after the
            // miner vanished is exactly the kind of loose end that becomes a stray.
            BotPackAnimals.Release(this);

            // And so does the horse, for the same reason: a mount whose rider was deleted is an
            // orphan nothing owns and nothing reaps. Upstream does this at PlayerBot.cs:856.
            //
            // ReleaseMount, not Dismount: Dismount now PARKS the animal beside the bot, which is
            // exactly the orphan this line exists to prevent. It covers the parked one too.
            BotMovement.ReleaseMount(this);

            NamePool.Release(Name);
            LiveRegistry.Unregister(this);

            base.OnDelete();
        }

        protected override BaseAI ForcedAI
        {
            get { return new BotAI(this); }
        }

        // ---- serialization ----
        //
        // This exists because ServUO saves every mobile in World.Mobiles, not because anything
        // reads it back: Deserialize schedules the bot's own deletion. It is written properly
        // anyway - a half-written mobile in the save is a load-time exception, and the version
        // ladder costs nothing now and is impossible to retrofit later.

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);

            writer.Write(1); // version

            writer.Write((byte)Class);
            writer.Write((byte)SkillTier);
            writer.Write((byte)CrafterSpec);

            // v1. An enum as a byte, like the three above.
            writer.Write((byte)MountDisposition);

            Personality.Write(writer);

            writer.Write(_behavior != null ? _behavior.SerializableName : "Idle");
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);

            int version = reader.ReadInt();

            Class = (BotClass)reader.ReadByte();
            SkillTier = (BotSkillTier)reader.ReadByte();
            CrafterSpec = (CrafterType)reader.ReadByte();

            if (version >= 1)
            {
                MountDisposition = (MountDisposition)reader.ReadByte();
            }

            Personality = BotPersonality.Read(reader);

            reader.ReadString(); // behaviour name; nothing to restore it onto yet

            _behavior = new IdleBehavior();

            // ServUO has no [AfterDeserialization]; this is the equivalent, and it is what makes
            // the bot ephemeral across a restart. Deliberately NOT registering with LiveRegistry
            // on this path - a bot that is about to delete itself would sit on the live map for
            // one tick as a corpse.
            Timer.DelayCall(Delete);
        }
    }
}
