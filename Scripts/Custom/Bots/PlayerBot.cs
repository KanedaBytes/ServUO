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

using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    public class PlayerBot : BaseCreature, IBotActor
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

            // THE PHASE CLOCK STARTS NOW, and forgetting this was a real bug rather than a tidy-up.
            //
            // BotLifecycle has a "first sight" branch that assigns a personality and starts the
            // clock together - but the constructor above already assigns one, so that branch never
            // fired and PhaseStartedAt stayed at DateTime.MinValue for the life of the bot. Every
            // bot was therefore permanently overdue: `CustomTime.Now - MinValue` is two thousand
            // years, which is greater than any phase length, so the roller wanted to transition it
            // on every single pass. [BotInfo showed it as "63924344915s of 14160s elapsed".
            PhaseStartedAt = CustomTime.Now;

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

            LiveRegistry.Register(this);
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

        /// <summary>The paperdoll title is the class and tier; a click-title would double it up.</summary>
        public override bool ClickTitle
        {
            get { return false; }
        }

        public override void OnDeath(Container c)
        {
            base.OnDeath(c);

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

            writer.Write(0); // version

            writer.Write((byte)Class);
            writer.Write((byte)SkillTier);
            writer.Write((byte)CrafterSpec);

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
