using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BannerlordTwitch.Util;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Keeps caravans moving.
    ///
    /// Vanilla picks a caravan's next stop in CaravansCampaignBehavior: every candidate town is
    /// filtered through CanTradeWith, and if nothing survives that filter the destination search
    /// returns null. There is no fallback - the caravan simply stays where it is, forever. On a
    /// map where most factions are permanently at war with their neighbours that happens a lot,
    /// and towns slowly fill up with caravans that will never leave again.
    ///
    /// Two independent, opt-in remedies live here:
    ///   * a watchdog that spots a caravan parked far too long and forces a fresh decision, and
    ///   * a fallback destination supplied only when the game's own search already came up empty.
    ///
    /// The watchdog fixes the symptom whatever the cause; the fallback fixes this specific cause.
    /// </summary>
    public class BLTCaravanBehavior : CampaignBehaviorBase
    {
        public static BLTCaravanBehavior Current { get; private set; }

        public BLTCaravanBehavior() { Current = this; }

        // Deliberately not serialised: how long a caravan has been parked is cheap to observe
        // again after a load, and stale counters across a save would only cause false positives.
        private readonly Dictionary<MobileParty, ParkedState> parked = new();
        private readonly Dictionary<MobileParty, ParkedState> stalled = new();

        /// <summary>
        /// How close a party has to be to a settlement to count as standing on it rather than
        /// travelling towards it.
        /// </summary>
        private const float AtSettlementDistance = 1.5f;

        private class ParkedState
        {
            public Settlement Settlement;
            public int Hours;
            public bool Nudged;
        }

        public override void RegisterEvents()
        {
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
        }

        public override void SyncData(IDataStore dataStore) { }

        private void OnHourlyTick()
        {
            var cfg = BLTAdoptAHeroModule.CommonConfig;
            if (cfg == null) return;

            if (cfg.UnstickCaravans) TickCaravans(cfg);
            if (cfg.UnstickLordParties) TickLordParties(cfg);
        }

        private void TickCaravans(GlobalCommonConfig cfg)
        {
            int threshold = Math.Max(4, cfg.CaravanStuckHours);

            try
            {
                foreach (var party in MobileParty.All.ToList())
                {
                    if (party == null || !party.IsCaravan || !party.IsActive) continue;

                    var settlement = party.CurrentSettlement;
                    if (settlement == null)
                    {
                        // On the road, which is exactly where we want it.
                        parked.Remove(party);
                        continue;
                    }

                    if (!parked.TryGetValue(party, out var state) || state.Settlement != settlement)
                    {
                        parked[party] = new ParkedState { Settlement = settlement, Hours = 1 };
                        continue;
                    }

                    state.Hours++;

                    // First escalation: hand the decision back to the game with everything that
                    // could be suppressing it cleared. Most stuck caravans start moving here.
                    if (state.Hours >= threshold && !state.Nudged)
                    {
                        state.Nudged = true;
                        Nudge(party);
                        continue;
                    }

                    // Second escalation: the game had its chance and the caravan is still sitting
                    // there, so pick a destination ourselves and send it.
                    if (state.Hours >= threshold * 2)
                    {
                        state.Hours = 0;
                        state.Nudged = false;
                        Evict(party, settlement);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTCaravanBehavior)}.{nameof(TickCaravans)}", ex);
            }
        }

        /// <summary>
        /// AI lord parties suffer the same class of failure as caravans, but it looks different:
        /// the party arrives at the settlement it was heading for, comes to rest on top of it,
        /// and then never enters and never picks anything else to do. Everything that could
        /// legitimately keep a party standing still - an army, a battle, a siege, being inside
        /// already - is excluded, so only genuine stalls are touched.
        /// </summary>
        private void TickLordParties(GlobalCommonConfig cfg)
        {
            int threshold = Math.Max(4, cfg.LordPartyStuckHours);

            try
            {
                foreach (var party in MobileParty.All.ToList())
                {
                    if (!IsStallCandidate(party))
                    {
                        if (party != null) stalled.Remove(party);
                        continue;
                    }

                    var target = party.TargetSettlement;

                    // Sitting on top of its own destination, rather than travelling towards it.
                    if (party.Position.Distance(target.Position) > AtSettlementDistance)
                    {
                        stalled.Remove(party);
                        continue;
                    }

                    if (!stalled.TryGetValue(party, out var state) || state.Settlement != target)
                    {
                        stalled[party] = new ParkedState { Settlement = target, Hours = 1 };
                        continue;
                    }

                    state.Hours++;

                    if (state.Hours >= threshold && !state.Nudged)
                    {
                        state.Nudged = true;
                        Nudge(party);
                        continue;
                    }

                    if (state.Hours >= threshold * 2)
                    {
                        state.Hours = 0;
                        state.Nudged = false;
                        ForceArrival(party, target);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTCaravanBehavior)}.{nameof(TickLordParties)}", ex);
            }
        }

        private static bool IsStallCandidate(MobileParty party)
        {
            if (party == null || !party.IsActive || !party.IsLordParty) return false;
            if (party.IsMainParty) return false;              // never touch the player
            if (party.CurrentSettlement != null) return false; // already inside, that is fine
            if (party.Army != null) return false;              // the army leader decides for it
            if (party.MapEvent != null) return false;          // in a battle
            if (party.SiegeEvent != null || party.BesiegedSettlement != null) return false;
            if (party.DefaultBehavior != AiBehavior.GoToSettlement) return false;

            return party.TargetSettlement != null;
        }

        /// <summary>
        /// The party has had its chance to decide for itself. If it is welcome in the settlement
        /// it has been standing on, put it inside; if it is not, send it somewhere it can go.
        /// </summary>
        private static void ForceArrival(MobileParty party, Settlement target)
        {
            try
            {
                var faction = party.MapFaction;
                bool welcome = target.MapFaction != null
                               && (faction == null || !target.MapFaction.IsAtWarWith(faction));

                party.Ai?.SetDoNotMakeNewDecisions(false);

                if (welcome)
                {
                    EnterSettlementAction.ApplyForParty(party, target);
                    return;
                }

                var elsewhere = FindFallbackDestination(party, target);
                if (elsewhere != null)
                    party.SetMoveGoToSettlement(elsewhere, party.NavigationCapability, false);
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTCaravanBehavior)}.{nameof(ForceArrival)}", ex);
            }
        }

        private static void Nudge(MobileParty party)
        {
            try
            {
                if (party.Ai == null) return;

                // A caravan whose AI was switched off - by a quest, or by another mod that never
                // switched it back on - never thinks again on its own.
                if (party.Ai.IsDisabled) party.Ai.EnableAi();
                party.Ai.SetDoNotMakeNewDecisions(false);
                party.Ai.RethinkAtNextHourlyTick = true;
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTCaravanBehavior)}.{nameof(Nudge)}", ex);
            }
        }

        private static void Evict(MobileParty party, Settlement from)
        {
            try
            {
                var target = FindFallbackDestination(party, from);
                if (target == null) return;

                party.Ai?.SetDoNotMakeNewDecisions(false);
                party.SetMoveGoToSettlement(target, party.NavigationCapability, false);
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTCaravanBehavior)}.{nameof(Evict)}", ex);
            }
        }

        /// <summary>
        /// Nearest town the caravan is not at war with, preferring towns of its owner's own
        /// faction; the caravan's home settlement is the last resort. Returns null only when the
        /// caravan has nowhere at all to go, in which case leaving it parked is the honest answer.
        /// </summary>
        public static Settlement FindFallbackDestination(MobileParty party, Settlement exclude)
        {
            if (party == null) return null;

            var faction = party.MapFaction;

            var candidates = Settlement.All
                .Where(s => s?.IsTown == true && s != exclude && s.Town != null)
                .Where(s => s.MapFaction != null
                            && (faction == null || !s.MapFaction.IsAtWarWith(faction)))
                .ToList();

            if (candidates.Count == 0)
            {
                var home = party.HomeSettlement;
                return home != null && home != exclude ? home : null;
            }

            var own = candidates.Where(s => s.MapFaction == faction).ToList();
            if (own.Count > 0) candidates = own;

            Settlement best = null;
            float bestDistance = float.MaxValue;
            foreach (var s in candidates)
            {
                float d = Campaign.Current.Models.MapDistanceModel
                    .GetDistance(party, s, false, party.NavigationCapability, out _);
                if (d < bestDistance) { bestDistance = d; best = s; }
            }

            return best;
        }
    }

    /// <summary>
    /// Supplies a destination when the game's own caravan destination search returns nothing.
    /// Runs as a postfix, so vanilla logic is untouched and this only ever fills a null.
    /// </summary>
    [HarmonyPatch]
    public static class CaravanDestinationFallbackPatch
    {
        private const string BehaviorType =
            "TaleWorlds.CampaignSystem.CampaignBehaviors.CaravansCampaignBehavior";

        // Resolved by name rather than typeof: a missing type here would otherwise throw out of
        // PatchAll and take every other patch in this assembly down with it.
        static bool Prepare() => AccessTools.TypeByName(BehaviorType) != null;

        static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName(BehaviorType);
            var method = type != null ? AccessTools.Method(type, "ThinkNextDestination") : null;
            if (method != null) yield return method;
        }

        static void Postfix(MobileParty caravanParty, ref Town __result,
            ref MobileParty.NavigationType bestNavigationType,
            ref bool isFromPort, ref bool isTargetingPort)
        {
            try
            {
                if (__result != null) return;
                if (BLTAdoptAHeroModule.CommonConfig?.CaravanFallbackDestination != true) return;
                if (caravanParty == null || !caravanParty.IsCaravan) return;

                var target = BLTCaravanBehavior.FindFallbackDestination(
                    caravanParty, caravanParty.CurrentSettlement);
                if (target?.Town == null) return;

                bestNavigationType = caravanParty.NavigationCapability;
                isFromPort = false;
                isTargetingPort = false;
                __result = target.Town;
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(CaravanDestinationFallbackPatch)}.{nameof(Postfix)}", ex);
            }
        }
    }
}
