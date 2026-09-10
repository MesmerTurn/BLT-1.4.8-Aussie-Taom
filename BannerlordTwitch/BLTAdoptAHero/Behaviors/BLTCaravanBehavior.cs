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
            CampaignEvents.DailyTickHeroEvent.AddNonSerializedListener(this, OnDailyTickHero);
        }

        public override void SyncData(IDataStore dataStore) { }

        private void OnHourlyTick()
        {
            var cfg = BLTAdoptAHeroModule.CommonConfig;
            if (cfg == null) return;

            if (cfg.UnstickCaravans) TickCaravans(cfg);
            if (cfg.UnstickLordParties) TickLordParties(cfg);
        }

        /// <summary>
        /// Pays AI lords, world-wide, so that an overhaul which leaves them unable to afford
        /// wages, recruits or food does not slowly grind every kingdom down. Two independent
        /// knobs: a daily income, and a floor that only tops up lords who have fallen below it.
        /// The player and adopted heroes are never paid - viewers earn their gold.
        /// </summary>
        public void OnDailyTickHero(Hero hero)
        {
            var cfg = BLTAdoptAHeroModule.CommonConfig;
            if (cfg == null) return;

            int daily = cfg.AiLordDailyGold;
            int floor = cfg.AiLordMinimumGold;
            if (daily <= 0 && floor <= 0) return;

            try
            {
                if (!IsPayableAiLord(hero)) return;

                if (daily > 0)
                    GiveGoldAction.ApplyBetweenCharacters(null, hero, daily, true);

                // A floor, not an income: a lord already above it is paid nothing, so this never
                // makes rich lords richer.
                if (floor > 0 && hero.Gold < floor)
                    GiveGoldAction.ApplyBetweenCharacters(null, hero, floor - hero.Gold, true);
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTCaravanBehavior)}.{nameof(OnDailyTickHero)}", ex);
            }
        }

        private static bool IsPayableAiLord(Hero hero)
        {
            if (hero == null || !hero.IsAlive || !hero.IsLord) return false;
            if (hero.IsHumanPlayerCharacter) return false;
            if (hero == Hero.MainHero) return false;
            if (hero.IsPlayerCompanion) return false;

            // Adopted heroes earn their own gold through the channel; handing them a daily
            // allowance on top would quietly undo every price in the mod.
            return !hero.IsAdopted();
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
                        // On the road, which is exactly where we want it - but check that the
                        // road still leads somewhere it can safely arrive.
                        parked.Remove(party);
                        RedirectIfDestinationTurnedHostile(party);
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

                    var target = NearbySettlementFor(party);
                    if (target == null)
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

            return true;
        }

        /// <summary>
        /// The settlement a party is standing on, if it is standing on one at all.
        ///
        /// Deliberately does not require the party to still be heading there. When the campaign
        /// map reports a settlement as unreachable - the tell is a travel estimate in the
        /// hundreds of thousands of days - a party can arrive on top of it, fail to enter, and
        /// then fall out of GoToSettlement into some other behaviour entirely while never
        /// actually going anywhere. Judging by where the party physically is catches that;
        /// judging by what it intends does not.
        /// </summary>
        private static Settlement NearbySettlementFor(MobileParty party)
        {
            var target = party.TargetSettlement;
            if (target != null && party.Position.Distance(target.Position) <= AtSettlementDistance)
                return target;

            foreach (var s in Settlement.All)
            {
                if (s == null || (!s.IsTown && !s.IsCastle)) continue;
                if (party.Position.Distance(s.Position) <= AtSettlementDistance) return s;
            }

            return null;
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
        /// Whether a party may safely be sent to a settlement: it must exist, be owned, not be
        /// under siege, and above all not belong to somebody the party is at war with. A caravan
        /// walked into a hostile kingdom is a caravan destroyed, which is a worse outcome than
        /// the parking this whole feature exists to cure.
        /// </summary>
        private static bool IsSafeDestination(MobileParty party, Settlement settlement)
        {
            if (settlement?.MapFaction == null) return false;
            if (settlement.IsUnderSiege) return false;

            var faction = party?.MapFaction;
            if (faction == null) return true;

            if (settlement.MapFaction.IsAtWarWith(faction)) return false;

            // A caravan's owner can also be at war personally - through their own clan - while
            // their kingdom is not, so check the owning clan as well as the map faction.
            var ownerClan = party.ActualClan;
            if (ownerClan != null && ownerClan != settlement.MapFaction
                && settlement.MapFaction.IsAtWarWith(ownerClan))
                return false;

            return true;
        }

        /// <summary>
        /// A destination that was safe when it was chosen may not be safe by the time the caravan
        /// arrives - wars start while it is on the road. Every hour, any caravan travelling to a
        /// settlement that has since turned hostile is turned around.
        /// </summary>
        private static void RedirectIfDestinationTurnedHostile(MobileParty party)
        {
            try
            {
                var target = party.TargetSettlement;
                if (target == null) return;
                if (IsSafeDestination(party, target)) return;

                var elsewhere = FindFallbackDestination(party, target);
                if (elsewhere == null || elsewhere == target) return;

                party.Ai?.SetDoNotMakeNewDecisions(false);
                party.SetMoveGoToSettlement(elsewhere, party.NavigationCapability, false);
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTCaravanBehavior)}.{nameof(RedirectIfDestinationTurnedHostile)}", ex);
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
                .Where(s => IsSafeDestination(party, s))
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
