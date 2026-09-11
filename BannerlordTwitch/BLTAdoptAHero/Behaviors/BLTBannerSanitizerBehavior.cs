using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch.Util;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Keeps clan banners to icons the current module set actually has.
    ///
    /// Banner.CreateRandomBanner picks from whatever banner icons are registered, and a saved
    /// banner keeps the icon ids it was made with. Change the installed mods - or make a banner
    /// while a mod's icons are registered oddly - and a clan is left pointing at an icon that no
    /// longer exists. The result ranges from a blank or black banner to a native crash inside
    /// ApplyBannerTextureToMesh, because the mesh lookup returns nothing and nothing checks.
    ///
    /// Reported by Maku against clans created by promoting retinue and by the nemesis promotion,
    /// which is why the sweep covers every clan rather than only adopted heroes' clans: those
    /// clans are led by a promoted troop, not by an adopted hero, so a hero-only sweep - which is
    /// what the older builds do - walks straight past exactly the clans at fault.
    /// </summary>
    public class BLTBannerSanitizerBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(this, Sanitize);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, _ => Sanitize());
            // New clans appear during play - a promotion, a nemesis rising - long after load and
            // session launch have run. Daily rather than hourly: this walks every clan in the
            // campaign, and a broken banner is a cosmetic fault until something draws it.
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, Sanitize);
        }

        public override void SyncData(IDataStore dataStore) { }

        /// <summary>
        /// A random banner guaranteed to reference icons this module set has. Used at creation
        /// time so a new clan never gets a broken banner in the first place, rather than being
        /// repaired later by the sweep below.
        /// </summary>
        public static Banner CreateSafeBanner()
        {
            try
            {
                CollectValidIds(out var validIcons, out var validBg);
                if (validIcons.Count == 0 && validBg.Count == 0) return Banner.CreateRandomBanner();

                // A few attempts, because the randomness is the game's, not ours: if the icon
                // tables are healthy the first one is already fine.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    var candidate = Banner.CreateRandomBanner();
                    if (candidate != null && !HasInvalidIcon(candidate, validIcons, validBg))
                        return candidate;
                }

                // Every attempt was bad, which means the icon tables themselves disagree with what
                // CreateRandomBanner draws from. Borrow a banner that the campaign is already
                // drawing without trouble rather than inventing another broken one.
                var known = Clan.All?.FirstOrDefault(c =>
                    c?.Banner != null && !HasInvalidIcon(c.Banner, validIcons, validBg));

                return known?.Banner ?? Banner.CreateRandomBanner();
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTBannerSanitizerBehavior)}.{nameof(CreateSafeBanner)}", ex);
                return Banner.CreateRandomBanner();
            }
        }

        private static void Sanitize()
        {
            try
            {
                CollectValidIds(out var validIcons, out var validBg);
                if (validIcons.Count == 0 && validBg.Count == 0) return;

                int fixedCount = 0;

                foreach (var clan in Clan.All.ToList())
                {
                    try
                    {
                        if (clan?.Banner == null) continue;
                        if (!HasInvalidIcon(clan.Banner, validIcons, validBg)) continue;

                        var safe = CreateSafeBanner();
                        clan.Banner = safe;

                        // A kingdom draws its leader's banner in several places, so leaving the
                        // kingdom pointing at the old broken one would keep the fault visible.
                        if (clan.Kingdom != null && clan.Kingdom.RulingClan == clan)
                            clan.Kingdom.Banner = safe;

                        fixedCount++;
                        Log.Info($"[BannerFix] Replaced an unusable banner on clan '{clan.Name}'.");
                    }
                    catch (Exception exClan)
                    {
                        Log.Exception($"[BannerFix] Failed on clan '{clan?.Name}'", exClan);
                    }
                }

                if (fixedCount > 0)
                    Log.Info($"[BannerFix] Repaired {fixedCount} clan banner(s).");
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTBannerSanitizerBehavior)}.{nameof(Sanitize)}", ex);
            }
        }

        private static void CollectValidIds(out HashSet<int> validIcons, out HashSet<int> validBg)
        {
            validIcons = new HashSet<int>();
            validBg = new HashSet<int>();

            var mgr = BannerManager.Instance;
            if (mgr?.BannerIconGroups == null) return;

            foreach (var group in mgr.BannerIconGroups)
            {
                if (group?.AllIcons != null)
                    foreach (int key in group.AllIcons.Keys) validIcons.Add(key);
                if (group?.AllBackgrounds != null)
                    foreach (int key in group.AllBackgrounds.Keys) validBg.Add(key);
            }
        }

        /// <summary>
        /// The first entry of a banner is its background, everything after it is an icon, and the
        /// two are looked up in different tables - so they have to be checked against different
        /// sets or valid banners get thrown away as broken.
        /// </summary>
        private static bool HasInvalidIcon(Banner banner, HashSet<int> validIcons, HashSet<int> validBg)
        {
            var list = banner?.BannerDataList;
            if (list == null) return false;

            for (int i = 0; i < list.Count; i++)
            {
                int mesh = list[i].MeshId;
                bool ok = i == 0 ? validBg.Contains(mesh) : validIcons.Contains(mesh);
                if (!ok) return true;
            }

            return false;
        }
    }
}
