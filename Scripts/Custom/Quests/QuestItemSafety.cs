using Server.Items;

namespace Server.Custom
{
    /// <summary>
    /// Decides whether an item is unremarkable enough to be counted toward an ObtainObjective
    /// without the player having flagged it through the "Toggle Quest Item" context menu.
    ///
    /// This exists because obtain objectives are consumed with Item.Delete() (QuestHelper
    /// DeleteItems), which bypasses insurance, and because magic/exceptional/artifact gear shares
    /// the same CLR type as the plain version - ObtainObjective.IsObjective is a plain
    /// IsAssignableFrom test, so "collect 5 Bow" would otherwise happily eat a runic bow.
    /// Anything this predicate rejects still works through the manual toggle, so a rejection costs
    /// friction, never functionality.
    ///
    /// Pure function over a single item: no world state, no allocation, no side effects.
    ///
    /// Ported from the ModernUO shard. Four API differences, all forced by ServUO:
    /// PlayerConstructed is not on Item here (it lives on each equipment base class, so the check
    /// moved into the per-type branches); the three quality enums collapsed into one ItemQuality;
    /// Crafter is a Mobile rather than a string; and clothing carries its extra attributes as
    /// AosArmorAttributes.
    /// </summary>
    public static class QuestItemSafety
    {
        /// <summary>True when the item may be auto-counted and auto-consumed.</summary>
        public static bool CanAutoCount(Item item)
        {
            if (item == null || item.Deleted)
            {
                return false;
            }

            // Deliberately protected by the player, or otherwise not ordinary loot.
            if (item.LootType != LootType.Regular || item.Insured || item.BlessedFor != null)
            {
                return false;
            }

            // Personalized: renamed, or dyed/hued away from the default.
            // Note: types whose default hue is non-zero never auto-count. That is a false negative
            // in the safe direction - the player can still toggle them by hand.
            if (item.Name != null || item.Hue != 0)
            {
                return false;
            }

            var weapon = item as BaseWeapon;

            if (weapon != null)
            {
                return IsPlainWeapon(weapon);
            }

            var armor = item as BaseArmor;

            if (armor != null)
            {
                return IsPlainArmor(armor);
            }

            var clothing = item as BaseClothing;

            if (clothing != null)
            {
                return IsPlainClothing(clothing);
            }

            var jewel = item as BaseJewel;

            if (jewel != null)
            {
                return IsPlainJewel(jewel);
            }

            return true;
        }

        private static bool IsPlainWeapon(BaseWeapon weapon)
        {
            return weapon.Attributes.IsEmpty &&
                   weapon.WeaponAttributes.IsEmpty &&
                   weapon.SkillBonuses.IsEmpty &&
                   weapon.Quality == ItemQuality.Normal &&
                   // Player-crafted items carry a maker's mark and an exceptional bonus worth keeping.
                   !weapon.PlayerConstructed &&
                   weapon.Crafter == null &&
                   weapon.Slayer == SlayerName.None &&
                   weapon.Slayer2 == SlayerName.None &&
                   weapon.Poison == null &&
                   // Pre-AOS magic weapons carry no AosAttributes at all - they use these levels instead.
                   weapon.DamageLevel == WeaponDamageLevel.Regular &&
                   weapon.AccuracyLevel == WeaponAccuracyLevel.Regular &&
                   weapon.DurabilityLevel == WeaponDurabilityLevel.Regular &&
                   CraftResources.IsStandard(weapon.Resource);
        }

        private static bool IsPlainArmor(BaseArmor armor)
        {
            return armor.Attributes.IsEmpty &&
                   armor.ArmorAttributes.IsEmpty &&
                   armor.SkillBonuses.IsEmpty &&
                   armor.Quality == ItemQuality.Normal &&
                   !armor.PlayerConstructed &&
                   armor.Crafter == null &&
                   armor.ProtectionLevel == ArmorProtectionLevel.Regular &&
                   armor.Durability == ArmorDurabilityLevel.Regular &&
                   CraftResources.IsStandard(armor.Resource);
        }

        private static bool IsPlainClothing(BaseClothing clothing)
        {
            return clothing.Attributes.IsEmpty &&
                   clothing.ClothingAttributes.IsEmpty &&
                   clothing.SkillBonuses.IsEmpty &&
                   clothing.Resistances.IsEmpty &&
                   clothing.Quality == ItemQuality.Normal &&
                   !clothing.PlayerConstructed &&
                   clothing.Crafter == null &&
                   CraftResources.IsStandard(clothing.Resource);
        }

        private static bool IsPlainJewel(BaseJewel jewel)
        {
            return jewel.Attributes.IsEmpty &&
                   jewel.Resistances.IsEmpty &&
                   jewel.SkillBonuses.IsEmpty &&
                   jewel.Quality == ItemQuality.Normal &&
                   !jewel.PlayerConstructed &&
                   jewel.Crafter == null &&
                   CraftResources.IsStandard(jewel.Resource);
        }
    }
}
