using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using BannerlordTwitch.Util;

namespace BannerlordTwitch.Localization
{
    public static class Naming
    {
        public static readonly string To = "{=2Ncpkenz}→".Translate();
        public static readonly string Inc = "{=83uNsc2Z}+".Translate();
        public static readonly string Dec = "{=drHSxMvO}−".Translate();
        public static readonly string Gold = "{=BolFnYCO}⦷".Translate();
        public static readonly string XP = "{=JFCeRYdn}XP".Translate();
        public static readonly string HP = "{=NtqSb7B7}HP".Translate();
        public static readonly string Lvl = "{=RQlq59mz}lvl".Translate();
        public static readonly string Item = "{=5rErcMGl}Item".Translate();
        public static readonly string Skills = "{=5gVLi7NA}Skills".Translate();
        public static readonly string Sep = "{=aG3roJj3} ■".Translate();
        public static readonly string Sep2 = "{=AmuKxrmY} ■".Translate();

        public static string NotEnoughGold(int need, int have) =>
            "{=fuwuk4bR}Not enough {GoldIcon}: need {NeededGold}{GoldIcon}, have {HaveGold}{GoldIcon}!"
                .Translate(
                    ("GoldIcon", Gold),
                    ("NeededGold", need),
                    ("HaveGold", have)
                    );

        public static string JoinList(IEnumerable<string> list) => string.Join(Sep, list);

        /// <summary>
        /// Cleans a name that came from chat before it is written onto anything the campaign
        /// keeps - a clan, a kingdom, a hero, a custom item.
        ///
        /// The reason is the overlay. Names entered in chat are rendered there, and they are not
        /// only shown as text: some are interpolated into button handlers. A name carrying the
        /// wrong punctuation can therefore stop being data and start being page code, and because
        /// the name is stored in the save it would do so every time the overlay drew it, not just
        /// once. The overlay sends its commands as the broadcaster, so that matters.
        ///
        /// Stripping at the point of entry is not a substitute for escaping at the point of
        /// display - it is the cheaper of the two layers, and the one that also keeps these names
        /// readable. Anything that reaches the overlay from elsewhere still needs the display side
        /// doing its job.
        /// </summary>
        public static string SanitizeUserProvidedName(string name, int maxLength = 48)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                // Control characters would not render anyway, and newlines let a name pretend to
                // be several lines of something else.
                if (char.IsControl(c)) continue;

                switch (c)
                {
                    // Markup and quoting: the characters that let a value escape whatever it was
                    // embedded in, whether that is an element, an attribute, or a script string.
                    case '<':
                    case '>':
                    case '&':
                    case '"':
                    case '\'':
                    case '`':
                    case '\\':
                    // Already stripped from item names before this existed, kept for the same
                    // reason: it is markup in the game's own text system.
                    case '#':
                    // Braces delimit variables in TextObject, so a name containing them can eat
                    // the text it is placed into.
                    case '{':
                    case '}':
                        continue;
                    default:
                        sb.Append(c);
                        break;
                }
            }

            // Collapse the runs of spaces left behind by the removals, so "Foo   Bar" does not
            // come out looking like a mistake.
            string cleaned = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();

            return cleaned.Length > maxLength ? cleaned.Substring(0, maxLength).Trim() : cleaned;
        }
    }
}
