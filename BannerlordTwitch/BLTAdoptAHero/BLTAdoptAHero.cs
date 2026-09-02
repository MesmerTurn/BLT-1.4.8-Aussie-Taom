using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Rewards;
using BannerlordTwitch.Util;
using BLTAdoptAHero.UI;
using BLTAdoptAHero;
using HarmonyLib;
using JetBrains.Annotations;
using SandBox.GauntletUI.Missions;
using SandBox.Tournaments.MissionLogics;
using SandBox.View;
using SandBox.View.Missions.NameMarkers;
using SandBox.ViewModelCollection.Missions.NameMarker;
using SandBox.ViewModelCollection.Missions.NameMarker.Targets;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.ComponentInterfaces;
using TaleWorlds.MountAndBlade.GauntletUI.Widgets.Mission.NameMarker;
using TaleWorlds.MountAndBlade.Source.Missions;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;
using static TaleWorlds.MountAndBlade.Launcher.Library.NativeMessageBox;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using BLTAdoptAHero.Models;
using BLTAdoptAHero.Actions;
using BLTAdoptAHero.Behaviors;

#pragma warning disable 649

namespace BLTAdoptAHero
{
    public static class TwitchDevUsers
    {
        public static readonly HashSet<string> Developers = new HashSet<string>
        {
            "randomchair22",
            "kanboru201"
        };
    }

    // Streamers who run BLT get their own tag, the same way developers get [DEV].
    // The list is read from "streamers.txt" next to BLTAdoptAHero.dll - one Twitch name
    // per line, blank lines and lines starting with # ignored - so new streamers can be
    // added without rebuilding the mod. The names below are the built-in defaults.
    public static class TwitchStreamerUsers
    {
        private static HashSet<string> cached;

        public static HashSet<string> Streamers => cached ??= Load();

        private static HashSet<string> Load()
        {
            // Twitch names are case insensitive, so the lookup is too.
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "aussielime_",
            };

            try
            {
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(TwitchStreamerUsers).Assembly.Location) ?? string.Empty,
                    "streamers.txt");

                if (System.IO.File.Exists(path))
                {
                    foreach (string raw in System.IO.File.ReadAllLines(path))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        result.Add(line);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Couldn't read streamers.txt, using built-in list only: {ex.Message}");
            }

            return result;
        }
    }

    // Twitch tells us a viewer's roles on every message, but only for that message - there
    // is no "look up this user's roles" call. So remember what we last saw per viewer, and
    // use it when deciding which cosmetic tag their hero should carry.
    public static class ViewerRoles
    {
        private class Roles
        {
            public bool IsModerator;
            public bool IsVip;
            public bool IsSubscriber;
        }

        private static readonly Dictionary<string, Roles> known =
            new Dictionary<string, Roles>(StringComparer.OrdinalIgnoreCase);

        public static void Update(ReplyContext context)
        {
            if (context == null || string.IsNullOrEmpty(context.UserName)) return;

            // Broadcaster counts as a moderator for tagging purposes.
            known[context.UserName] = new Roles
            {
                IsModerator = context.IsModerator || context.IsBroadcaster,
                IsVip = context.IsVip,
                IsSubscriber = context.IsSubscriber,
            };
        }

        public static bool IsModerator(string userName) =>
            known.TryGetValue(userName ?? "", out var r) && r.IsModerator;

        public static bool IsVip(string userName) =>
            known.TryGetValue(userName ?? "", out var r) && r.IsVip;

        public static bool IsSubscriber(string userName) =>
            known.TryGetValue(userName ?? "", out var r) && r.IsSubscriber;
    }

    // One place that knows every hero name tag, so adding another one later does not mean
    // hunting through a dozen files for hard coded "[BLT]" / "[DEV]" strings.
    public static class HeroNameTags
    {
        public static readonly string[] All =
        {
            BLTAdoptAHeroModule.Tag,
            BLTAdoptAHeroModule.DevTag,
            BLTAdoptAHeroModule.StreamerTag,
            BLTAdoptAHeroModule.ModTag,
            BLTAdoptAHeroModule.VipTag,
            BLTAdoptAHeroModule.SubTag,
        };

        /// <summary>
        /// The tag a given Twitch user's hero should carry. Purely cosmetic - it grants
        /// nothing. Highest standing wins: DEV, then Streamer, Mod, VIP, Sub, else plain BLT.
        /// </summary>
        public static string For(string userName) =>
            TwitchDevUsers.Developers.Contains(userName) ? BLTAdoptAHeroModule.DevTag
            : TwitchStreamerUsers.Streamers.Contains(userName) ? BLTAdoptAHeroModule.StreamerTag
            : ViewerRoles.IsModerator(userName) ? BLTAdoptAHeroModule.ModTag
            : ViewerRoles.IsVip(userName) ? BLTAdoptAHeroModule.VipTag
            : ViewerRoles.IsSubscriber(userName) ? BLTAdoptAHeroModule.SubTag
            : BLTAdoptAHeroModule.Tag;

        /// <summary>Removes any BLT tag suffix from a hero name.</summary>
        public static string Strip(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            foreach (string tag in All)
            {
                name = name.Replace(" " + tag, "");
            }
            return name.Trim();
        }

        /// <summary>
        /// Removes only the plain [BLT] tag, keeping the special ones ([DEV], [Streamer],
        /// [MOD], [VIP], [SUB]) visible. Used for the in-battle name markers: showing "[BLT]"
        /// above every adopted hero is just noise, but the role tags are the whole point.
        /// </summary>
        public static string StripPlainTagOnly(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return name.Replace(" " + BLTAdoptAHeroModule.Tag, "").Trim();
        }

        public static bool HasAny(string name) =>
            !string.IsNullOrEmpty(name) && All.Any(t => name.Contains(t));

        public static bool EndsWithAny(string name) =>
            !string.IsNullOrEmpty(name) && All.Any(t => name.EndsWith(" " + t));
    }


    [UsedImplicitly]
    [HarmonyPatch]
    public class BLTAdoptAHeroModule : MBSubModuleBase
    {
        private Harmony harmony;

        internal static GlobalCommonConfig CommonConfig { get; private set; }
        internal static GlobalTournamentConfig TournamentConfig { get; private set; }
        internal static GlobalHeroClassConfig HeroClassConfig { get; private set; }
        internal static GlobalHeroPowerConfig HeroPowerConfig { get; private set; }

        public BLTAdoptAHeroModule()
        {
            ActionManager.RegisterAll(typeof(BLTAdoptAHeroModule).Assembly);

            GlobalCommonConfig.Register();
            GlobalTournamentConfig.Register();
            GlobalHeroClassConfig.Register();
            GlobalHeroPowerConfig.Register();

            TournamentHub.Register();
            MissionInfoHub.Register();
            MapHub.Register();
            
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            try
            {
                mission.AddMissionBehavior(new BLTAdoptAHeroCommonMissionBehavior());
                mission.AddMissionBehavior(new BLTAdoptAHeroCustomMissionBehavior());
                mission.AddMissionBehavior(new BLTSummonBehavior());
                mission.AddMissionBehavior(new BLTBossMissionBehavior());
                mission.AddMissionBehavior(new BLTRemoveAgentsBehavior());
                mission.AddMissionBehavior(new BLTHeroPowersMissionBehavior());
                mission.AddMissionBehavior(new BLTHeroDetachmentBehavior());
                mission.AddMissionBehavior(new BLTFollowBehavior());
                mission.AddMissionBehavior(new BLTGuardBehavior());
                mission.AddMissionBehavior(new BLTDuelBehavior());
                //if (mission.CombatType == Mission.MissionCombatType.Combat && mission.PlayerTeam != null && mission.HasMissionBehavior<BLTAdoptAHeroCommonMissionBehavior>())
                //{
                //    mission.AddMissionBehavior(new HeroWidgetMissionView());
                //}
            }
            catch (Exception e)
            {
                Log.Exception(nameof(OnMissionBehaviorInitialize), e);
            }
        }


        //[UsedImplicitly, HarmonyPostfix, HarmonyPatch(typeof(MissionScreen), "TaleWorlds.MountAndBlade.IMissionSystemHandler.OnMissionAfterStarting")]
        //static void OnMissionAfterStartingPostFix(MissionScreen __instance)
        //{
        //    if (__instance.Mission.GetMissionBehavior<MissionNameMarkerUIHandler>() == null
        //    && (__instance.Mission.GetMissionBehavior<BattleSpawnLogic>() != null
        //        || __instance.Mission.GetMissionBehavior<TournamentFightMissionController>() != null))
        //    {
        //        __instance.AddMissionView(SandBoxViewCreator.CreateMissionNameMarkerUIHandler(__instance.Mission));
        //    }
        //}

        [HarmonyPatch(typeof(MissionAgentMarkerTargetVM))]
        [HarmonyPatch(MethodType.Constructor)]
        [HarmonyPatch(new[] { typeof(Agent) })]
        public static class MissionAgentMarkerTargetVM_Ctor_Patch
        {
            static void Postfix(MissionAgentMarkerTargetVM __instance, Agent target)
            {
                if (!(MissionHelpers.InSiegeMission() ||
                      MissionHelpers.InFieldBattleMission() /*||
                      MissionHelpers.InHideOutMission()*/))
                    return;

                bool isEnemy =
                    (Agent.Main != null && target.IsEnemyOf(Agent.Main)) ||
                    (Mission.Current.PlayerTeam?.IsValid == true && target.Team.IsEnemyOf(Mission.Current.PlayerTeam));

                bool isFriendly =
                    (Agent.Main != null && target.IsFriendOf(Agent.Main)) ||
                    (Mission.Current.PlayerTeam?.IsValid == true && target.Team.IsFriendOf(Mission.Current.PlayerTeam));

                if (isEnemy)
                {
                    __instance.NameType = "Enemy";
                    __instance.Name = HeroNameTags.StripPlainTagOnly(__instance.Name);
                    __instance.IsFriendly = false;
                    __instance.IsEnemy = true;
                    __instance.IsTracked = true;
                }
                else if (isFriendly)
                {
                    __instance.NameType = "Friendly";
                    __instance.Name = HeroNameTags.StripPlainTagOnly(__instance.Name);
                    __instance.IsFriendly = true;
                    __instance.IsEnemy = false;
                    __instance.IsTracked = true;
                }
            }
        }


        //[UsedImplicitly, HarmonyPrefix, HarmonyPatch(typeof(MissionGauntletNameMarkerView), "OnConversationEnd")]
        //public static bool OnConversationEndPrefix(MissionGauntletNameMarkerView __instance)
        //{
        //    return __instance.Mission != null;
        //}

        [UsedImplicitly, HarmonyPostfix, HarmonyPatch(typeof(NameMarkerScreenWidget), "OnLateUpdate")]
        public static void NameMarkerScreenWidget_OnLateUpdatePostfix(List<NameMarkerListPanel> ____markers)
        {
            foreach (var marker in ____markers)
            {
                marker.IsFocused = marker.IsInScreenBoundaries;
            }
        }




        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            if (harmony == null)
            {
                harmony = new Harmony("mod.bannerlord.bltadoptahero");
                harmony.PatchAll(); 
                NavalHarmonyPatches.ApplyIfAvailable(harmony);
            }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            try
            {
                if (game.GameType is Campaign)
                {
                    // Reload settings here so they are fresh
                    CommonConfig = GlobalCommonConfig.Get();
                    TournamentConfig = GlobalTournamentConfig.Get();
                    HeroClassConfig = GlobalHeroClassConfig.Get();
                    HeroPowerConfig = GlobalHeroPowerConfig.Get();

                    var campaignStarter = (CampaignGameStarter)gameStarterObject;
                    campaignStarter.AddBehavior(new BLTAdoptAHeroCampaignBehavior());
                    campaignStarter.AddBehavior(new BLTTournamentQueueBehavior());
                    campaignStarter.AddBehavior(new BLTCustomItemsCampaignBehavior());
                    campaignStarter.AddBehavior(new BLTClanBehavior());
                    campaignStarter.AddBehavior(new GoldIncomeBehavior()); 
                    campaignStarter.AddBehavior(new UpgradeBehavior());
                    campaignStarter.AddBehavior(new BLTLogsBehavior());
                    campaignStarter.AddBehavior(new BLTHeirBehavior());
                    //campaignStarter.AddBehavior(new BLTClanAllianceBehavior());
                    campaignStarter.AddBehavior(new TrainingBehavior());

                    // These drive campaign politics. They are ALWAYS registered - hundreds of
                    // call sites do Behaviour.Current.Something() and would throw
                    // NullReferenceException if the object simply didn't exist. Instead each
                    // one checks its own toggle in RegisterEvents and stays inert when off,
                    // so it exists but never touches the campaign. Toggles live in
                    // Configure Window > Campaign Features and default to ON.
                    campaignStarter.AddBehavior(new BLTSettlementUpgradeBehavior());
                    campaignStarter.AddBehavior(new ReinforcementBehavior());
                    campaignStarter.AddBehavior(new BLTClanArmyBehavior());
                    campaignStarter.AddBehavior(new PartyOrderBehavior());
                    campaignStarter.AddBehavior(new VassalBehavior());
                    campaignStarter.AddBehavior(new KingdomTaxBehavior());
                    campaignStarter.AddBehavior(new CapitalBehavior());

                    // Diplomacy
                    campaignStarter.AddBehavior(new BLTTreatyManager());         // 1. Core data
                    campaignStarter.AddBehavior(new BLTDiplomacyHelper());       // 2. Rebellion tracking
                    campaignStarter.AddBehavior(new BLTAllianceBehavior());      // 3. Alliance auto-join
                    campaignStarter.AddBehavior(new BLTDiplomacyBehavior());     // 4. Cleanup
                    campaignStarter.AddBehavior(new BLTClanDiplomacyBehavior()); // independent clans
                    campaignStarter.AddBehavior(new BLTPlayerOffersBehavior());

                    Log.Info($"[BLT] Campaign features - diplomacy:{CommonConfig?.EnableDiplomacyFeatures} "
                             + $"kingdom:{CommonConfig?.EnableKingdomFeatures} "
                             + $"army:{CommonConfig?.EnableArmyFeatures} "
                             + $"settlement:{CommonConfig?.EnableSettlementFeatures}");

                    gameStarterObject.AddModel(new BLTAgentApplyDamageModel(gameStarterObject.Models.OfType<AgentApplyDamageModel>().FirstOrDefault()));
                    gameStarterObject.AddModel(new BLTPartySizeLimitModel(gameStarterObject.Models.OfType<PartySizeLimitModel>().FirstOrDefault()));
                    gameStarterObject.AddModel(new BLTPartySpeedModel(gameStarterObject.Models.OfType<PartySpeedModel>().FirstOrDefault()));
                    gameStarterObject.AddModel(new BLTClanTierModel(gameStarterObject.Models.OfType<ClanTierModel>().FirstOrDefault()));
                }
            }
            catch (Exception e)
            {
                Log.Exception(nameof(OnGameStart), e);
                MessageBox.Show($"Error in {nameof(OnGameStart)}, please report this on the discord: {e}", "Bannerlord Twitch Mod STARTUP ERROR");
            }
        }

        public override void BeginGameStart(Game game)
        {
        }

        public override void OnGameEnd(Game game)
        {
            base.OnGameEnd(game);
            if (game.GameType is Campaign campaign)
            {
                JoinTournament.OnGameEnd(campaign);
            }
        }

        internal const string Tag = "[BLT]";
        internal const string DevTag = "[DEV]";
        internal const string StreamerTag = "[Streamer]";
        internal const string ModTag = "[MOD]";
        internal const string VipTag = "[VIP]";
        internal const string SubTag = "[SUB]";
    }

    public class BLTAgentApplyDamageModel : AgentApplyDamageModel
    {
        private readonly AgentApplyDamageModel previousModel;

        public BLTAgentApplyDamageModel(AgentApplyDamageModel previousModel)
        {
            this.previousModel = previousModel;
        }

        public override float ApplyDamageAmplifications(in AttackInformation attackInformation, in AttackCollisionData collisionData, float baseDamage)
        {
            return previousModel.ApplyDamageAmplifications(in attackInformation, in collisionData, baseDamage);
        }

        public override float ApplyDamageReductions(in AttackInformation attackInformation, in AttackCollisionData collisionData, float baseDamage)
        {
            return previousModel.ApplyDamageReductions(in attackInformation, in collisionData, baseDamage);
        }

        public override float ApplyDamageScaling(in AttackInformation attackInformation, in AttackCollisionData collisionData, float baseDamage)
        {
            return previousModel.ApplyDamageScaling(in attackInformation, in collisionData, baseDamage);
        }

        public override float ApplyGeneralDamageModifiers(in AttackInformation attackInformation, in AttackCollisionData collisionData, float baseDamage)
        {
            return previousModel.ApplyGeneralDamageModifiers(in attackInformation, in collisionData, baseDamage);
        }

        public override float CalculateAlternativeAttackDamage(in AttackInformation attackInformation, in AttackCollisionData collisionData, WeaponComponentData weapon)
        {
            return previousModel.CalculateAlternativeAttackDamage(in attackInformation, in collisionData, weapon);
        }

        public new float CalculateDamage(in AttackInformation attackInformation, in AttackCollisionData collisionData, float baseDamage)
        {
            return previousModel.CalculateDamage(in attackInformation, in collisionData, baseDamage);
        }

        public override void CalculateDefendedBlowStunMultipliers(
        Agent attackerAgent,
        Agent defenderAgent,
        CombatCollisionResult collisionResult,
        WeaponComponentData attackerWeapon,
        WeaponComponentData defenderWeapon,
        ref float attackerStunMultiplier,
        ref float defenderStunMultiplier)
        {
            previousModel.CalculateDefendedBlowStunMultipliers(
                attackerAgent,
                defenderAgent,
                collisionResult,
                attackerWeapon,
                defenderWeapon,
                ref attackerStunMultiplier,
                ref defenderStunMultiplier
            );
        }

        public override float CalculateHullFireDamage(float baseFireDamage, IShipOrigin shipOrigin)
        {
            if (CampaignHelpers.NavalDLC())
                return previousModel.CalculateHullFireDamage(baseFireDamage, shipOrigin);
            return baseFireDamage;
        }

        public override float CalculatePassiveAttackDamage(BasicCharacterObject attackerCharacter, in AttackCollisionData collisionData, float baseDamage)
        {
            return previousModel.CalculatePassiveAttackDamage(attackerCharacter, in collisionData, baseDamage);
        }

        public override float CalculateRemainingMomentum(float originalMomentum, in Blow b, in AttackCollisionData collisionData, Agent attacker, Agent victim, in MissionWeapon attackerWeapon, bool isCrushThrough)
        {
            return previousModel.CalculateRemainingMomentum(originalMomentum, in b, in collisionData, attacker, victim, in attackerWeapon, isCrushThrough);
        }

        public override float CalculateSailFireDamage(Agent attackerAgent, IShipOrigin shipOrigin, float baseDamage, bool damageFromShipMachine)
        {
            return previousModel.CalculateSailFireDamage(attackerAgent, shipOrigin, baseDamage, damageFromShipMachine);
        }

        public override float CalculateShieldDamage(in AttackInformation attackInformation, float baseDamage)
        {
            return previousModel.CalculateShieldDamage(in attackInformation, baseDamage);
        }

        public override float CalculateStaggerThresholdDamage(Agent defenderAgent, in Blow blow)
        {
            return previousModel.CalculateStaggerThresholdDamage(defenderAgent, in blow);
        }

        public override bool CanWeaponDealSneakAttack(in AttackInformation attackInformation, WeaponComponentData weapon)
            => previousModel.CanWeaponDealSneakAttack(in attackInformation, weapon);

        public override bool CanWeaponDismount(Agent attackerAgent, WeaponComponentData attackerWeapon, in Blow blow, in AttackCollisionData collisionData)
        {
            return previousModel.CanWeaponDismount(attackerAgent, attackerWeapon, in blow, in collisionData);
        }

        public override bool CanWeaponIgnoreFriendlyFireChecks(WeaponComponentData weapon)
        {
            return previousModel.CanWeaponIgnoreFriendlyFireChecks(weapon);
        }

        public override bool CanWeaponKnockback(Agent attackerAgent, WeaponComponentData attackerWeapon, in Blow blow, in AttackCollisionData collisionData)
        {
            return previousModel.CanWeaponKnockback(attackerAgent, attackerWeapon, in blow, in collisionData);
        }

        public override bool CanWeaponKnockDown(Agent attackerAgent, Agent victimAgent, WeaponComponentData attackerWeapon, in Blow blow, in AttackCollisionData collisionData)
        {
            return previousModel.CanWeaponKnockDown(attackerAgent, victimAgent, attackerWeapon, in blow, in collisionData);
        }

        public override bool DecideAgentDismountedByBlow(Agent attackerAgent, Agent victimAgent, in AttackCollisionData collisionData, WeaponComponentData attackerWeapon, in Blow blow)
        {
            return previousModel.DecideAgentDismountedByBlow(attackerAgent, victimAgent, in collisionData, attackerWeapon, in blow);
        }

        public override bool DecideAgentKnockedBackByBlow(Agent attackerAgent, Agent victimAgent, in AttackCollisionData collisionData, WeaponComponentData attackerWeapon, in Blow blow)
        {
            return previousModel.DecideAgentKnockedBackByBlow(attackerAgent, victimAgent, in collisionData, attackerWeapon, in blow);
        }

        public override bool DecideAgentKnockedDownByBlow(Agent attackerAgent, Agent victimAgent, in AttackCollisionData collisionData, WeaponComponentData attackerWeapon, in Blow blow)
        {
            return previousModel.DecideAgentKnockedDownByBlow(attackerAgent, victimAgent, in collisionData, attackerWeapon, in blow);
        }

        public override bool DecideAgentShrugOffBlow(Agent victimAgent, in AttackCollisionData collisionData, in Blow blow)
        {
            return previousModel.DecideAgentShrugOffBlow(victimAgent, collisionData, in blow);
        }

        public class DecideCrushedThroughParams
        {
            public float totalAttackEnergy;
            public Agent.UsageDirection attackDirection;
            public StrikeType strikeType;
            public WeaponComponentData defendItem;
            public bool isPassiveUsageHit;
            public bool crushThrough; // set this to override the behaviour
        }
        public override bool DecideCrushedThrough(Agent attackerAgent, Agent defenderAgent, float totalAttackEnergy,
            Agent.UsageDirection attackDirection, StrikeType strikeType, WeaponComponentData defendItem, bool isPassiveUsageHit)
        {
            bool originalResult = previousModel.DecideCrushedThrough(attackerAgent, defenderAgent, totalAttackEnergy, attackDirection, strikeType, defendItem, isPassiveUsageHit);
            var args = new DecideCrushedThroughParams
            {
                totalAttackEnergy = totalAttackEnergy,
                attackDirection = attackDirection,
                strikeType = strikeType,
                defendItem = defendItem,
                isPassiveUsageHit = isPassiveUsageHit,
                crushThrough = originalResult,
            };

            BLTHeroPowersMissionBehavior.PowerHandler?.CallHandlersForAgentPair(attackerAgent, defenderAgent,
                handlers => handlers.DecideCrushedThrough(attackerAgent, defenderAgent, args));

            return args.crushThrough;
        }

        public class DecideMissileWeaponFlagsParams
        {
            public MissionWeapon missileWeapon;
            public WeaponFlags missileWeaponFlags;
        }
        public override void DecideMissileWeaponFlags(Agent attackerAgent, in MissionWeapon missileWeapon, ref WeaponFlags missileWeaponFlags)
        {
            previousModel.DecideMissileWeaponFlags(attackerAgent, in missileWeapon, ref missileWeaponFlags);
            var args = new DecideMissileWeaponFlagsParams
            {
                missileWeapon = missileWeapon,
                missileWeaponFlags = missileWeaponFlags,
            };

            if (BLTHeroPowersMissionBehavior.PowerHandler?.CallHandlersForAgent(attackerAgent,
                handlers => handlers.DecideMissileWeaponFlags(attackerAgent, args)
                ) == true)
            {
                missileWeaponFlags = args.missileWeaponFlags;
            }
        }

        public override bool DecideMountRearedByBlow(Agent attackerAgent, Agent victimAgent, in AttackCollisionData collisionData, WeaponComponentData attackerWeapon, in Blow blow)
        {
            return previousModel.DecideMountRearedByBlow(attackerAgent, victimAgent, in collisionData, attackerWeapon, in blow);
        }

        public override MeleeCollisionReaction DecidePassiveAttackCollisionReaction(Agent attacker, Agent defender, bool isFatalHit)
        {
            return previousModel.DecidePassiveAttackCollisionReaction(attacker, defender, isFatalHit);
        }

        public override void DecideWeaponCollisionReaction(in Blow registeredBlow, in AttackCollisionData collisionData, Agent attacker, Agent defender, in MissionWeapon attackerWeapon, bool isFatalHit, bool isShruggedOff, float momentumRemaining, out MeleeCollisionReaction colReaction)
        {
            previousModel.DecideWeaponCollisionReaction(in registeredBlow, in collisionData, attacker, defender, in attackerWeapon, isFatalHit, isShruggedOff, momentumRemaining, out colReaction);
        }

        public override float GetDamageMultiplierForBodyPart(BoneBodyPartType bodyPart, DamageTypes type, bool isHuman, bool isMissile)
        {
            return previousModel.GetDamageMultiplierForBodyPart(bodyPart, type, isHuman, isMissile);
        }

        public override float GetDismountPenetration(Agent attackerAgent, WeaponComponentData attackerWeapon, in Blow blow, in AttackCollisionData collisionData)
        {
            return previousModel.GetDismountPenetration(attackerAgent, attackerWeapon, in blow, in collisionData);
        }

        public override float GetHorseChargePenetration()
        {
            return previousModel.GetHorseChargePenetration();
        }

        public override float GetKnockBackPenetration(Agent attackerAgent, WeaponComponentData attackerWeapon, in Blow blow, in AttackCollisionData collisionData)
        {
            return previousModel.GetKnockBackPenetration(attackerAgent, attackerWeapon, in blow, in collisionData);
        }

        public override float GetKnockDownPenetration(Agent attackerAgent, WeaponComponentData attackerWeapon, in Blow blow, in AttackCollisionData collisionData)
        {
            return previousModel.GetKnockDownPenetration(attackerAgent, attackerWeapon, in blow, in collisionData);
        }

        public override bool IsDamageIgnored(in AttackInformation attackInformation, in AttackCollisionData collisionData)
        {
            return previousModel.IsDamageIgnored(in attackInformation, in collisionData);
        }

        public override bool ShouldMissilePassThroughAfterShieldBreak(Agent attackerAgent, WeaponComponentData attackerWeapon)
        {
            return previousModel.ShouldMissilePassThroughAfterShieldBreak(attackerAgent, attackerWeapon);
        }

        //public override float CalculateDefaultRemainingMomentum(float originalMomentum, in Blow b, in AttackCollisionData collisionData, Agent attacker, Agent victim, in MissionWeapon attackerWeapon, bool isCrushThrough)
        //{
        //    return previousModel.CalculateDefaultRemainingMomentum(originalMomentum, in b, in collisionData, attacker, victim, in attackerWeapon, isCrushThrough);
        //}


        }
}