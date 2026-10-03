using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BLTAdoptAHero.Util
{
    /// <summary>
    /// Keeps a viewer in their clan across a save/load.
    ///
    /// BLT lets a viewer join a clan without being a noble. The game's own
    /// Hero.PreAfterLoad only rebuilds the clan's hero caches for lords and for
    /// separately registered companions, so a viewer who is neither keeps the clan on
    /// their hero but disappears from the clan's roster the moment the save is loaded.
    ///
    /// Putting them back through the same callback the Clan setter uses restores the
    /// alive/dead and kingdom caches without touching occupation, companion status,
    /// parties, gold or the saved membership itself.
    ///
    /// Fix taken from DarkTiger512's BLT fork (Classic 5.5.2).
    /// </summary>
    [HarmonyPatch(typeof(Hero), "PreAfterLoad")]
    internal static class ViewerClanMembershipPatch
    {
        private static readonly Action<Clan, Hero> AddLord =
            (Action<Clan, Hero>)Delegate.CreateDelegate(typeof(Action<Clan, Hero>),
                AccessTools.Method(typeof(Clan), "OnLordAdded", new[] { typeof(Hero) }));

        [HarmonyPostfix]
        internal static void Postfix(Hero __instance)
        {
            // Companions and nobles are rebuilt by the game already. Never hand a hero a
            // clan they did not have in the save that was just loaded.
            if (__instance.CharacterObject.IsObsolete || !__instance.IsAdopted()
                || __instance.IsLord || __instance.CompanionOf != null)
                return;

            var clan = __instance.Clan;
            if (clan == null || clan.StringId == "neutral" || clan.Heroes.Contains(__instance))
                return;

            AddLord(clan, __instance);
        }
    }
}
