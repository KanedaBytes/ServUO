using Server.Engines.Quests;
using Server.Items;

namespace Server.Custom
{
    /// <summary>
    /// Old Marta's fetch quest: bring her five fish for a small purse of gold.
    ///
    /// There is no registry to declare this in - ServUO binds quests to NPCs through
    /// MondainQuester.Quests, so the only reference to this type is on OldMarta.
    ///
    /// The public parameterless constructor is load bearing: both QuestHelper.Construct and
    /// QuestReader build quests with Activator.CreateInstance(type).
    /// </summary>
    public class MartasFishRequest : BaseQuest
    {
        public MartasFishRequest()
            : base()
        {
            // Image 0x9CC is the fish tile, so the objectives page draws one. Never reorder or
            // insert AddObjective calls after go-live: BaseQuest.Deserialize replays objective
            // state positionally, so a new objective at index 0 would inherit the old one's
            // progress on every character mid-quest.
            AddObjective(new ObtainObjective(typeof(Fish), "fish", 5, 0x9CC));

            // ServUO has no gold reward class. GiveRewards collapses a stackable reward into a
            // single stack of Amount, so this is one 250-gold pile, not 250 coins.
            AddReward(new BaseReward(typeof(Gold), 250, "250 gold"));
        }

        /// <summary>
        /// Once per character. On turn-in, BaseQuest.RemoveQuest writes a QuestRestartInfo into
        /// PlayerMobile.DoneQuests and QuestHelper.Delayed then refuses the offer permanently.
        ///
        /// The record is only written for AccessLevel.Player, so a staff character can repeat
        /// this quest forever - that is upstream behaviour, not a bug here.
        /// </summary>
        public override bool DoneOnce
        {
            get
            {
                return true;
            }
        }

        public override object Title
        {
            get
            {
                return "A Basket of Fish";
            }
        }

        public override object Description
        {
            get
            {
                return "Ah, a kind face at last. These old hands aren't what they were, and the walk " +
                       "to the docks is longer every year. Could you spare an afternoon and bring me " +
                       "five fish? I'll not ask you to work for nothing - there's coin in it for you.";
            }
        }

        public override object Refuse
        {
            get
            {
                return "No matter, dear. The river will still be there tomorrow, and so will I.";
            }
        }

        public override object Uncomplete
        {
            get
            {
                return "Five fish is all I ask. Any honest fish will do - the docks south of here are " +
                       "the place for it.";
            }
        }

        /// <summary>
        /// Must not be null: MondainQuestGump.SecComplete treats a null Complete as "no dialog",
        /// consumes the items and hands over the reward with no confirmation page at all.
        /// </summary>
        public override object Complete
        {
            get
            {
                return "Bless you, child. Here, take this for your trouble - and mind you don't spend " +
                       "it all at the tavern.";
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
