using System;
using System.Collections.Generic;

using Server.Engines.Quests;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom
{
    /// <summary>
    /// A fishwife in Britain Trammel who greets passers-by, answers "help", and offers
    /// MartasFishRequest.
    ///
    /// She contains no quest logic of her own: MondainQuester supplies the offer gump, the
    /// "Quest Giver" tooltip and all progress tracking, and the binding is nothing more than the
    /// Quests property below - ServUO has no central quest registry.
    ///
    /// Ported from the ModernUO shard, where she was a BaseCreature with CanShout/Shout. ServUO
    /// has no such hooks; the nearest idiom is MondainQuester's OnMovement/Advertise pair, which
    /// this class replaces wholesale for the reasons noted on OnMovement.
    /// </summary>
    public class OldMarta : MondainQuester
    {
        /// <summary>
        /// How close a player must come to be greeted. MondainQuester.AutoSpeakRange defaults to
        /// 10, which is a long way to hail someone across a town square; this is a judgement call,
        /// not a ported constant - ModernUO's ShoutRange has no ServUO counterpart.
        /// </summary>
        private const int GreetRange = 5;

        /// <summary>How close a player must be for her to answer the spoken word "help".</summary>
        private const int HelpRange = 3;

        private const int PruneThreshold = 32;

        private static readonly TimeSpan GreetCooldown = TimeSpan.FromMinutes(1.0);

        /// <summary>
        /// Per-player greeting cooldown. Transient by design and never serialized: a restart
        /// letting her say hello again is the harmless outcome.
        ///
        /// MondainQuester's own cooldown is a single private m_Spoken field shared by every
        /// player, so one passer-by silences the NPC for everyone. That is what this replaces.
        /// </summary>
        private readonly Dictionary<Mobile, DateTime> _nextGreet = new Dictionary<Mobile, DateTime>();

        /// <summary>
        /// Name and title go through the MondainQuester(name, title) constructor, which assigns
        /// Name after BaseVendor's constructor has already run InitBody/InitOutfit. That ordering
        /// is what stops BaseVendor.InitBody's NameList.RandomName from winning - do not move the
        /// name into the body.
        /// </summary>
        [Constructable]
        public OldMarta()
            : base("Old Marta", "the fishwife")
        {
        }

        public OldMarta(Serial serial)
            : base(serial)
        {
        }

        public override Type[] Quests
        {
            get
            {
                return new Type[]
                {
                    typeof(MartasFishRequest)
                };
            }
        }

        /// <summary>
        /// MondainQuester inherits CanTeach = true from BaseVendor, which makes
        /// BaseCreature.GetContextMenuEntries add a Teach entry for every skill at 60.0 or above
        /// and lets BaseAI answer *train* / *teach*. She has no skills set so nothing shows today;
        /// this keeps that true if anyone later gives her one.
        /// </summary>
        public override bool CanTeach
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Fully explicit rather than chaining to base: MondainQuester.InitBody only rolls hair
        /// and skin hue - it sets neither Body nor stats.
        /// </summary>
        public override void InitBody()
        {
            InitStats(100, 100, 25);

            Female = true;
            Race = Race.Human;
            Body = 0x191;

            // CantWalk is the ServUO stationary idiom (there is no SetSpeed here).
            // MondainQuester.Serialize/Deserialize mirror it onto Frozen for us.
            CantWalk = true;

            Hue = Race.RandomSkinHue();

            Utility.AssignRandomHair(this);
        }

        public override void InitOutfit()
        {
            AddItem(new Backpack());
            AddItem(new Shoes(0x74A));
            AddItem(new Skirt(0x8AB));
            AddItem(new FancyShirt(0x483));
        }

        /// <summary>
        /// Deliberately does not chain to base.
        ///
        /// MondainQuester.OnMovement speaks publicly and unconditionally, whether or not she has
        /// anything to offer, off a cooldown shared by every player. Its only other job is
        /// AutoTalkRange, which is -1 (off) and which we do not want anyway.
        ///
        /// This is the shape stock Gareth (Scripts/Quests/CloakOfHumility/Mobiles/Gareth.cs) uses.
        /// </summary>
        public override void OnMovement(Mobile m, Point3D oldLocation)
        {
            if (!m.Alive || m.Hidden || !(m is PlayerMobile))
            {
                return;
            }

            // Edge trigger: fire on the step that brings them into range, not on every step taken
            // inside it.
            if (!InRange(m, GreetRange) || InRange(oldLocation, GreetRange) || !InLOS(m))
            {
                return;
            }

            var player = (PlayerMobile)m;

            DateTime next;

            if (_nextGreet.TryGetValue(player, out next) && DateTime.UtcNow < next)
            {
                return;
            }

            if (!CanStillOffer(player))
            {
                return;
            }

            _nextGreet[player] = DateTime.UtcNow + GreetCooldown;

            PruneGreetCooldowns();

            SayTo(
                player,
                Utility.RandomList(
                    "Good day to you! Have you a moment for an old woman?",
                    "You there - you've the look of someone who isn't afraid of a bit of water.",
                    "Ah, a traveller. Come closer, I'll not bite."));
        }

        /// <summary>
        /// QuestHelper.CanOffer refuses any quest sharing an objective Type() with one the player
        /// already holds, and stock ServUO ships Norton "the fisher" with an identical
        /// ObtainObjective(typeof(Fish), "fish", 5) - spawned in Trammel. Without this, a player
        /// carrying Norton's errand gets a generic refusal and no clue why.
        /// </summary>
        public override void OnTalk(PlayerMobile player)
        {
            // Only explain the clash to someone who could otherwise still take the quest. Anyone
            // who has already finished it should hear the engine's own "not again" line instead.
            if (QuestHelper.GetRestartInfo(player, typeof(MartasFishRequest)) == null && HasOtherFishQuest(player))
            {
                SayTo(player, "You're already off catching fish for someone else, dear. Finish that errand first - I'll keep.");
                return;
            }

            base.OnTalk(player);
        }

        public override void OnOfferFailed()
        {
            Say("Nothing for you just now, dear. Mind how you go.");
        }

        /// <summary>
        /// Required, not belt-and-braces. BaseCreature.HandlesOnSpeech ands the AI's answer with
        /// from.InRange(this, RangePerception), and BaseVendor's constructor passes a perception
        /// of 2 - so without this she cannot hear "help" from three tiles away.
        /// </summary>
        public override bool HandlesOnSpeech(Mobile from)
        {
            return from.InRange(Location, HelpRange) || base.HandlesOnSpeech(from);
        }

        public override void OnSpeech(SpeechEventArgs e)
        {
            // Re-check the range: HandlesOnSpeech only decides who joins the listener list, and
            // Mobile.DoSpeech broadcasts to 15 tiles.
            //
            // Client-side speech keywords are unreliable (empty on ASCII clients, and there is no
            // stock "*help*" id), so match the text directly.
            if (!e.Handled && e.Mobile.InRange(Location, HelpRange) && Insensitive.Contains(e.Speech, "help"))
            {
                SayTo(e.Mobile, "Help, is it? Double-click me and I'll tell you what I need. It's fish, mostly.");
                e.Handled = true;
            }

            // Always chain: swallowing this breaks the AI's own speech handling.
            base.OnSpeech(e);
        }

        /// <summary>
        /// Deliberately not QuestHelper.RandomQuest, which constructs a BaseQuest on every call -
        /// this runs on movement.
        /// </summary>
        private static bool CanStillOffer(PlayerMobile player)
        {
            if (QuestHelper.HasQuest(player, typeof(MartasFishRequest)))
            {
                return false;
            }

            if (QuestHelper.GetRestartInfo(player, typeof(MartasFishRequest)) != null)
            {
                return false;
            }

            return !HasOtherFishQuest(player);
        }

        private static bool HasOtherFishQuest(PlayerMobile player)
        {
            List<BaseQuest> quests;

            // Read the store directly rather than through PlayerMobile.Quests: that getter is
            // MondainQuestData.GetQuests, which INSERTS an empty list for anyone it is asked
            // about, and MondainQuestData.OnSave writes every entry it holds.
            if (!MondainQuestData.QuestData.TryGetValue(player, out quests) || quests == null)
            {
                return false;
            }

            for (int i = 0; i < quests.Count; i++)
            {
                BaseQuest quest = quests[i];

                if (quest == null || quest is MartasFishRequest)
                {
                    continue;
                }

                for (int j = 0; j < quest.Objectives.Count; j++)
                {
                    if (quest.Objectives[j].Type() == typeof(Fish))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Drops every expired entry, so the map holds at most the players greeted in the last
        /// minute. An expired entry carries no information - the next greeting re-adds it.
        /// </summary>
        private void PruneGreetCooldowns()
        {
            if (_nextGreet.Count <= PruneThreshold)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            List<Mobile> stale = null;

            foreach (KeyValuePair<Mobile, DateTime> pair in _nextGreet)
            {
                if (pair.Value <= now)
                {
                    if (stale == null)
                    {
                        stale = new List<Mobile>();
                    }

                    stale.Add(pair.Key);
                }
            }

            if (stale == null)
            {
                return;
            }

            for (int i = 0; i < stale.Count; i++)
            {
                _nextGreet.Remove(stale[i]);
            }
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);

            writer.Write((int)0); // version
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);

            int version = reader.ReadInt();
        }
    }
}
