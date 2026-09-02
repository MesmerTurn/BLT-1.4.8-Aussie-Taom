using System.Collections.Generic;

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

        public BossNameEntry(string name, string title, BossArchetype archetype)
        {
            Name = name;
            Title = title;
            Archetype = archetype;
        }

        public string FullName => string.IsNullOrEmpty(Title) ? Name : $"{Name} {Title}";
    }

    // Names drawn from real historical, mythological and folkloric warrior figures, each tagged
    // with the combat archetype they're best known for. This is a starting pool, not exhaustive -
    // add to it freely, the spawn logic just needs Name/Title/Archetype.
    public static class BossNamePool
    {
        public static readonly List<BossNameEntry> Entries = new()
        {
            // Archers
            new("Robin Hood", "the Outlaw", BossArchetype.Archer),
            new("Arjuna", "the Peerless Archer", BossArchetype.Archer),
            new("Odysseus", "Bender of the Great Bow", BossArchetype.Archer),
            new("William Tell", "the Marksman", BossArchetype.Archer),
            new("Legolas", "Greenleaf", BossArchetype.Archer),
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

        private static readonly System.Random Rng = new();

        // Prefer a name matching the boss's actual chosen archetype; if none of the pool fits,
        // fall back to any entry rather than failing to name the boss at all.
        public static BossNameEntry Pick(BossArchetype archetype)
        {
            var matching = Entries.FindAll(e => e.Archetype == archetype || e.Archetype == BossArchetype.Any);
            var pool = matching.Count > 0 ? matching : Entries;
            return pool[Rng.Next(pool.Count)];
        }
    }
}
