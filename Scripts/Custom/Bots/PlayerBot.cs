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
        private PlayerBotBehavior _behavior;

        // ---- identity, rolled once and persisted ----

        [CommandProperty(AccessLevel.GameMaster)]
        public BotClass Class { get; set; }

        [CommandProperty(AccessLevel.GameMaster)]
        public BotSkillTier SkillTier { get; set; }

        [CommandProperty(AccessLevel.GameMaster)]
        public CrafterType CrafterSpec { get; set; }

        /// <summary>Inclination toward each behaviour. Rolled at creation; nothing reads it yet.</summary>
        public BotPersonality Personality { get; set; }

        /// <summary>True while a walker is steering. Transient - see IBotActor.</summary>
        public bool Commuting { get; set; }

        /// <summary>
        /// The current brain. Never null; falls back to Idle. Swapping fires OnDetached on the
        /// old and OnAttached on the new.
        /// </summary>
        public PlayerBotBehavior Behavior
        {
            get { return _behavior; }
            set
            {
                PlayerBotBehavior next = value ?? new IdleBehavior();

                if (ReferenceEquals(_behavior, next))
                {
                    return;
                }

                if (_behavior != null)
                {
                    _behavior.OnDetached(this);
                }

                _behavior = next;
                _behavior.OnAttached(this);
            }
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

        public override void OnDelete()
        {
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
