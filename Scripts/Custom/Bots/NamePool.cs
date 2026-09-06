// -----------------------------------------------------------------------------
// Derived from the PlayerBots system in Klein187/uo-offline (GPL-3.0).
// This file is licensed under the GNU General Public License, version 3.
// See LICENSE-BOTS at the repository root.
// -----------------------------------------------------------------------------
//
// NamePool.cs — names for bots, and the registry of which are in use.
//
// Two jobs, and the second is the one that matters operationally:
//
//   1. Roll a name that reads like a 1999 player's. Curated period lists, a
//      seasoning of real player handles, and an algorithmic generator behind
//      them so the space never runs out.
//
//   2. Track which names are live. _inUse is claimed on bot creation and
//      released on delete, which makes InUseCount an O(1) census of the live
//      bot population - and that is what the Bots.Population health check
//      reports, rather than walking World.Mobiles (CLAUDE.md section 15).
//
// Case-insensitive, so "Bob" blocks "bob".

using System;
using System.Collections.Generic;

namespace Server.Custom
{
    public static class NamePool
    {
        private static readonly string[] MaleNames =
        {
            // Anglo-Saxon / Old English
            "Aldric", "Athelstan", "Beorn", "Cedric", "Cuthbert",
            "Drogo", "Eadwin", "Edric", "Edwyn", "Egbert",
            "Godric", "Hadrian", "Halric", "Hroth", "Hugh",
            "Leofric", "Osric", "Oswin", "Wendric", "Wulfgar",

            // Norse-flavored
            "Bjorn", "Erik", "Gunnar", "Harald", "Helmir",
            "Ivar", "Kjell", "Leif", "Magnus", "Olaf",
            "Ragnar", "Rolf", "Sten", "Sven", "Thorgil",
            "Torvald", "Ulfgar", "Vali", "Vidar",

            // Celtic / Welsh / Gaelic
            "Aiden", "Alistair", "Bran", "Cormac", "Declan",
            "Eamon", "Fergus", "Finn", "Gawain", "Kael",
            "Liam", "Lorcan", "Owen", "Rhys", "Ronan",

            // Fantasy classic
            "Aelius", "Aethel", "Albric", "Alaric",
            "Arden", "Aric", "Bram", "Brandt",
            "Caedmon", "Caelan", "Caine", "Caspian", "Corwin",
            "Daven", "Devin", "Donovan", "Draven", "Dyson",
            "Eldric", "Eldwin", "Elric", "Emeric",
            "Galen", "Garrick", "Gavric", "Gerard", "Gideon",
            "Halcyon", "Hawthorne", "Hektor",
            "Idric", "Ilric", "Ivor",
            "Jareth", "Joren", "Jorgen",
            "Kaelen", "Kestrel", "Korin",
            "Lael", "Loras", "Loric",
            "Mardus", "Maric", "Marius", "Merrick", "Morric",
            "Nessen", "Nyvar",
            "Olric", "Orin",
            "Padric", "Pelric", "Percy",
            "Quill", "Quintus",
            "Rael", "Renly", "Rhett", "Roric", "Rylan",
            "Sael", "Seoric", "Soren", "Stellan",
            "Thane", "Theoric", "Tobias", "Tomric", "Tristan",
            "Ulric", "Uther",
            "Valen", "Varric", "Vesric",
            "Wolfric",
            "Yorick", "Yvain",

            // Shortform / nicknames
            "Bart", "Bert", "Conn", "Dax",
            "Gus", "Hal", "Hux", "Jock", "Kit",
            "Mace", "Ned", "Nyl", "Rick", "Sam",
            "Stan", "Tig", "Tor", "Wat", "Wim"
        };

        private static readonly string[] FemaleNames =
        {
            // Anglo-Saxon / Old English
            "Adelyn", "Aldreth", "Alfreda", "Anwen", "Aria",
            "Bethany", "Brida", "Brunhild", "Edith",
            "Elspeth", "Esme", "Etta", "Faye", "Freya",
            "Gilda", "Hilda", "Imogen", "Isolde", "Lyra",
            "Mara", "Meridian", "Morag", "Morwen", "Nessa",
            "Odette", "Petra", "Riona", "Rowena", "Saoirse",
            "Sigrid", "Tamsin", "Una", "Verity", "Wenna",
            "Wren", "Yseult",

            // Norse-flavored
            "Astrid", "Brunhilde", "Dagny", "Eira", "Frejya",
            "Gerda", "Helga", "Inga", "Ingrid", "Liv",
            "Sif", "Signe", "Sigyn", "Solveig", "Thora",
            "Tove", "Vigdis",

            // Celtic / Welsh / Gaelic
            "Aine", "Bree", "Caitir", "Ceridwen", "Cliodhna",
            "Daire", "Deirdre", "Enid", "Eithne",
            "Fiana", "Grainne", "Iona", "Kayleigh", "Maeve",
            "Niamh", "Roisin", "Siobhan", "Tara",

            // Fantasy classic
            "Aelinor", "Aetha", "Aila", "Alessa", "Amara",
            "Arwyn", "Aurelia", "Aveline", "Bryn",
            "Calliope", "Calyx", "Celene", "Cerys",
            "Dalia", "Delyn", "Drusilla",
            "Elara", "Elowen", "Elyna", "Ember", "Eris",
            "Faela", "Fenra",
            "Gwyn", "Gwendoline",
            "Halia", "Helene", "Iselda", "Isla",
            "Jessa", "Joryn",
            "Kaela", "Kira", "Korin",
            "Lael", "Lara", "Lirien",
            "Marda", "Maren", "Mira", "Myrra",
            "Nala", "Nyra",
            "Orla", "Oryn",
            "Pira", "Pyrra",
            "Rana", "Riven", "Roselin", "Rowan", "Rylee",
            "Sable", "Sael", "Saira", "Selene", "Senna",
            "Shyra", "Sylva",
            "Tessa", "Thira", "Tira",
            "Ursa",
            "Vala", "Vela", "Vesna", "Vyra",
            "Yelena", "Yelka", "Yrsa",
            "Zara", "Zora",

            // Period shortform
            "Bea", "Cat", "Edie", "Fae", "Gertie",
            "Hettie", "Ivy", "Jo", "Liss",
            "May", "Nell", "Pip", "Rea", "Sal",
            "Tess", "Vi", "Win"
        };

        // Algorithmic generator parts. Combining one prefix with one
        // suffix gives names that "sound right" but aren't in any pool.
        private static readonly string[] MalePrefixes =
        {
            "Ael", "Ald", "Alar", "Arn", "Bal", "Bor", "Bran",
            "Cae", "Cor", "Dar", "Dor", "Dur", "Ed",
            "El", "Fal", "Far", "Fen", "Gal", "Gar", "Gor", "Gun",
            "Hal", "Har", "Helm", "Hold", "Ior", "Jar", "Kael",
            "Lor", "Mar", "Mor", "Nael", "Nor", "Oric", "Quin",
            "Rael", "Ric", "Ror", "Sael", "Sor", "Tar", "Thal",
            "Tor", "Tul", "Ulf", "Val", "Vael", "Vor", "Wend",
            "Wulf", "Yor"
        };

        private static readonly string[] MaleSuffixes =
        {
            "ric", "in", "an", "ar", "or", "us", "as", "is",
            "wyn", "win", "den", "dan", "dor", "gar", "mund",
            "old", "olf", "wald", "fred", "ward", "ron",
            "vin", "th", "stan", "fast", "berg", "horn", "moor"
        };

        private static readonly string[] FemalePrefixes =
        {
            "Ael", "Ais", "Aly", "Ari", "Bri", "Cae", "Cera",
            "Dae", "Dyl", "Ela", "Eli", "Eva", "Fae", "Far",
            "Fen", "Gwen", "Hael", "Hel", "Ily", "Iren", "Isol",
            "Kel", "Lael", "Lir", "Lyn", "Mae", "Mar", "Mor",
            "Myr", "Nael", "Niam", "Nyr", "Ori", "Rae", "Rin",
            "Sael", "Sel", "Ser", "Syl", "Thal", "Thi", "Tris",
            "Val", "Vel", "Vyr", "Wen", "Wyn", "Yri"
        };

        private static readonly string[] FemaleSuffixes =
        {
            "a", "ia", "yn", "wen", "lin", "wyn", "ara", "ena",
            "essa", "ira", "elle", "ette", "anna", "issa",
            "ora", "rys", "ndra", "ade", "ene", "ine", "rin",
            "wynn", "lyn", "ya", "ana", "ela", "elia", "ona", "wina"
        };

        // ---- Player handles ----
        //
        // What actual 1999 players named themselves: fantasy-lit borrows,
        // one-word tough-guy nouns, and unadorned lowercase real names.
        // Rolled at a modest rate so they season the population without
        // turning it into a Tolkien convention.
        private static readonly string[] MaleHandles =
        {
            "Gandalf", "Merlin", "Legolas", "Aragorn", "Gimli",
            "Raistlin", "Caramon", "Tanis", "Sturm", "Drizzt",
            "Elminster", "Conan", "Lancelot", "Galahad", "Mordred",
            "Strider", "Beowulf", "Roland", "Tristram",
            "Blade", "Reaper", "Shadow", "Phantom", "Storm",
            "Hawk", "Wolf", "Viper", "Falcon", "Talon",
            "Slasher", "Warlord", "Ranger", "Outlaw", "Bandit",
            "bob", "joe", "dave", "steve", "mike", "matt",
            "chris", "tom", "dan", "rob", "tim", "jeff",
            "kevin", "brian", "nick", "pete", "carl", "gary"
        };

        private static readonly string[] FemaleHandles =
        {
            "Xena", "Morgana", "Guinevere", "Arwen", "Eowyn",
            "Galadriel", "Morrigan", "Circe", "Cassandra", "Ophelia",
            "Raven", "Willow", "Ember", "Mystique", "Tempest",
            "Shadowdancer", "Moonshadow", "Starlight", "Silverwind",
            "Nightshade", "Wildfire", "Whisper",
            "sarah", "jenny", "lisa", "amy", "katie", "beth",
            "meg", "kate", "jess", "nikki", "carrie", "dawn"
        };

        // How often a fresh roll comes from the handle pool instead of the
        // curated period lists.
        private const double HandleChance = 0.08;

        // ---- Surnames ----
        //
        // A minority of the population carries one from birth; the rest
        // pick one up only if their first name is already taken.
        private const double SurnameChance = 0.25;

        private static readonly string[] FamilySurnames =
        {
            "Blackthorn", "Stormrider", "Ironheart", "Ravenwood",
            "Ashdown", "Thornfield", "Winterborne", "Hawkins",
            "Blackwood", "Greenfield", "Stonebridge", "Fairweather",
            "Oakhurst", "Redfern", "Silverleaf", "Grimm",
            "Weatherby", "Holloway", "Marsh", "Frost",
            "Nightingale", "Swift", "Crowe", "Thatcher",
            "Fletcher", "Cooper", "Wainwright", "Ashford",
            "Duskwalker", "Emberfall", "Ironwood", "Wolfsbane",
            "Stormcrow", "Longstrider", "Coldwater", "Highmoor"
        };

        private static readonly string[] PlaceSurnames =
        {
            "of Britain", "of Trinsic", "of Yew", "of Minoc",
            "of Vesper", "of Moonglow", "of Skara Brae", "of Jhelom",
            "of the North", "of the Woods"
        };

        private static readonly string[] EpithetSurnames =
        {
            "the Grey", "the Red", "the Bold", "the Quiet",
            "the Wanderer", "the Younger", "the Elder", "the Swift",
            "the Unlucky", "the Lame", "the Pious", "the Black"
        };

        // ---- Live-name registry ----
        //
        // Names currently walking the world. Claimed on creation, released on delete.

        private static readonly HashSet<string> _inUse =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// How many bot names are live. The O(1) population census - nothing needs to walk
        /// World.Mobiles to answer "how many bots are there?".
        /// </summary>
        public static int InUseCount
        {
            get { return _inUse.Count; }
        }

        /// <summary>Register a name as live. False if it was already taken.</summary>
        public static bool Claim(string name)
        {
            return !String.IsNullOrEmpty(name) && _inUse.Add(name);
        }

        public static void Release(string name)
        {
            if (!String.IsNullOrEmpty(name))
            {
                _inUse.Remove(name);
            }
        }

        /// <summary>
        /// Forget every claimed name. Only for a full teardown - the smoke test spawns one bot
        /// per class and deletes them, and a leaked claim there would slowly poison the census.
        /// </summary>
        public static void Clear()
        {
            _inUse.Clear();
        }

        /// <summary>
        /// Roll a name no live bot is using, and claim it.
        ///
        /// Escalating: plain roll, then add a surname, then the algorithmic space, then
        /// algorithmic plus surname. The later rungs are tens of thousands deep, so exhaustion is
        /// practically impossible; the final fallback accepts a duplicate rather than failing,
        /// because a bot with a shared name is a cosmetic problem and a bot with no name is not.
        /// </summary>
        public static string PickUnique(bool female)
        {
            // 1) Plain roll. Some get a surname anyway - flavour, not rescue.
            for (int i = 0; i < 8; i++)
            {
                string name = RollBase(female);

                if (Utility.RandomDouble() < SurnameChance)
                {
                    name = AttachSurname(name);
                }

                if (Claim(name))
                {
                    return name;
                }
            }

            // 2) First name taken - a surname disambiguates. The second Tessa on the shard
            //    becomes Tessa Ravenwood.
            for (int i = 0; i < 12; i++)
            {
                string name = AttachSurname(RollBase(female));

                if (Claim(name))
                {
                    return name;
                }
            }

            // 3) Algorithmic space, then algorithmic plus surname.
            for (int i = 0; i < 40; i++)
            {
                string name = Generate(female);

                if (i >= 20)
                {
                    name = AttachSurname(name);
                }

                if (Claim(name))
                {
                    return name;
                }
            }

            return PickRandom(female);
        }

        private static string RollBase(bool female)
        {
            if (Utility.RandomDouble() < HandleChance)
            {
                string[] handles = female ? FemaleHandles : MaleHandles;
                return handles[Utility.Random(handles.Length)];
            }

            return PickRandom(female);
        }

        private static string AttachSurname(string name)
        {
            double roll = Utility.RandomDouble();

            string[] pool = roll < 0.70 ? FamilySurnames
                : roll < 0.85 ? PlaceSurnames
                : EpithetSurnames;

            return name + " " + pool[Utility.Random(pool.Length)];
        }

        public static string PickRandom(bool female)
        {
            if (Utility.RandomDouble() < 0.10)
            {
                return Generate(female);
            }

            string[] pool = female ? FemaleNames : MaleNames;

            return pool[Utility.Random(pool.Length)];
        }

        private static string Generate(bool female)
        {
            if (female)
            {
                return FemalePrefixes[Utility.Random(FemalePrefixes.Length)]
                    + FemaleSuffixes[Utility.Random(FemaleSuffixes.Length)];
            }

            return MalePrefixes[Utility.Random(MalePrefixes.Length)]
                + MaleSuffixes[Utility.Random(MaleSuffixes.Length)];
        }
    }
}
