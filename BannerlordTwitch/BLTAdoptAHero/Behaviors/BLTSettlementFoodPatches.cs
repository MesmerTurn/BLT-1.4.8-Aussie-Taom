using System;
using System.Collections.Generic;
using System.Reflection;
using BannerlordTwitch.Util;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;

namespace BLTAdoptAHero
{
    /// <summary>
    /// World-wide food help for towns and castles.
    ///
    /// An overhaul can leave settlements unable to feed themselves - villages bound to a town
    /// produce less than the town's prosperity and garrison eat, so food drifts down everywhere
    /// at once, for every faction. Nothing in the campaign corrects that on its own, and the
    /// settlements that starve first are the ones under siege or freshly taken.
    ///
    /// Both knobs default to off. The bonus is flat rather than a percentage on purpose: a
    /// percentage applied to the food change would also multiply the losses a settlement takes
    /// while it is being besieged or its villages are being raided, which is the exact moment
    /// the help is meant to arrive.
    /// </summary>
    [HarmonyPatch]
    public static class TownFoodBonusPatch
    {
        private const string ModelType =
            "TaleWorlds.CampaignSystem.GameComponents.DefaultSettlementFoodModel";

        // The model exposes the food change twice: a public method the UI asks, and an internal
        // one the campaign's own daily tick goes through. Patching only the public one moved the
        // number the player could see in their own town while every AI settlement in the world
        // kept starving on the internal path. Both are patched, and this flag makes sure the
        // bonus lands exactly once when a single call passes through both.
        [ThreadStatic] private static bool appliedInInternal;

        // Resolved by name, and skipped cleanly if the type ever moves: a typeof patch against a
        // missing type throws out of PatchAll and takes every other patch in this assembly down.
        static bool Prepare() => AccessTools.TypeByName(ModelType) != null;

        static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName(ModelType);
            if (type == null) yield break;

            foreach (string name in new[]
                     { "CalculateTownFoodChangeInternal", "CalculateTownFoodStocksChange" })
            {
                var method = AccessTools.Method(type, name);
                if (method != null) yield return method;
            }
        }

        static void Prefix(MethodBase __originalMethod)
        {
            if (__originalMethod?.Name == "CalculateTownFoodStocksChange")
                appliedInInternal = false;
        }

        static void Postfix(Town town, ref ExplainedNumber __result, MethodBase __originalMethod)
        {
            try
            {
                float bonus = BLTAdoptAHeroModule.CommonConfig?.TownFoodDailyBonus ?? 0f;
                if (bonus <= 0f || town == null) return;

                bool isInternal = __originalMethod?.Name == "CalculateTownFoodChangeInternal";

                // The public method's result already contains the bonus whenever it delegated to
                // the internal one, so adding it again there would double it.
                if (!isInternal && appliedInInternal) return;

                __result.Add(bonus, new TextObject("{=BLTFoodBonus}Bannerlord Twitch"), null);

                if (isInternal) appliedInInternal = true;
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(TownFoodBonusPatch)}.{nameof(Postfix)}", ex);
            }
        }
    }

    /// <summary>
    /// Raises how much food a settlement may bank, so a good run of production carries it through
    /// a bad one instead of spilling over the cap and being thrown away.
    /// </summary>
    [HarmonyPatch]
    public static class TownFoodStoragePatch
    {
        private const string ModelType =
            "TaleWorlds.CampaignSystem.GameComponents.DefaultSettlementFoodModel";

        static bool Prepare() => AccessTools.TypeByName(ModelType) != null;

        static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName(ModelType);
            var prop = type?.GetProperty("FoodStocksUpperLimit",
                BindingFlags.Public | BindingFlags.Instance);
            var getter = prop?.GetGetMethod();
            if (getter != null) yield return getter;
        }

        static void Postfix(ref int __result)
        {
            try
            {
                int bonus = BLTAdoptAHeroModule.CommonConfig?.TownFoodStorageBonus ?? 0;
                if (bonus <= 0) return;

                __result += bonus;
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(TownFoodStoragePatch)}.{nameof(Postfix)}", ex);
            }
        }
    }
}
