using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch.Helpers;

namespace BLTAdoptAHero
{
    // What kind of BLT class this boss name "wants" - used to bias which of the streamer's
    // configured HeroClassDef entries gets picked for a given name, purely cosmetic (a Robin Hood
    // showing up as an archer reads better than as a lancer). Falls back to fully random if no
    // configured class matches the preference, so this never blocks a boss from spawning.
    public enum BossArchetype
    {
        Any,
        Archer,      // bow/crossbow-primary classes
        Melee,       // one/two-handed weapon-focused, unmounted
        Cavalry,     // Mounted == true
    }

    public readonly struct BossNameEntry
    {
        public readonly string Name;
        public readonly string Title;
        public readonly BossArchetype Archetype;
        // Culture StringId this figure belongs to, or "" for a name that fits any culture. When
        // set, the boss is equipped from this culture rather than a randomly drawn one, so
        // Sauron carries Mordor gear and Legolas carries Mirkwood gear.
        public readonly string CultureId;
        // Rarity this figure is worth. null means "fits any tier".
        public readonly BossRarity? Rarity;
        // Signature particle effect for this specific figure, e.g. the Witch-king trailing black
        // smoke. Empty means no effect. Names are vanilla particle systems - verified present in
        // Native/ModuleData/particle_systems*.xml; TAOM neither adds nor overrides any.
        public readonly string Pfx;
        public readonly ParticleEffectDef.AttachPointEnum PfxAttach;

        public BossNameEntry(string name, string title, BossArchetype archetype,
            string cultureId = "", BossRarity? rarity = null,
            string pfx = "",
            ParticleEffectDef.AttachPointEnum pfxAttach = ParticleEffectDef.AttachPointEnum.OnBody)
        {
            Name = name;
            Title = title;
            Archetype = archetype;
            CultureId = cultureId;
            Rarity = rarity;
            Pfx = pfx;
            PfxAttach = pfxAttach;
        }

        // The effect list to hand to AgentPfx, empty when this figure has no signature effect.
        public IEnumerable<ParticleEffectDef> ParticleEffects
            => string.IsNullOrEmpty(Pfx)
                ? Enumerable.Empty<ParticleEffectDef>()
                : new[] { new ParticleEffectDef { Name = Pfx, AttachPoint = PfxAttach } };

        public string FullName => string.IsNullOrEmpty(Title) ? Name : $"{Name} {Title}";
    }

    public static class BossNamePool
    {
        // Middle-earth roster for The Age of Middle-earth (TAOM), requested by aussielime_.
        //
        // Every entry is tied to three things at once, which is the whole point of the list:
        //   - a TAOM culture StringId, so the boss is equipped from that faction's gear;
        //   - a combat archetype, so Legolas spawns as an archer and Dain as cavalry;
        //   - a rarity, so the weight of the figure matches the weight of the boss. Legendary is
        //     reserved for the Fellowship and the powers that shaped the War of the Ring; Epic is
        //     named captains and lieutenants; Common is the rank-and-file with names.
        //
        // Where a faction has no canonical roster deep enough to fill a tier (goblins, the desert
        // cultures), the Common entries are flavour names built from that culture's naming style
        // rather than canon figures - marked below so nobody mistakes them for lore.
        public static readonly List<BossNameEntry> MiddleEarthEntries = new()
        {
            // ---- Mordor -------------------------------------------------------------------
            new("Sauron", "the Dark Lord", BossArchetype.Melee, "mordor", BossRarity.Legendary, pfx: "psys_game_burning_agent", pfxAttach: ParticleEffectDef.AttachPointEnum.OnBody),
            new("The Witch-king", "of Angmar", BossArchetype.Cavalry, "mordor", BossRarity.Legendary, pfx: "psys_haze_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnBody),
            new("Gothmog", "Lieutenant of Morgul", BossArchetype.Melee, "mordor", BossRarity.Epic, pfx: "psys_haze_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Shagrat", "Captain of Cirith Ungol", BossArchetype.Melee, "mordor", BossRarity.Epic, pfx: "psys_campfire_sparks", pfxAttach: ParticleEffectDef.AttachPointEnum.OnWeapon),
            new("The Mouth of Sauron", "", BossArchetype.Cavalry, "mordor", BossRarity.Epic, pfx: "psys_haze_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHead),
            new("Gorbag", "of Minas Morgul", BossArchetype.Melee, "mordor", BossRarity.Common),
            new("Grishnakh", "the Slaver", BossArchetype.Melee, "mordor", BossRarity.Common),
            new("Muzgash", "the Bowman", BossArchetype.Archer, "mordor", BossRarity.Common),
            new("Radbug", "the Cruel", BossArchetype.Melee, "mordor", BossRarity.Common),

            // ---- Isengard -----------------------------------------------------------------
            new("Saruman", "of Many Colours", BossArchetype.Melee, "isengard", BossRarity.Legendary, pfx: "psys_game_blacksmith_flame", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Lurtz", "the Uruk Captain", BossArchetype.Melee, "isengard", BossRarity.Epic, pfx: "psys_campfire_sparks", pfxAttach: ParticleEffectDef.AttachPointEnum.OnWeapon),
            new("Ugluk", "of the White Hand", BossArchetype.Melee, "isengard", BossRarity.Epic, pfx: "psys_campfire_sparks", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Mauhur", "the Ranger-slayer", BossArchetype.Archer, "isengard", BossRarity.Epic),
            new("Lugdush", "the Uruk", BossArchetype.Melee, "isengard", BossRarity.Common),
            new("Snaga", "the Tracker", BossArchetype.Archer, "isengard", BossRarity.Common),

            // ---- Gondor -------------------------------------------------------------------
            new("Aragorn", "son of Arathorn", BossArchetype.Melee, "gondor", BossRarity.Legendary, pfx: "psys_torch_fire_moving", pfxAttach: ParticleEffectDef.AttachPointEnum.OnWeapon),
            new("Boromir", "of the White Tower", BossArchetype.Melee, "gondor", BossRarity.Legendary, pfx: "psys_campfire_sparks", pfxAttach: ParticleEffectDef.AttachPointEnum.OnWeapon),
            new("Faramir", "Captain of Ithilien", BossArchetype.Archer, "gondor", BossRarity.Epic, pfx: "psys_game_sparkle_b", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Imrahil", "Prince of Dol Amroth", BossArchetype.Cavalry, "gondor", BossRarity.Epic, pfx: "psys_game_sparkle_b", pfxAttach: ParticleEffectDef.AttachPointEnum.OnWeapon),
            new("Beregond", "of the Tower Guard", BossArchetype.Melee, "gondor", BossRarity.Epic),
            new("Damrod", "the Ranger", BossArchetype.Archer, "gondor", BossRarity.Common),
            new("Mablung", "of Ithilien", BossArchetype.Archer, "gondor", BossRarity.Common),
            new("Ingold", "of the Causeway Forts", BossArchetype.Melee, "gondor", BossRarity.Common),

            // ---- Erebor (Dwarves) ---------------------------------------------------------
            new("Gimli", "son of Gloin", BossArchetype.Melee, "erebor", BossRarity.Legendary, pfx: "psys_campfire_sparks", pfxAttach: ParticleEffectDef.AttachPointEnum.OnWeapon),
            new("Dain", "Ironfoot", BossArchetype.Cavalry, "erebor", BossRarity.Legendary, pfx: "psys_campfire_sparks", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Thorin", "Oakenshield", BossArchetype.Melee, "erebor", BossRarity.Legendary, pfx: "psys_campfire_sparks", pfxAttach: ParticleEffectDef.AttachPointEnum.OnWeapon),
            new("Dwalin", "the Warrior", BossArchetype.Melee, "erebor", BossRarity.Epic, pfx: "psys_campfire_sparks", pfxAttach: ParticleEffectDef.AttachPointEnum.OnWeapon),
            new("Balin", "Lord of Moria", BossArchetype.Melee, "erebor", BossRarity.Epic, pfx: "psys_game_sparkle_b", pfxAttach: ParticleEffectDef.AttachPointEnum.OnWeapon),
            new("Kili", "the Young", BossArchetype.Archer, "erebor", BossRarity.Epic),
            new("Bofur", "the Miner", BossArchetype.Melee, "erebor", BossRarity.Common),
            new("Gloin", "the Stout", BossArchetype.Melee, "erebor", BossRarity.Common),
            new("Oin", "the Healer", BossArchetype.Melee, "erebor", BossRarity.Common),

            // ---- Rivendell (Noldor) -------------------------------------------------------
            new("Glorfindel", "the Balrog-slayer", BossArchetype.Cavalry, "rivendell", BossRarity.Legendary, pfx: "psys_torch_fire_moving", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHead),
            new("Elrond", "Half-elven", BossArchetype.Melee, "rivendell", BossRarity.Legendary, pfx: "psys_bug_fly_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnBody),
            new("Gandalf", "the Grey", BossArchetype.Melee, "rivendell", BossRarity.Legendary, pfx: "psys_campfire_sparks", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Elladan", "son of Elrond", BossArchetype.Cavalry, "rivendell", BossRarity.Epic),
            new("Elrohir", "son of Elrond", BossArchetype.Archer, "rivendell", BossRarity.Epic),
            new("Erestor", "of Imladris", BossArchetype.Melee, "rivendell", BossRarity.Epic),
            new("Gildor", "Inglorion", BossArchetype.Melee, "rivendell", BossRarity.Common),
            new("Lindir", "of the Last Homely House", BossArchetype.Archer, "rivendell", BossRarity.Common),

            // ---- Lothlorien (Galadhrim) ---------------------------------------------------
            new("Galadriel", "Lady of the Golden Wood", BossArchetype.Melee, "mirkwood", BossRarity.Legendary, pfx: "psys_game_sparkle_b", pfxAttach: ParticleEffectDef.AttachPointEnum.OnBody),
            new("Celeborn", "Lord of Lorien", BossArchetype.Melee, "mirkwood", BossRarity.Legendary, pfx: "psys_game_sparkle_b", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Haldir", "of Lorien", BossArchetype.Archer, "mirkwood", BossRarity.Epic, pfx: "psys_bug_fly_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Rumil", "of Lorien", BossArchetype.Archer, "mirkwood", BossRarity.Common),
            new("Orophin", "of Lorien", BossArchetype.Archer, "mirkwood", BossRarity.Common),

            // ---- Mirkwood (Silvan Elves) --------------------------------------------------
            new("Legolas", "Greenleaf", BossArchetype.Archer, "mirkwood", BossRarity.Legendary, pfx: "psys_bug_fly_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Thranduil", "the Elvenking", BossArchetype.Cavalry, "mirkwood", BossRarity.Legendary, pfx: "psys_bug_fly_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHead),
            new("Tauriel", "of the Woodland Realm", BossArchetype.Archer, "mirkwood", BossRarity.Epic, pfx: "psys_bug_fly_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Feren", "of the Woodland Guard", BossArchetype.Archer, "mirkwood", BossRarity.Common),
            new("Galion", "of the Elvenking's Halls", BossArchetype.Melee, "mirkwood", BossRarity.Common),

            // ---- Gundabad Orcs ------------------------------------------------------------
            new("Azog", "the Defiler", BossArchetype.Cavalry, "gundabad", BossRarity.Legendary, pfx: "psys_haze_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Yazneg", "the Warg-rider", BossArchetype.Cavalry, "gundabad", BossRarity.Epic),
            new("Fimbul", "the Hunter", BossArchetype.Melee, "gundabad", BossRarity.Epic),
            new("Narzug", "the Warg-archer", BossArchetype.Archer, "gundabad", BossRarity.Common),
            new("Ragash", "the Pit-fighter", BossArchetype.Melee, "gundabad", BossRarity.Common), // flavour

            // ---- Dol Guldur Orcs ----------------------------------------------------------
            new("Khamul", "the Easterling", BossArchetype.Cavalry, "dolguldur", BossRarity.Legendary, pfx: "psys_haze_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHead),
            new("The Nazgul", "of Dol Guldur", BossArchetype.Cavalry, "dolguldur", BossRarity.Epic, pfx: "psys_haze_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnBody),
            new("Ufthak", "the Gaoler", BossArchetype.Melee, "dolguldur", BossRarity.Epic),
            new("Lagduf", "the Warden", BossArchetype.Melee, "dolguldur", BossRarity.Common),
            new("Shrakh", "of the Black Pit", BossArchetype.Archer, "dolguldur", BossRarity.Common), // flavour

            // ---- Goblins ------------------------------------------------------------------
            new("The Great Goblin", "", BossArchetype.Melee, "gundabad", BossRarity.Legendary, pfx: "psys_haze_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnBody),
            new("Grinnah", "the Goblin", BossArchetype.Melee, "gundabad", BossRarity.Epic),
            new("Yagul", "the Cave-crawler", BossArchetype.Archer, "gundabad", BossRarity.Common), // flavour
            new("Skrat", "the Tunnel-runner", BossArchetype.Melee, "gundabad", BossRarity.Common), // flavour

            // ---- Misty Mountain Orcs ------------------------------------------------------
            new("Bolg", "of the North", BossArchetype.Melee, "gundabad", BossRarity.Legendary, pfx: "psys_haze_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Golfimbul", "of Mount Gram", BossArchetype.Cavalry, "gundabad", BossRarity.Epic),
            new("Uzbad", "the Skirmisher", BossArchetype.Archer, "gundabad", BossRarity.Common), // flavour
            new("Grukh", "the Stone-crawler", BossArchetype.Melee, "gundabad", BossRarity.Common), // flavour

            // ---- Umbar --------------------------------------------------------------------
            new("Castamir", "the Usurper", BossArchetype.Melee, "gondor", BossRarity.Legendary, pfx: "psys_torch_fire_moving", pfxAttach: ParticleEffectDef.AttachPointEnum.OnWeapon),
            new("Angamaite", "of Umbar", BossArchetype.Melee, "gondor", BossRarity.Epic, pfx: "psys_torch_fire_moving", pfxAttach: ParticleEffectDef.AttachPointEnum.OnWeapon),
            new("Sangahyando", "of Umbar", BossArchetype.Melee, "gondor", BossRarity.Epic),
            new("Herumor", "the Black Numenorean", BossArchetype.Melee, "gondor", BossRarity.Common),
            new("Balakhor", "the Corsair", BossArchetype.Archer, "gondor", BossRarity.Common), // flavour

            // ---- Shaghana (southern desert culture) ---------------------------------------
            new("Suladan", "the Serpent Lord", BossArchetype.Cavalry, "mordor", BossRarity.Legendary, pfx: "psys_torch_fire_moving", pfxAttach: ParticleEffectDef.AttachPointEnum.OnWeapon),
            new("Fuinur", "the Renegade", BossArchetype.Melee, "mordor", BossRarity.Epic),
            new("The Mumak-master", "of Harad", BossArchetype.Cavalry, "mordor", BossRarity.Epic, pfx: "psys_game_sparkle_b", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Zimrathor", "the Sun-archer", BossArchetype.Archer, "mordor", BossRarity.Common), // flavour
            new("Harun", "the Dune-runner", BossArchetype.Melee, "mordor", BossRarity.Common), // flavour

            // ---- Abanissa (southern desert culture) ---------------------------------------
            new("Adunaphel", "the Quiet", BossArchetype.Cavalry, "mordor", BossRarity.Legendary, pfx: "psys_haze_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnBody),
            new("Akhorahil", "the Blind Sorcerer", BossArchetype.Cavalry, "mordor", BossRarity.Epic, pfx: "psys_haze_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Ren", "the Unclean", BossArchetype.Cavalry, "mordor", BossRarity.Epic, pfx: "psys_haze_1", pfxAttach: ParticleEffectDef.AttachPointEnum.OnHands),
            new("Ibal", "of the Blue Sands", BossArchetype.Archer, "mordor", BossRarity.Common), // flavour
            new("Azrabeth", "the Spear-maiden", BossArchetype.Melee, "mordor", BossRarity.Common), // flavour
        };

        // Historical/mythological pool used on installs that are not TAOM, where the Middle-earth
        // names would make no sense. Untagged by culture - these are equipped as before.
        public static readonly List<BossNameEntry> Entries = new()
        {
            // Archers
            new("Robin Hood", "the Outlaw", BossArchetype.Archer),
            new("Arjuna", "the Peerless Archer", BossArchetype.Archer),
            new("Odysseus", "Bender of the Great Bow", BossArchetype.Archer),
            new("William Tell", "the Marksman", BossArchetype.Archer),
            new("Houyi", "the Sky-Piercer", BossArchetype.Archer),
            new("Skadi", "the Winter Huntress", BossArchetype.Archer),
            new("Merida", "of the Wilds", BossArchetype.Archer),

            // Melee (swordsmen, duelists, heavy infantry)
            new("El Cid", "the Undefeated", BossArchetype.Melee),
            new("Beowulf", "the Monster-Slayer", BossArchetype.Melee),
            new("Musashi", "the Sword Saint", BossArchetype.Melee),
            new("Achilles", "the Wrathful", BossArchetype.Melee),
            new("Hector", "Breaker of Horses", BossArchetype.Melee),
            new("Leonidas", "the Unyielding", BossArchetype.Melee),
            new("Guan Yu", "the Blade Saint", BossArchetype.Melee),
            new("Joan of Arc", "the Maid of Orleans", BossArchetype.Melee),
            new("William Wallace", "the Braveheart", BossArchetype.Melee),

            // Cavalry / commanders
            new("Attila", "Scourge of the West", BossArchetype.Cavalry),
            new("Saladin", "the Just", BossArchetype.Cavalry),
            new("Richard", "the Lionheart", BossArchetype.Cavalry),
            new("Vlad", "the Impaler", BossArchetype.Cavalry),
            new("Genghis Khan", "the Conqueror", BossArchetype.Cavalry),
            new("Boudica", "the Rebel Queen", BossArchetype.Cavalry),
            new("Hannibal", "of Carthage", BossArchetype.Cavalry),

            // Any / wildcards - fine on any class, used as filler and for the "picked randomly
            // regardless of class" feel requested in chat
            new("Grendel", "the Devourer", BossArchetype.Any),
            new("Cu Chulainn", "the Hound of Ulster", BossArchetype.Any),
            new("Sigurd", "the Dragon-Slayer", BossArchetype.Any),
            new("Xena", "the Warrior Princess", BossArchetype.Any),
            new("Conan", "the Barbarian", BossArchetype.Any),
        };

        private static readonly Random Rng = new();

        // Prefer a name matching the boss's actual chosen archetype; if none of the pool fits,
        // fall back to any entry rather than failing to name the boss at all.
        public static BossNameEntry Pick(BossArchetype archetype)
        {
            var matching = Entries.FindAll(e => e.Archetype == archetype || e.Archetype == BossArchetype.Any);
            var pool = matching.Count > 0 ? matching : Entries;
            return pool[Rng.Next(pool.Count)];
        }

        // Pick a Middle-earth figure worth this rarity, whose culture actually exists in the
        // loaded game, preferring one that also matches the class archetype.
        //
        // Returns null when no Middle-earth culture is present at all (a non-TAOM install), which
        // is the caller's signal to fall back to the historical pool and the ordinary culture
        // selection. Archetype is relaxed before rarity is: a Legendary boss with the wrong weapon
        // reads better than a Common name on a Legendary boss.
        public static BossNameEntry? PickForRarity(BossRarity rarity, BossArchetype archetype,
            Func<string, bool> cultureExists)
        {
            if (cultureExists == null) return null;

            var available = MiddleEarthEntries.Where(e => cultureExists(e.CultureId)).ToList();
            if (available.Count == 0) return null;

            var sameRarity = available.Where(e => e.Rarity == rarity).ToList();
            if (sameRarity.Count == 0) sameRarity = available;

            var exact = sameRarity
                .Where(e => e.Archetype == archetype || e.Archetype == BossArchetype.Any)
                .ToList();
            var pool = exact.Count > 0 ? exact : sameRarity;

            return pool[Rng.Next(pool.Count)];
        }
    }
}
