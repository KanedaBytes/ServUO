// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// SpeechHues.cs — the colour a bot talks in.
//
// Set once at creation onto Mobile.SpeechHue, which is what overhead chat
// actually uses, and persisted by Mobile's own serialization. Deliberately not
// redeclared on PlayerBot: shadowing the inherited property would break Say()'s
// colour lookup.
//
// Nothing speaks yet - that is the next session. The hue is rolled now because
// it is part of the character, and because a crowd that is already visibly
// varied is the point of the tier/class model.

namespace Server.Custom
{
    public static class SpeechHues
    {
        /// <summary>No hue set: the client's default light-grey system colour.</summary>
        public const int Default = 0;

        // A curated palette rather than a random hue id, because most of the hue table is
        // unreadable as text - too dark, too washed out, or too close to the system colours a
        // player needs to be able to tell apart.
        public static readonly int[] Palette =
        {
            33,    // red - aggressive, mage red
            53,    // bright yellow
            63,    // bright cyan
            73,    // bright pink
            88,    // bright green - trader-feel
            93,    // bright blue - classic mage blue
            113,   // bright orange
            153,   // bright purple
            1153,  // royal blue, subtler than 93
            1175,  // dusky grey - brooding
            1281,  // near-black
            1287,  // crimson
            1361,  // gold
            1430,  // turquoise
            1502,  // rose

            // A few softer hues so the palette is not all-bright.
            38,    // muted forest green
            68,    // dusty sky blue
        };

        /// <summary>
        /// How often a bot keeps the default colour. Low on purpose - the inverse of a real
        /// shard, where a coloured name was the exception. A varied crowd reads as a crowd;
        /// seventeen shades of white reads as a spawner.
        /// </summary>
        private const double DefaultProbability = 0.10;

        public static int PickRandom()
        {
            if (Utility.RandomDouble() < DefaultProbability)
            {
                return Default;
            }

            return Palette[Utility.Random(Palette.Length)];
        }
    }
}
