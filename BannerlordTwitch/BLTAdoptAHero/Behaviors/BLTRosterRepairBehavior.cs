using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BannerlordTwitch.Util;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Repairs party rosters that are already broken in the save.
    ///
    /// BLT used to take a summoned hero's retinue back out with AddToCounts(troop, -1) without
    /// checking the party still had one. Subtracting a troop that is not there leaves the roster
    /// claiming more entries than its backing array actually holds, and every later walk of that
    /// roster reads past the end:
    ///
    ///     IndexOutOfRangeException at TroopRoster.GetCharacterAtIndex
    ///       DefaultPartyMoraleModel.GetMoraleEffectsFromSkill
    ///
    /// which is Maku's crash, hit from the hourly clan tick, from starting a siege, and from the
    /// AI weighing up peace - anything that asks a party how strong it is.
    ///
    /// The subtraction itself is guarded now (BLTSummonBehavior.RemoveOne), but that only stops
    /// new damage. A save that was already played on the old build carries the broken rosters
    /// with it, so they have to be repaired on load as well.
    /// </summary>
    public class BLTRosterRepairBehavior : CampaignBehaviorBase
    {
        private static readonly FieldInfo DataField = AccessTools.Field(typeof(TroopRoster), "data");
        private static readonly FieldInfo CountField = AccessTools.Field(typeof(TroopRoster), "_count");
        private static readonly FieldInfo TotalRegularsField = AccessTools.Field(typeof(TroopRoster), "_totalRegulars");
        private static readonly FieldInfo TotalWoundedRegularsField = AccessTools.Field(typeof(TroopRoster), "_totalWoundedRegulars");
        private static readonly FieldInfo TotalHeroesField = AccessTools.Field(typeof(TroopRoster), "_totalHeroes");
        private static readonly FieldInfo TotalWoundedHeroesField = AccessTools.Field(typeof(TroopRoster), "_totalWoundedHeroes");

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, _ => RepairAll("load"));
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, _ => RepairAll("battle end"));
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, () => RepairAll("daily"));
        }

        public override void SyncData(IDataStore dataStore) { }

        private static void RepairAll(string reason)
        {
            if (DataField == null || CountField == null) return;
            try
            {
                int fixedRosters = 0;

                foreach (var party in MobileParty.All.ToList())
                {
                    if (Repair(party?.MemberRoster, party?.Name?.ToString())) fixedRosters++;
                    if (Repair(party?.PrisonRoster, party?.Name?.ToString())) fixedRosters++;
                }

                foreach (var settlement in Settlement.All.ToList())
                {
                    if (Repair(settlement?.Party?.MemberRoster, settlement?.Name?.ToString())) fixedRosters++;
                    if (Repair(settlement?.Party?.PrisonRoster, settlement?.Name?.ToString())) fixedRosters++;
                }

                if (fixedRosters > 0)
                {
                    Log.Info($"[RosterRepair] Repaired {fixedRosters} corrupt roster(s) on {reason}");
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTRosterRepairBehavior)}.{nameof(RepairAll)}", ex);
            }
        }

        /// <returns>true if this roster was actually broken and had to be rebuilt</returns>
        private static bool Repair(TroopRoster roster, string owner)
        {
            if (roster == null) return false;
            try
            {
                var data = DataField.GetValue(roster) as TroopRosterElement[];
                int count = (int) CountField.GetValue(roster);

                if (data == null)
                {
                    if (count == 0) return false;
                    CountField.SetValue(roster, 0);
                    return true;
                }

                // Entries the roster claims to have but cannot actually reach, plus any left with
                // no character or a count driven to zero or below.
                int reachable = Math.Min(count, data.Length);
                bool broken = count > data.Length || count < 0;
                for (int i = 0; i < reachable && !broken; i++)
                {
                    if (data[i].Character == null || data[i].Number <= 0) broken = true;
                }

                if (!broken) return false;

                var good = new List<TroopRosterElement>();
                for (int i = 0; i < reachable; i++)
                {
                    var element = data[i];
                    if (element.Character == null || element.Number <= 0) continue;
                    // A wounded count larger than the troop count corrupts the cached totals too.
                    if (element.WoundedNumber > element.Number) element.WoundedNumber = element.Number;
                    if (element.WoundedNumber < 0) element.WoundedNumber = 0;
                    good.Add(element);
                }

                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = i < good.Count ? good[i] : default;
                }

                CountField.SetValue(roster, good.Count);
                // The roster caches its own totals; they are wrong now too, so recount by hand
                // (CalculateCachedStatsOnLoad is static and only serves the load-game list).
                int regulars = 0, woundedRegulars = 0, heroes = 0, woundedHeroes = 0;
                foreach (var element in good)
                {
                    if (element.Character.IsHero)
                    {
                        heroes += element.Number;
                        woundedHeroes += element.WoundedNumber;
                    }
                    else
                    {
                        regulars += element.Number;
                        woundedRegulars += element.WoundedNumber;
                    }
                }
                TotalRegularsField?.SetValue(roster, regulars);
                TotalWoundedRegularsField?.SetValue(roster, woundedRegulars);
                TotalHeroesField?.SetValue(roster, heroes);
                TotalWoundedHeroesField?.SetValue(roster, woundedHeroes);
                roster.UpdateVersion();

                Log.Info($"[RosterRepair] {owner ?? "unnamed party"}: {count} -> {good.Count} entries "
                         + $"(backing array holds {data.Length})");
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTRosterRepairBehavior)}.{nameof(Repair)}", ex);
                return false;
            }
        }
    }
}
