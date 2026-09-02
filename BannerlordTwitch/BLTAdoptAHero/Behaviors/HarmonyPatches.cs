using System;
using System.Collections.Generic;
using System.Reflection;
using Helpers;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using BannerlordTwitch.Util;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.CampaignSystem.ViewModelCollection.KingdomManagement.Diplomacy;
using static TaleWorlds.MountAndBlade.Launcher.Library.NativeMessageBox;
using TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors;
using TaleWorlds.CampaignSystem.Siege;
using System.Linq;
using TaleWorlds.CampaignSystem.MapEvents;
using System.Runtime.CompilerServices;
using BLTAdoptAHero.Behaviors;

namespace BLTAdoptAHero
{
    public static class AdoptedHeroFlags
    {
        public static bool _allowKingdomMove = false;
        public static bool _allowDiplomacyAction = false;
        public static bool _allowBLTArmyCreation = false;
        public static bool _allowAIjoinBLT = GlobalCommonConfig.Get().AllowAIJoinBLT;
    }

    #region FactionDiscontinuationCampaignBehavior
    [HarmonyPatch(typeof(FactionDiscontinuationCampaignBehavior))]
    internal static class FactionDiscontinuationPatches
    {
        // Skipped entirely when BLT diplomacy is switched off (Configure Window >
        // Campaign Features), so an overhaul like TAOM is left to run its own politics.
        static bool Prepare() => BLTAdoptAHeroModule.CommonConfig?.EnableDiplomacyFeatures != false;

        // 1. Define the Delegate for the private method: 
        //    It must include the instance (__instance) as the first parameter.
        private delegate void FinalizeMapEventsDelegate(FactionDiscontinuationCampaignBehavior instance, Clan clan);

        // 2. Static field to hold the callable delegate
        private static FinalizeMapEventsDelegate FinalizeMapEvents;

        // 3. Static Constructor: Runs once to initialize the delegate via Reflection.
        static FactionDiscontinuationPatches()
        {
            Type instanceType = typeof(FactionDiscontinuationCampaignBehavior);
            // Get the private instance method "FinalizeMapEvents"
            MethodInfo methodInfo = instanceType.GetMethod("FinalizeMapEvents", BindingFlags.NonPublic | BindingFlags.Instance);

            if (methodInfo != null)
            {
                // Create the delegate from the MethodInfo
                FinalizeMapEvents = (FinalizeMapEventsDelegate)Delegate.CreateDelegate(
                    typeof(FinalizeMapEventsDelegate),
                    null,
                    methodInfo
                );
            }
            // Optional: If methodInfo is null, FinalizeMapEvents remains null, 
            // which the Prefix should handle.
        }

        [HarmonyPrefix]
        [HarmonyPatch("DiscontinueClan")]
        private static bool Prefix_DiscontinueClan(Clan clan)
        {
            if ((clan?.Leader != null && clan.Leader.IsAdopted()) || clan.Name.ToString().ToLower().Contains("vassal"))
            {
                try
                {
#if DEBUG
                    Log.Trace("[BLT] Prevented DiscontinueClan for adopted leader clan");
#endif
                    return false; // skip original -> clan not destroyed
                }
                catch (Exception ex)
                {
                    Log.Error($"[BLT] Prefix_DiscontinueClan error: {ex}");
                }
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch("CanClanBeDiscontinued")]
        private static bool Prefix_CanClanBeDiscontinued(Clan clan, ref bool __result)
        {
            if ((clan?.Leader != null && clan.Leader.IsAdopted()) || clan.Name.ToString().ToLower().Contains("vassal"))
            {
                try
                {
                    __result = false;
#if DEBUG
                    Log.Trace("[BLT] CanClanBeDiscontinued -> false for adopted leader clan");
#endif
                    return false; // skip original
                }
                catch (Exception ex)
                {
                    Log.Error($"[BLT] Prefix_CanClanBeDiscontinued error: {ex}");
                }
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch("DiscontinueKingdom")]
        private static bool Prefix(Kingdom kingdom, FactionDiscontinuationCampaignBehavior __instance)
        {
            try
            {
                // Safety check: if reflection failed, log and let the original method run
                if (FinalizeMapEvents == null)
                {
                    Log.Error("[BLT] FinalizeMapEvents delegate is null. Running original method.");
                    return true;
                }

                // Re-implement the original method's logic here
                foreach (Clan clan in new List<Clan>(kingdom.Clans))
                {
                    FinalizeMapEvents(__instance, clan);
                    // YOUR CUSTOM LOGIC: Check if the clan leader is adopted
                    if (clan.Leader != null && clan.Leader.IsAdopted())
                    {
                        AdoptedHeroFlags._allowKingdomMove = true;
                        ChangeKingdomAction.ApplyByLeaveKingdom(clan);
                        AdoptedHeroFlags._allowKingdomMove = false;
#if DEBUG
                        Log.Trace("[BLT] DiscontinueKingdom success ");
#endif
                    }
                    else
                    {

                        ChangeKingdomAction.ApplyByLeaveByKingdomDestruction(clan, true);
                    }
                }

                // Re-implement the rest of the original method
                kingdom.RulingClan = null;
                DestroyKingdomAction.Apply(kingdom);

                // CRITICAL: Return false to prevent the original method from running
                return false;
            }
            catch (Exception ex)
            {
                // If anything goes wrong, log the error and run the original method to be safe
                Log.Error($"[BLT] DiscontinueKingdom Prefix error: {ex}");
                return true;
            }
            finally { AdoptedHeroFlags._allowKingdomMove = false; }
        }
    }
    #endregion

    #region KingdomActions
    [HarmonyPatch(typeof(ChangeKingdomAction))]
    internal static class ChangeKingdomActionPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("ApplyByJoinToKingdom")]
        private static bool Prefix_ApplyByJoinToKingdom(Clan clan, Kingdom newKingdom)
        {
            if (!AdoptedHeroFlags._allowKingdomMove)
            {
                if ((clan?.Leader != null && clan.Leader.IsAdopted()) || clan.Name.ToString().ToLower().Contains("vassal"))
                {
                    try
                    {
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[BLT] Prefix_ApplyByJoinToKingdom(blt)error: {ex}");
                    }
                }
            }
            if (!AdoptedHeroFlags._allowAIjoinBLT)
            {
                if (clan?.Leader != null && !clan.Leader.IsAdopted() && clan.Leader != Hero.MainHero && newKingdom.Leader.IsAdopted() && !clan.Name.ToString().ToLower().Contains("vassal"))
                {
                    try
                    {
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[BLT] Prefix_ApplyByJoinToKingdom(ai)error: {ex}");
                    }
                }
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch("ApplyByJoinToKingdomByDefection")]
        private static bool Prefix_ApplyByJoinToKingdomByDefection(Clan clan, Kingdom newKingdom)
        {
            if (!AdoptedHeroFlags._allowKingdomMove)
            {
                if ((clan?.Leader != null && clan.Leader.IsAdopted()) || clan.Name.ToString().ToLower().Contains("vassal"))
                {
                    try
                    {
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[BLT] Prefix_ApplyByJoinToKingdom(blt)error: {ex}");
                    }
                }
            }
            if (!AdoptedHeroFlags._allowAIjoinBLT)
            {
                if (clan?.Leader != null && !clan.Leader.IsAdopted() && clan.Leader != Hero.MainHero && newKingdom.Leader.IsAdopted() && !clan.Name.ToString().ToLower().Contains("vassal"))
                {
                    try
                    {
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[BLT] Prefix_ApplyByJoinToKingdom(ai)error: {ex}");
                    }
                }
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch("ApplyByLeaveKingdom")]
        private static bool Prefix_ApplyByLeaveKingdom(Clan clan)
        {
            if (!AdoptedHeroFlags._allowKingdomMove)
            {
                if ((clan?.Leader != null && clan.Leader.IsAdopted()) || clan.Name.ToString().ToLower().Contains("vassal"))
                {
                    try
                    {
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[BLT] Prefix_ApplyByLeaveKingdom error: {ex}");
                    }
                }
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch("ApplyByLeaveWithRebellionAgainstKingdom")]
        private static bool Prefix_ApplyByLeaveWithRebellionAgainstKingdom(Clan clan)
        {
            if (!AdoptedHeroFlags._allowKingdomMove)
            {
                if ((clan?.Leader != null && clan.Leader.IsAdopted()) || clan.Name.ToString().ToLower().Contains("vassal"))
                {
                    try
                    {
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[BLT] Prefix_ApplyByLeaveWithRebellionAgainstKingdom error: {ex}");
                    }
                }
            }
            return true;
        }
    }

        #region Eddy (The Angel)

    [HarmonyPatch(typeof(ChangeKingdomAction), "ApplyInternal")]
    public static class KingdomLeaveGuardPatch
    {
        private static bool _resolved;
        private static bool _bltPresent;
        private static FieldInfo? _allowKingdomMove;

        [HarmonyPrefix]
        static bool Prefix(Clan clan, ref ChangeKingdomAction.ChangeKingdomActionDetail detail)
        {
            try
            {
                RedirectLeaderlessKingdomExit(clan, ref detail);

                if (!IsAiEviction(detail)) return true;
                if (clan == null || clan == Clan.PlayerClan) return true;
                if (!IsBltClan(clan)) return true;
                if (ShouldAllow()) return true;

                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static void RedirectLeaderlessKingdomExit(
            Clan clan, ref ChangeKingdomAction.ChangeKingdomActionDetail detail)
        {
            if (detail != ChangeKingdomAction.ChangeKingdomActionDetail.LeaveKingdom) return;

            Kingdom? kingdom = clan?.Kingdom;
            if (kingdom == null || kingdom.Leader != null) return;
            if (clan!.Settlements == null || clan.Settlements.Count == 0) return;

            detail = ChangeKingdomAction.ChangeKingdomActionDetail.LeaveByKingdomDestruction;
        }

        private static bool IsAiEviction(ChangeKingdomAction.ChangeKingdomActionDetail detail)
        {
            return detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveKingdom
                || detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveWithRebellion
                || detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveAsMercenary;
        }

        private static bool IsBltClan(Clan clan)
        {
            string leader = clan.Leader?.Name?.ToString() ?? string.Empty;
            if (HeroNameTags.HasAny(leader)) return true;

            string name = clan.Name?.ToString() ?? string.Empty;
            return name.IndexOf("vassal", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ShouldAllow()
        {
            if (!_resolved) Resolve();

            if (!_bltPresent) return true;              // no BLT, nothing to guard
            if (_allowKingdomMove == null) return false; // BLT changed, stay closed
            return (bool)_allowKingdomMove.GetValue(null);
        }

        private static void Resolve()
        {
            _resolved = true;

            Type? flags = AccessTools.TypeByName("BLTAdoptAHero.AdoptedHeroFlags");
            if (flags == null)
            {
                return;
            }

            _bltPresent = true;
            _allowKingdomMove = AccessTools.Field(flags, "_allowKingdomMove");
        }
    }

        #endregion

    #endregion

    #region ClanKingdomDecisions
        // Block DeclareWarDecision for BLT kingdoms
        [HarmonyPatch(typeof(DeclareWarDecision), MethodType.Constructor, new Type[] { typeof(Clan), typeof(IFaction) })]
        internal static class DeclareWarDecisionConstructorPatch
        {
        // Skipped entirely when BLT diplomacy is switched off (Configure Window >
        // Campaign Features), so an overhaul like TAOM is left to run its own politics.
        static bool Prepare() => BLTAdoptAHeroModule.CommonConfig?.EnableDiplomacyFeatures != false;

            [HarmonyPrefix]
            private static bool Prefix(Clan proposerClan)
            {
                if (proposerClan?.Kingdom?.Leader != null && proposerClan.Kingdom.Leader.IsAdopted() && Hero.MainHero?.Clan != proposerClan)
                {
#if DEBUG
                    Log.Trace($"[BLT] Blocked DeclareWarDecision for BLT kingdom: {proposerClan.Kingdom.Name}");
#endif
                    return false; // Block decision creation
                }
                return true;
            }
        }

        //        // Block KingdomPolicyDecision for BLT kingdoms (optional - you might want to keep this)
        //        [HarmonyPatch(typeof(KingdomPolicyDecision), MethodType.Constructor, new Type[] { typeof(Clan), typeof(PolicyObject), typeof(bool) })]
        //        internal static class KingdomPolicyDecisionConstructorPatch
        //        {
        //            [HarmonyPrefix]
        //            private static bool Prefix(Clan proposerClan)
        //            {
        //                if (proposerClan?.Kingdom?.Leader != null && proposerClan.Kingdom.Leader.IsAdopted())
        //                {
        //#if DEBUG
        //                Log.Trace($"[BLT] Blocked KingdomPolicyDecision for BLT kingdom: {proposerClan.Kingdom.Name}");
        //#endif
        //                    return false; // Block decision creation
        //                }
        //                return true;
        //            }
        //        }

        // Block ExpelClanFromKingdomDecision for BLT kingdoms
        [HarmonyPatch(typeof(ExpelClanFromKingdomDecision), MethodType.Constructor, new Type[] { typeof(Clan), typeof(Clan) })]
        internal static class ExpelClanFromKingdomDecisionConstructorPatch
        {
        // Skipped entirely when BLT diplomacy is switched off (Configure Window >
        // Campaign Features), so an overhaul like TAOM is left to run its own politics.
        static bool Prepare() => BLTAdoptAHeroModule.CommonConfig?.EnableDiplomacyFeatures != false;

            [HarmonyPrefix]
            private static bool Prefix(Clan proposerClan)
            {
                if (proposerClan?.Kingdom?.Leader != null && proposerClan.Kingdom.Leader.IsAdopted() && Hero.MainHero?.Clan != proposerClan)
                {
#if DEBUG
                    Log.Trace($"[BLT] Blocked ExpelClanFromKingdomDecision for BLT kingdom: {proposerClan.Kingdom.Name}");
#endif
                    return false; // Block decision creation
                }
                return true;
            }
        }

    //        // Block SettlementClaimantDecision for BLT kingdoms (fief distribution)
    //        [HarmonyPatch(typeof(SettlementClaimantDecision), MethodType.Constructor, new Type[] { typeof(Clan), typeof(Settlement) })]
    //        internal static class SettlementClaimantDecisionConstructorPatch
    //        {
    //            [HarmonyPrefix]
    //            private static bool Prefix(Clan proposerClan)
    //            {
    //                if (proposerClan?.Kingdom?.Leader != null && proposerClan.Kingdom.Leader.IsAdopted())
    //                {
    //#if DEBUG
    //                Log.Trace($"[BLT] Blocked SettlementClaimantDecision for BLT kingdom: {proposerClan.Kingdom.Name}");
    //#endif
    //                    return false; // Block decision creation
    //                }
    //                return true;
    //            }
    //        }

    //        // Block AnnexationDecision for BLT kingdoms
    //        [HarmonyPatch(typeof(KingdomDecision), "DetermineChooser")]
    //        internal static class DetermineChooserPatch
    //        {
    //            [HarmonyPrefix]
    //            private static bool Prefix(KingdomDecision __instance, ref Clan __result)
    //            {
    //                if (__instance?.Kingdom?.Leader != null && __instance.Kingdom.Leader.IsAdopted())
    //                {
    //                    // For BLT kingdoms, always return null to prevent AI from choosing
    //                    __result = null;
    //#if DEBUG
    //                Log.Trace($"[BLT] Blocked DetermineChooser for BLT kingdom: {__instance.Kingdom.Name}");
    //#endif
    //                    return false;
    //                }
    //                return true;
    //            }
    //        }
    //    }
#endregion

    #region DiplomacyProposalPatches

    //    // Additional safety - block at the proposal level
    //    [HarmonyPatch(typeof(KingdomDiplomacyVM))]
    //    internal static class KingdomDiplomacyVMPatches
    //    {
    //        // This blocks the UI from even showing diplomacy options for BLT kingdoms
    //        [HarmonyPatch("CanProposeAction")]
    //        [HarmonyPrefix]
    //        private static bool Prefix_CanProposeAction(ref bool __result, Kingdom ____playerKingdom)
    //        {
    //            if (____playerKingdom?.Leader != null && ____playerKingdom.Leader.IsAdopted())
    //            {
    //                __result = false;
    // #if DEBUG
    //            Log.Trace($"[BLT] Blocked CanProposeAction in KingdomDiplomacyVM for BLT kingdom");
    //#endif
    //                return false;
    //            }
    //            return true;
    //        }
    //    }

    #endregion

    #region KingdomDecisionProposalBehaviorPatches

    // Block the behavior that creates kingdom decisions
    [HarmonyPatch(typeof(KingdomDecisionProposalBehavior))]
        internal static class KingdomDecisionProposalBehaviorPatches
        {
        // Skipped entirely when BLT diplomacy is switched off (Configure Window >
        // Campaign Features), so an overhaul like TAOM is left to run its own politics.
        static bool Prepare() => BLTAdoptAHeroModule.CommonConfig?.EnableDiplomacyFeatures != false;

            [HarmonyPrefix]
            [HarmonyPatch("ConsiderWar")]
            private static bool Prefix_ConsiderWar(Clan clan)
            {
                if (clan?.Kingdom?.Leader != null && clan.Kingdom.Leader.IsAdopted())
                {
#if DEBUG
                    Log.Trace($"[BLT] Blocked ConsiderWar for BLT kingdom: {clan.Kingdom.Name}");
#endif
                    return false;
                }
                return true;
            }
        }
    #endregion

    #region ClanPatches
    [HarmonyPatch(typeof(Clan))]
    internal static class ClanPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("UpdateBannerColorsAccordingToKingdom")]
        private static bool Prefix_UpdateBannerColorsAccordingToKingdom(Clan __instance)
        {
            if (__instance?.Leader != null && __instance.Leader.IsAdopted())
            {
                try
                {
#if DEBUG
            Log.Trace("[BLT] Blocked UpdateBannerColorsAccordingToKingdom for adopted clan");
#endif
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Error($"[BLT] Prefix_UpdateBannerColorsAccordingToKingdom error: {ex}");
                }
            }
            return true;
        }
    }
    [HarmonyPatch(typeof(DefaultMarriageModel), nameof(DefaultMarriageModel.GetClanAfterMarriage))]
    internal class BLTAfterMarriage
    {
        static void Postfix(DefaultMarriageModel __instance, ref Clan __result, Hero firstHero, Hero secondHero)
        {
            if (firstHero.Clan?.Leader == firstHero || secondHero.Clan?.Leader == secondHero)
                return;

            if (firstHero.IsAdopted() == true || secondHero.IsAdopted() == true)
                return;

            if (firstHero.Clan?.Leader.IsAdopted() == false && secondHero.Clan?.Leader.IsAdopted() == false)
                return;

            if (firstHero.Clan?.Leader.IsAdopted() == true && secondHero.Clan?.Leader.IsAdopted() == true)
                return;

            if (firstHero.Clan.Leader.IsAdopted())
            {
                __result = firstHero.Clan;
            }
            else { __result = secondHero.Clan; }
#if DEBUG
            Log.Trace($"[BLT] Changed marriage clan for {firstHero.FirstName}/{secondHero.FirstName} to {__result.Name}");
#endif
        }
    }
    [HarmonyPatch(typeof(KillCharacterAction), nameof(KillCharacterAction.ApplyInLabor))]
    internal class BLTNoPregnancyDeath_Action
    {
        static bool Prefix(Hero lostMother, bool showNotification)
        {
            if (lostMother.IsAdopted())
            {
#if DEBUG
                Log.Trace($"[BLT] Prevented childbirth death for {lostMother?.Name}");
#endif
                return false;
            }
        return true;
        }

    }

    [HarmonyPatch(typeof(DefaultMarriageModel), nameof(DefaultMarriageModel.IsSuitableForMarriage))]
    internal class BLTMarriageBlock
    {
        static void Postfix(ref bool __result, Hero maidenOrSuitor)
        {
            if (maidenOrSuitor == null) return;
            if (maidenOrSuitor.IsAdopted())
            {
                __result = false;
#if DEBUG
                Log.Trace($"[BLT] Overwrote marriage for adopted hero");
#endif
                return;
            }

            var heirs = Campaign.Current.GetCampaignBehavior<BLTHeirBehavior>()?._heirs;
            if (heirs != null && heirs.Contains(maidenOrSuitor))
            {
#if DEBUG
                Log.Trace($"[BLT] Overwrote marriage for heir");
#endif
                __result = false;
            }
        }
    }

    #endregion

    #region DEATH

    [HarmonyPatch(typeof(KillCharacterAction), "ApplyInternal")]
    internal class BLTNoDeathAllowed
    {
        static bool Prefix(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail actionDetail, bool showNotification, bool isForced = false)
        {           
            if (isForced) return true;
            if (!victim.IsAdopted()) return true;
            if (killer == Hero.MainHero && actionDetail == KillCharacterAction.KillCharacterActionDetail.Executed) return true;
            var config = GlobalCommonConfig.Get();
            if (!config.AllowDeath) return false;
            if (victim.Age > config.MinimumAge) return true;

            return false;
        }
    }


    #endregion

    #region TownFoodStocks

    [HarmonyPatch(nameof(DefaultSettlementFoodModel), "FoodStocksUpperLimit")]
        [HarmonyPatch(MethodType.Getter)]
        internal static class FoodStocksUpperLimitUncap
        {
            [HarmonyPrefix]
            public static bool FoodStocksUpperLimitPrefix(ref int __result)
            {
                __result = BLTAdoptAHeroModule.CommonConfig.UncapFoodStocks ? 10000 : 300;
                return false; // Skip original method
            }
        }

        [HarmonyPatch(typeof(Village), "GetHearthLevel")]
        public class HearthExpansionPatch
        {
            [HarmonyPrefix]
            public static bool GetHearthLevelPrefix(Village __instance, ref int __result)
            {
                if (__instance.Hearth >= BLTAdoptAHeroModule.CommonConfig.HearthPerVillageTier)
                {
                    __result = (int)(__instance.Hearth / BLTAdoptAHeroModule.CommonConfig.HearthPerVillageTier);
                }
                else
                {
                    __result = 0;
                }

                // Return false to prevent the original method from running
                return false;
            }
        }
    #endregion

    #region DiplomacyPatches

    [HarmonyPatch]
    public class BLTDiplomacyPatches
    {
        // ── helpers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true when <paramref name="faction"/> is — or contains — at least one
        /// clan whose leader is an adopted hero.  Works for both Kingdom and Clan factions.
        /// </summary>
        private static bool FactionHasAdoptedClan(IFaction faction)
        {
            if (faction == null) return false;

            // Faction IS a clan (e.g. minor factions, rebel clans)
            if (faction is Clan clan)
                return clan.Leader != null && clan.Leader.IsAdopted();

            // Faction is a kingdom — check every member clan
            if (faction is Kingdom kingdom)
                return kingdom.Clans.Any(c => c?.Leader != null && c.Leader.IsAdopted());

            return false;
        }

        /// <summary>
        /// Logs a feed response for every adopted leader found inside <paramref name="faction"/>.
        /// </summary>
        private static void NotifyAdoptedLeaders(IFaction faction, IFaction other, string reason)
        {
            IEnumerable<Hero> adopted = faction is Kingdom k
                ? k.Clans.Where(c => c?.Leader != null && c.Leader.IsAdopted()).Select(c => c.Leader)
                : faction is Clan c2 && c2.Leader != null && c2.Leader.IsAdopted()
                    ? new[] { c2.Leader }
                    : Enumerable.Empty<Hero>();

            foreach (Hero h in adopted)
            {
                string n = h.FirstName.ToString()
                    .Replace(BLTAdoptAHeroModule.Tag, "")
                    .Replace(BLTAdoptAHeroModule.DevTag, "").Replace(BLTAdoptAHeroModule.StreamerTag, "").Replace(BLTAdoptAHeroModule.ModTag, "").Replace(BLTAdoptAHeroModule.VipTag, "").Replace(BLTAdoptAHeroModule.SubTag, "")
                    .Trim();
                Log.LogFeedResponse($"@{n} Peace with {other.Name} rejected – {reason}");
            }
        }

        // ── main patch ───────────────────────────────────────────────────────────

        /// <summary>
        /// Intercepts MakePeaceAction.ApplyInternal BEFORE any siege/stance teardown occurs.
        /// Blocks peace whenever either faction contains any clan with an adopted leader,
        /// unless BLT itself sanctioned the action (<see cref="AdoptedHeroFlags._allowDiplomacyAction"/>).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MakePeaceAction), "ApplyInternal")]
        public static bool Prefix_MakePeaceAction_Apply(
            IFaction faction1,
            IFaction faction2,
            int dailyTributeFrom1To2,
            int dailyTributeDuration,
            MakePeaceAction.MakePeaceDetail detail = MakePeaceAction.MakePeaceDetail.Default)
        {
            // Always allow peace that BLT itself initiated
            if (AdoptedHeroFlags._allowDiplomacyAction)
                return true;

            // We only meddle when at least one side involves an adopted clan
            bool f1HasAdopted = FactionHasAdoptedClan(faction1);
            bool f2HasAdopted = FactionHasAdoptedClan(faction2);

            if (!f1HasAdopted && !f2HasAdopted)
                return true; // pure AI-vs-AI with no adopted clans: let it through

            // Exclude the player's own kingdom from BLT restrictions
            Kingdom playerKingdom = Hero.MainHero?.Clan?.Kingdom;
            bool f1IsBLT = f1HasAdopted && faction1 != playerKingdom;
            bool f2IsBLT = f2HasAdopted && faction2 != playerKingdom;

            if (!f1IsBLT && !f2IsBLT)
                return true; // adopted clans are all inside the player's own kingdom

            if (BLTTreatyManager.Current == null)
                return true;

            // ── Case 1: minimum war duration not yet met ─────────────────────────
            var k1 = faction1 as Kingdom;
            var k2 = faction2 as Kingdom;

            if (k1 != null && k2 != null &&
                !BLTTreatyManager.Current.CanMakePeace(k1, k2, out string reason))
            {
#if DEBUG
                Log.Trace($"[BLT-Harmony] Blocked peace (min duration): {faction1.Name} <-> {faction2.Name} – {reason}");
#endif
                if (f1IsBLT) NotifyAdoptedLeaders(faction1, faction2, reason);
                if (f2IsBLT) NotifyAdoptedLeaders(faction2, faction1, reason);

                Log.ShowInformation($"Peace rejected – {reason}",
                    (faction1 as Kingdom)?.Leader?.CharacterObject
                    ?? (faction1 as Clan)?.Leader?.CharacterObject);

                return false; // blocked — war never ended, no re-declare needed
            }

            // ── Case 2: AI trying to make peace with a BLT-side faction ─────────
            if (f1IsBLT != f2IsBLT)
            {
                IFaction aiFaction = f1IsBLT ? faction2 : faction1;
                IFaction bltFaction = f1IsBLT ? faction1 : faction2;
#if DEBUG
                Log.Trace($"[BLT-Harmony] Blocked AI->BLT peace: {aiFaction.Name} -> {bltFaction.Name}. Creating proposal.");
#endif
                // Only queue a visible proposal when both sides are kingdoms
                if (aiFaction is Kingdom aiKingdom && bltFaction is Kingdom bltKingdom)
                    BLTDiplomacyBehavior.Current?.HandleAIPeaceAttempt(aiKingdom, bltKingdom);

                return false; // blocked — siege state untouched
            }

            // ── Case 3: Both BLT sides without _allowDiplomacyAction ─────────────
#if DEBUG
            Log.Trace($"[BLT-Harmony] Blocked unsanctioned BLT-BLT peace: {faction1.Name} <-> {faction2.Name}");
#endif
            return false;
        }
    }

    #endregion

    #region ArmyDispersionAndCohesionPatches

    [HarmonyPatch(typeof(Army), "CheckArmyDispersion")]
    internal static class BLT_ArmyDispersionPatch
    {
        private static readonly Dictionary<Army, CampaignTime> ArmyCreationTimes = new();

        static bool Prefix(Army __instance)
        {
            try
            {
                if (__instance?.LeaderParty?.LeaderHero == null)
                    return true;

                // Mercenary armies: MercenaryArmyPatches owns those — skip here
                // (MercenaryArmyPatches.Prefix_CheckArmyDispersion already blocks them)
                //if (MercenaryArmyPatches.IsMercenaryArmy(__instance))
                //    return true;

                if (__instance.LeaderParty == MobileParty.MainParty)
                    return true;

                // Only process armies led by adopted heroes
                if (!__instance.LeaderParty.LeaderHero.IsAdopted())
                    return true;

                // Quick cleanup of stale tracking entry
                if (__instance.LeaderParty?.Army != __instance)
                {
                    ArmyCreationTimes.Remove(__instance);
                    return true;
                }

                // Track creation time
                if (!ArmyCreationTimes.ContainsKey(__instance))
                    ArmyCreationTimes[__instance] = CampaignTime.Now;

                float daysAlive =
                    (float)(CampaignTime.Now.ToDays - ArmyCreationTimes[__instance].ToDays);

                // If no active wars with real factions, allow normal disbanding
                var kingdom = __instance.LeaderParty.MapFaction as Kingdom;
                if (kingdom == null
                    || !kingdom.FactionsAtWarWith.Any(f =>
                        f.IsKingdomFaction || (f.IsClan && f.Fiefs.Any())))
                {
                    ArmyCreationTimes.Remove(__instance);
                    return true;
                }

                // Still within minimum lifetime — block dispersion
                if (daysAlive < BLTAdoptAHeroModule.CommonConfig.BLTArmyMinLifetimeDays)
                {
#if DEBUG
                Log.Trace($"[BLT] Blocked dispersion (age {daysAlive:F1}d) for {__instance.LeaderParty.LeaderHero.Name}'s army");
#endif
                    return false;
                }

                // Beyond minimum lifetime but LockBLTArmyCohesion enabled:
                // block dispersion that would have been caused by cohesion only
                // (peace/no-war path already returned above; this blocks the
                //  CohesionDepleted path while leaving LeaderDead etc. through)
                if (BLTAdoptAHeroModule.CommonConfig.LockBLTArmyCohesion
                    && __instance.Cohesion >= 100f)
                {
                    return false; // cohesion can't actually be the problem; skip
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[BLT] BLT_ArmyDispersionPatch error: {ex}");
                return true;
            }
        }
    }

    /// <summary>
    /// Clamps cohesion to 100 for player BLT armies when LockPlayerArmyCohesion is on.
    /// Mercenary army cohesion is handled separately in MercenaryArmyPatches.
    /// </summary>
    //[HarmonyPatch(typeof(Army), nameof(Army.Cohesion), MethodType.Setter)]
    //internal static class BLT_ArmyCohesionSetterPatch
    //{
    //    static void Postfix(Army __instance)
    //    {
    //        try
    //        {
    //            // Mercenary armies handled in MercenaryArmyPatches — skip
    //            //if (MercenaryArmyPatches.IsMercenaryArmy(__instance)) return;
    //            if (__instance.LeaderParty == MobileParty.MainParty) return;
    //
    //            if (!BLTAdoptAHeroModule.CommonConfig.LockPlayerArmyCohesion) return;
    //
    //            if (__instance.LeaderParty?.LeaderHero?.IsAdopted() == true
    //                && __instance.Cohesion < 100f)
    //            {
    //                __instance.Cohesion = 100f;
    //            }
    //        }
    //        catch (Exception ex)
    //        {
    //            Log.Error($"[BLT] BLT_ArmyCohesionSetterPatch error: {ex}");
    //        }
    //    }
    //}
    #endregion

    #region MilitiaSallyOut
    [HarmonyPatch(typeof(Town), "GetDefenderParties")]
    class Town_GetDefenderParties_Patch
    {
        static bool Prefix(Town __instance, MapEvent.BattleTypes battleType, ref IEnumerable<PartyBase> __result)
        {
            __result = GetDefenderPartiesWithMilitia(__instance, battleType);
            return false; // Skip original method
        }

        static IEnumerable<PartyBase> GetDefenderPartiesWithMilitia(Town town, MapEvent.BattleTypes battleType)
        {
            yield return town.Settlement.Party;

            foreach (MobileParty mobileParty in town.Settlement.Parties)
            {
                if (mobileParty.MapFaction.IsAtWarWith(town.Settlement.SiegeEvent.BesiegerCamp.MapFaction)
                    && mobileParty.IsActive
                    && !mobileParty.IsVillager
                    && !mobileParty.IsCaravan
                    && (!mobileParty.IsMilitia || !town.InRebelliousState)) // FIXED: Militia now included in SallyOut
                {
                    yield return mobileParty.Party;
                }
            }
        }
    }
    #endregion

    #region SiegeRetreatFix

    /// <summary>
    /// Fixes the vanilla bug where retreating from a siege assault causes the ENTIRE
    /// besieging army to be captured/killed, and lords made fugitive respawning with 1 troop.
    /// This version safely tracks mutated MapEvent instances instead of using ThreadStatic.
    /// </summary>

    [HarmonyPatch(typeof(MapEvent), "CalculateAndCommitMapEventResults")]
    internal static class BLT_SiegeRetreatFix
    {
        private static readonly PropertyInfo RetreatingSideProp =
            typeof(MapEvent).GetProperty("RetreatingSide",
                BindingFlags.Public | BindingFlags.Instance);

        // Tracks MapEvent instances we mutate so we can safely restore them.
        private static readonly HashSet<MapEvent> _mutated = new();

        private static bool IsSiegeRelated(MapEvent e) =>
            e.IsSiegeAssault || e.IsSallyOut || e.IsSiegeOutside;

        static void Prefix(MapEvent __instance)
        {
            try
            {
                if (!IsSiegeRelated(__instance))
                    return;

                if (!__instance.HasWinner)
                    return;

                if (__instance.RetreatingSide != BattleSideEnum.None)
                    return;

                var defeatedSide = __instance.GetMapEventSide(__instance.DefeatedSide);
                if (defeatedSide == null)
                    return;

                int survivors = defeatedSide.GetTotalHealthyTroopCountOfSide();
                if (survivors <= 0)
                    return; // Truly wiped out — allow vanilla full capture

                RetreatingSideProp?.SetValue(__instance, __instance.DefeatedSide);
                _mutated.Add(__instance);

#if DEBUG
            Log.Trace($"[BLT] SiegeRetreatFix: {survivors} survivors on " +
                      $"{__instance.DefeatedSide} side — temporarily suppressing troop capture.");
#endif
            }
            catch (Exception ex)
            {
                Log.Error($"[BLT] BLT_SiegeRetreatFix Prefix error: {ex}");
            }
        }

        static void Postfix(MapEvent __instance)
        {
            try
            {
                if (!_mutated.Remove(__instance))
                    return;

                // Restore original state so later systems see correct battle result
                RetreatingSideProp?.SetValue(__instance, BattleSideEnum.None);

#if DEBUG
            Log.Trace("[BLT] SiegeRetreatFix: RetreatingSide restored to None.");
#endif
            }
            catch (Exception ex)
            {
                Log.Error($"[BLT] BLT_SiegeRetreatFix Postfix error: {ex}");
            }
        }
    }


    // -------------------------------------------------------------------------
    // Part 2: Safety net — prevent lords from being made fugitive if they belong
    //         to a party actively part of a siege besieger camp.
    // -------------------------------------------------------------------------

    [HarmonyPatch(typeof(MakeHeroFugitiveAction), nameof(MakeHeroFugitiveAction.Apply))]
    internal static class BLT_SiegeLordFugitiveFix
    {
        static bool Prefix(Hero fugitive, bool showNotification)
        {
            try
            {
                MobileParty party = fugitive.PartyBelongedTo;
                if (party == null)
                    return true;

                var mapEvent = party.MapEvent ?? party.Army?.LeaderParty?.MapEvent;

                // Ignore naval blockades
                if (mapEvent != null && (mapEvent.IsBlockade || mapEvent.IsBlockadeSallyOut))
                    return true;

                bool isInSiegingParty =
                    party.BesiegedSettlement != null ||
                    (party.Army != null &&
                     party.Army.LeaderParty?.BesiegedSettlement != null);

                if (!isInSiegingParty)
                    return true;

                // Only suppress if the party still has healthy troops
                if (party.Party?.NumberOfHealthyMembers <= 0)
                    return true;

#if DEBUG
            Log.Trace($"[BLT] SiegeLordFugitiveFix: Blocked MakeHeroFugitive for " +
                      $"{fugitive.Name} (besieging " +
                      $"{party.BesiegedSettlement?.Name ?? party.Army?.LeaderParty?.BesiegedSettlement?.Name})");
#endif

                return false; // Prevent fugitive conversion
            }
            catch (Exception ex)
            {
                Log.Error($"[BLT] BLT_SiegeLordFugitiveFix error: {ex}");
                return true;
            }
        }
    }

    #endregion

    #region BlockArmies
    [HarmonyPatch(typeof(Kingdom), "CreateArmy")]
    internal static class BLT_BlockAIArmyCreation
    {
        [HarmonyPrefix]
        private static bool Prefix(Kingdom __instance, Hero armyLeader)
        {
            try
            {
                // Always allow the player
                if (armyLeader == Hero.MainHero)
                    return true;

                var pb = PartyOrderBehavior.Current;

                if (armyLeader?.IsAdopted() == true)
                {
                    // Block all BLT army creation unless a BLT command explicitly allowed it
                    if (!AdoptedHeroFlags._allowBLTArmyCreation)
                    {
#if DEBUG
                    Log.Trace($"[BLT] Blocked unsanctioned BLT army creation by {armyLeader?.Name} in {__instance?.Name}");
#endif
                        return false;
                    }
                    // Sanctioned creation: still respect the per-kingdom block flag
                    return pb == null || !pb.IsBLTArmiesBlocked(__instance);
                }

                // AI hero
                if (pb == null) return true;
                return !pb.IsAIArmiesBlocked(__instance);
            }
            catch (Exception ex)
            {
                Log.Error($"[BLT] BLT_BlockAIArmyCreation Prefix error: {ex}");
                return true; // fail-safe
            }
        }
    }
    #endregion

    #region ClanArmyPatches

    /// <summary>
    /// Patches Army.FindBestGatheringSettlementAndMoveTheLeader for clan armies
    /// (Army.Kingdom == null).  The vanilla method unconditionally iterates
    /// this.Kingdom.Settlements, which would throw a NullReferenceException.
    ///
    /// When Kingdom is non-null the prefix returns true and vanilla runs unchanged.
    /// When Kingdom is null (clan army) the prefix:
    ///   • Calls BLTClanArmyBehavior.FindClanGatherSettlement to pick a friendly settlement.
    ///   • Sets AiBehaviorObject to that settlement.
    ///   • Moves the leader party toward it (replicating SendLeaderPartyToReachablePointAroundPosition).
    ///   • Returns false to skip the vanilla body entirely.
    /// </summary>
    [HarmonyPatch(typeof(Army), "FindBestGatheringSettlementAndMoveTheLeader")]
    internal static class BLT_ClanArmyFindGatheringPatch
    {
        [HarmonyPrefix]
        static bool Prefix(Army __instance, Settlement focusSettlement)
        {
            try
            {
                if (__instance.Kingdom != null) return true;

                var gather = BLTClanArmyBehavior.FindClanGatherSettlement(__instance)
                             ?? focusSettlement;

                if (gather == null)
                {
                    __instance.LeaderParty.SetMoveModeHold();
                    return false;
                }

                __instance.AiBehaviorObject = gather;

                __instance.LeaderParty.SetMoveGoToPoint(
                    NavigationHelper.FindReachablePointAroundPosition(
                        gather.GatePosition,
                        MobileParty.NavigationType.Default,
                        __instance.GatheringPositionMaxDistanceToTheSettlement,
                        __instance.GatheringPositionMinDistanceToTheSettlement,
                        false),
                    __instance.LeaderParty.NavigationCapability);

                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"[BLT] BLT_ClanArmyFindGatheringPatch error: {ex}");
                return true;
            }
        }
    }

    #endregion

    #region ClanSiegePatches

    [HarmonyPatch(typeof(AiPartyThinkBehavior), "PartyHourlyAiTick")]
    internal static class BLT_PartyHourlyAiTickPatch
    {
        [HarmonyPrefix]
        static bool Prefix(MobileParty mobileParty)
        {
            try
            {
                // If this party has an active siege order, skip the vanilla AI tick entirely
                // — our PartyOrderBehavior.OnHourlyTickParty handles it instead
                var order = PartyOrderBehavior.Current?.GetActiveOrder(mobileParty.StringId);
                if (order?.Type != PartyOrderType.Siege) return true;
                if (mobileParty.MapFaction.IsKingdomFaction) return true;
                if (mobileParty.MapFaction == Clan.PlayerClan.MapFaction) return true;

                //Log.Trace("AiPartyTick");
                return false; // Skip vanilla tick — prevent Hold/disband interference
            }
            catch (Exception ex)
            {
                Log.Error($"[BLT] BLT_PartyHourlyAiTickPatch error: {ex}");
                return true;
            }
        }
    }

    #endregion

    #region Eddy

        #region Broken Governer Patches

    [HarmonyPatch(typeof(GovernorCampaignBehavior), "DailyTickSettlement")]
    public static class GovernorDailyTickSettlementPatch
    {
        [HarmonyFinalizer]
        static Exception? Finalizer(Exception? __exception, Settlement settlement)
        {
            if (__exception is NullReferenceException)
            {
                return null;
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(BuildingsCampaignBehavior), "DailyTickSettlement")]
    public static class BuildingsDailyTickSettlementPatch
    {
        [HarmonyFinalizer]
        static Exception? Finalizer(Exception? __exception, Settlement settlement)
        {
            if (__exception is NullReferenceException)
            {
                return null;
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(SettlementClaimantCampaignBehavior), "DailyTickSettlement")]
    public static class SettlementClaimantDailyTickSettlementPatch
    {
        [HarmonyFinalizer]
        static Exception? Finalizer(Exception? __exception, Settlement settlement)
        {
            if (__exception is NullReferenceException)
            {
                return null;
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(GovernorCampaignBehavior), "DailyTickSettlement")]
    public static class GovernorRepairPatch
    {
        [HarmonyPrefix]
        static void Prefix(Settlement settlement)
        {
            try
            {
                Town town = settlement?.Town;
                Hero governor = town?.Governor;
                if (governor == null) return;

                if (governor.Clan == null)
                {
                    ChangeGovernorAction.RemoveGovernorOfIfExists(town);
                }
            }
            catch (Exception ex) { }
        }
    }

    #endregion

        #region More Naval Fixes holy shit how broken is this DLC

    [PatchAfterModulesLoaded]
    [HarmonyPatch]
    public static class NavalPatrolRadiusPatch
    {
        private const string ModelType = "NavalDLC.GameComponents.NavalDLCMobilePartyAIModel";

        private static readonly DefaultMobilePartyAIModel StockModel = new DefaultMobilePartyAIModel();

        // Skip this whole patch class cleanly when NavalDLC isn't loaded (e.g. TAOM doesn't
        // depend on NavalDLC at all) - without this, Harmony's PatchAll() throws
        // "Undefined target method" the moment TargetMethods() yields nothing, which took
        // down all of BLTAdoptAHeroModule.OnBeforeInitialModuleScreenSetAsRoot().
        static bool Prepare() => AccessTools.TypeByName(ModelType) != null;

        static IEnumerable<MethodBase> TargetMethods()
        {
            Type? type = AccessTools.TypeByName(ModelType);
            MethodInfo? method = type != null ? AccessTools.Method(type, "GetPatrolRadius") : null;
            if (method != null) yield return method;
        }

        static bool Prefix(MobileParty mobileParty, CampaignVec2 patrolPoint, ref float __result)
        {
            try
            {
                if (patrolPoint.IsOnLand || !patrolPoint.IsValid()) return true;

                if (mobileParty == null) return true;
                if (mobileParty.IsBandit || !mobileParty.IsLordParty) return true;
                if (!mobileParty.IsCurrentlyAtSea) return true;
                if (mobileParty.TargetSettlement != null) return true;

                __result = StockModel.GetPatrolRadius(mobileParty, patrolPoint);
                return false;
            }
            catch (Exception ex)
            {
                __result = 0f;
                return false;
            }
        }
    }

        #endregion

        #region NullSettlement Patch

    [HarmonyPatch]
    public static class NullSettlementAiActionPatch
    {
        // Every SetPartyAiAction entry point whose Settlement argument reaches the
        // TargetSettlement.Party dereference in RecalculateShortTermBehavior.
        private static readonly string[] Actions =
        {
            "GetActionForDefendingSettlement",
            "GetActionForRaidingSettlement",
            "GetActionForBesiegingSettlement",
            "GetActionForVisitingSettlement",
            "GetActionForPatrollingAroundSettlement",
        };

        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in Actions)
            {
                MethodInfo? m = AccessTools.Method(typeof(SetPartyAiAction), name);
                if (m != null) yield return m;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(MobileParty owner, Settlement settlement, MethodBase __originalMethod)
        {
            if (settlement != null) return true;

            string where = __originalMethod?.Name ?? "unknown";
            return false;
        }
    }

    #endregion

        #region Child Equipment on Birth Patch

    // The original patch requires the NavalDLC, so I'll have to convert this one to use
    // equipmentflags instead of templates, as those are a NavalDLC specific feature.

        #endregion

        #region Raiding Null Patch

    // Was a fixed-signature [HarmonyPatch] targeting a specific 4-arg overload of
    // GetActionForRaidingSettlement. That overload's exact parameter list isn't guaranteed
    // to match across game versions, and Harmony throws "Undefined target method" (taking
    // down all of BLTAdoptAHeroModule.OnBeforeInitialModuleScreenSetAsRoot) if it doesn't
    // find an exact match. Resolve every overload by name instead and patch whichever exist.
    [HarmonyPatch]
    public static class RaidNullSettlementPatch
    {
        static bool Prepare() =>
            AccessTools.GetDeclaredMethods(typeof(SetPartyAiAction))
                .Any(m => m.Name == nameof(SetPartyAiAction.GetActionForRaidingSettlement));

        static IEnumerable<MethodBase> TargetMethods() =>
            AccessTools.GetDeclaredMethods(typeof(SetPartyAiAction))
                .Where(m => m.Name == nameof(SetPartyAiAction.GetActionForRaidingSettlement));

        [HarmonyPrefix]
        static bool Prefix(Settlement settlement)
        {
            if (settlement != null) return true;

            return false; // skip the action; the party keeps its current behaviour
        }
    }

        #endregion

    // Marks a [HarmonyPatch] class whose target lives in ANOTHER mod's assembly
    // rather than in stock TaleWorlds code. Those assemblies are not guaranteed
    // to be loaded yet at OnSubModuleLoad (load order decides), so AccessTools
    // would just fail to resolve the type. SubModule holds these back to
    // OnBeforeInitialModuleScreenSetAsRoot, by which point every module's
    // assembly is in the AppDomain.
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class PatchAfterModulesLoadedAttribute : Attribute { }

    #endregion
}