using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using BLTAdoptAHero.Powers;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.AgentOrigins;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace BLTAdoptAHero
{
    public enum BossRarity
    {
        Common,
        Epic,
        Legendary,
    }

    // Random boss spawning, per the "!focusmelee etc" chat thread with siemanko_smialy/mrudd/
    // irishtom666: a chance each field battle/siege to spawn an oversized, tougher enemy (or
    // ally, if enabled) using one of the streamer's configured HeroClassDef classes, with a
    // rarity-appropriate name from BossNamePool, an HP bar overlay (BossBarMissionView, since
    // this class - a plain MissionBehavior - has no MissionScreen to draw to), and a reward for
    // whichever BLT hero lands the killing blow.
    //
    // The boss is a throwaway Hero+Agent that lives only for this mission - never registered
    // with BLTAdoptAHeroCampaignBehavior, never shown in !heroinfo or the adoption pool, gone
    // once the mission ends regardless of whether it died.
    public class BLTBossMissionBehavior : AutoMissionBehavior<BLTBossMissionBehavior>
    {
        public class BossState
        {
            public Hero Hero;
            public Agent Agent;
            public BossRarity Rarity;
            public string DisplayName;
            public float MaxHealth;
            public bool Dead;
            // Signature particle effect for this figure, kept so it can be stopped when the boss
            // dies - otherwise the emitter outlives the corpse.
            public AgentPfx Pfx;
            // Active powers are normally temporary. A boss is supposed to have them permanently,
            // so these get re-activated from the tick whenever they lapse.
            public List<IHeroPowerActive> ActivePowers = new();
        }

        private readonly List<BossState> bosses = new();
        public IReadOnlyList<BossState> Bosses => bosses;
        private bool hasRolled;

        // Boss heroes are deliberately NOT adopted (no [BLT] name tag), so agent.GetAdoptedHero()
        // returns null for them and the whole power dispatch in PowerHandler skips their agent.
        // This registry lets the power system - and only the power system - map a boss agent back
        // to its hero, without leaking the boss into adoption/leaderboard/nametag handling.
        public static Hero GetBossHeroForAgent(Agent agent)
        {
            if (agent == null) return null;
            var self = Current;
            if (self == null) return null;
            foreach (var boss in self.bosses)
            {
                if (boss.Agent == agent) return boss.Hero;
            }
            return null;
        }

        // Find any party backing the given team.
        //
        // Agents do NOT all use PartyAgentOrigin: bandit/looter troops in particular come through
        // other IAgentOriginBase implementations, which is why looking only for PartyAgentOrigin
        // found the player's party every time and the enemy's never. Every origin exposes its
        // combatant though, and for a mobile party that combatant IS the PartyBase - so fall back
        // to that before giving up.
        private static PartyBase ResolvePartyFor(Team team)
        {
            var agents = new List<Agent>();
            if (team?.TeamAgents != null) agents.AddRange(team.TeamAgents);
            if (team?.ActiveAgents != null) agents.AddRange(team.ActiveAgents);

            foreach (var agent in agents)
            {
                if (agent?.Origin is PartyAgentOrigin partyOrigin && partyOrigin.Party != null)
                    return partyOrigin.Party;
            }
            foreach (var agent in agents)
            {
                if (agent?.Origin?.BattleCombatant is PartyBase party)
                    return party;
            }

            return null;
        }

        // Deliberately NOT rolling in AfterStart(): at that point the mission exists but no troops
        // have spawned yet, so the target team has no living agent to place the boss beside and
        // the roll always bailed out silently. Wait until the battle is actually populated.
        private float missionTime;
        private const float SpawnDelaySeconds = 3f;
        private const float SpawnGiveUpSeconds = 45f;

        // Cultures that actually have equipment in the loaded modules.
        //
        // A culture existing is NOT the same as a culture having gear: TAOM defines cultures for
        // the Misty Mountain orcs, goblins, Lothlorien, Umbar and the two southern realms, but no
        // module ships items tagged with them. Equipping from such a culture finds nothing, and
        // EquipHero then falls back to an unfiltered search - which is how Bolg ended up wearing a
        // vanilla steppe helmet. Only offer a themed culture when there is gear behind it.
        private static HashSet<string> culturesWithEquipment;
        private static HashSet<string> CulturesWithEquipment
        {
            get
            {
                if (culturesWithEquipment != null) return culturesWithEquipment;
                culturesWithEquipment = new HashSet<string>();
                foreach (var item in CampaignHelpers.AllItems)
                {
                    if (item?.Culture?.StringId != null)
                        culturesWithEquipment.Add(item.Culture.StringId);
                }
                return culturesWithEquipment;
            }
        }

        private static bool SideIsPopulated(Team team)
            => team?.ActiveAgents?.Any(a => a.IsActive() && a.IsHuman) == true;

        private void RollForBosses()
        {
            var cfg = GlobalCommonConfig.Get();
            if (cfg?.BossEnabled != true) return;

            // Only field battles and sieges. InFieldBattleMission() alone is NOT enough of a
            // gate: it's just Mission.IsFieldBattle, and the engine flags tournaments as field
            // battles too - which let a boss spawn into a tournament and then hard-crashed, since
            // SpawnAgent asks for a formation-derived spawn position that doesn't exist there
            // ("Nullable object must have a value"). Everything non-battle is excluded explicitly.
            bool isSiege = MissionHelpers.InSiegeMission();
            bool isFieldBattle = MissionHelpers.InFieldBattleMission();
            if (!isSiege && !isFieldBattle)
            {
                hasRolled = true;
                Log.LogFeedSystem("[Boss] Skipped: not a field battle or siege.");
                return;
            }
            string excluded =
                MissionHelpers.InTournament() ? "tournament"
                : MissionHelpers.InArenaPracticeMission() ? "arena practice"
                : MissionHelpers.InArenaPracticeVisitingArea() ? "arena area"
                : MissionHelpers.InTrainingFieldMission() ? "training field"
                : MissionHelpers.InFriendlyMission() ? "friendly mission"
                : MissionHelpers.InHideOutMission() ? "hideout"
                : MissionHelpers.InLordsHallBattleMission() ? "lord's hall"
                : MissionHelpers.InConversationMission() ? "conversation"
                : MissionHelpers.InVillageEncounter() ? "village encounter"
                : null;
            if (excluded != null)
            {
                hasRolled = true;
                Log.LogFeedSystem($"[Boss] Skipped: excluded mission type ({excluded}).");
                return;
            }
            if (Mission.Current?.CombatType != Mission.MissionCombatType.Combat)
            {
                hasRolled = true;
                Log.LogFeedSystem($"[Boss] Skipped: CombatType is {Mission.Current?.CombatType}.");
                return;
            }
            if (Mission.Current.PlayerTeam?.IsValid != true || Mission.Current.PlayerEnemyTeam?.IsValid != true)
            {
                // Teams may not be valid yet - don't consume the roll, try again next tick.
                return;
            }
            if (hasRolled) return;
            hasRolled = true;

            float commonPct = isSiege ? cfg.BossCommonWeightSiege : cfg.BossCommonWeightFieldBattle;
            float epicPct = isSiege ? cfg.BossEpicWeightSiege : cfg.BossEpicWeightFieldBattle;
            float legendaryPct = isSiege ? cfg.BossLegendaryWeightSiege : cfg.BossLegendaryWeightFieldBattle;

            var rng = new Random();

            BossRarity? RollRarity()
            {
                // Rarest first - only one can trigger per roll.
                if (rng.NextDouble() * 100.0 < legendaryPct) return BossRarity.Legendary;
                if (rng.NextDouble() * 100.0 < epicPct) return BossRarity.Epic;
                if (rng.NextDouble() * 100.0 < commonPct) return BossRarity.Common;
                return null;
            }

            // One independent roll per boss slot, up to the configured cap - the old version only
            // ever had two candidate sides in a list, so "Max Bosses Per Battle" above 2 could
            // never actually be reached. Each slot that rolls a rarity also picks its own side.
            int slots = Math.Max(1, cfg.BossMaxPerBattle);
            for (int i = 0; i < slots; i++)
            {
                if (bosses.Count >= cfg.BossMaxPerBattle) break;
                var rarity = RollRarity();
                if (rarity == null) continue;

                bool onPlayerSide = cfg.BossCanSpawnOnAllySide && rng.NextDouble() < 0.5;
                var side = onPlayerSide ? Mission.Current.PlayerTeam : Mission.Current.PlayerEnemyTeam;
                SpawnBoss(side, onPlayerSide, rarity.Value, cfg);
            }
        }

        // onPlayerSide is passed in rather than re-derived from the team: Team.IsPlayerAlly depends
        // on mission team relations, which in some battles (bandit/looter encounters especially)
        // report the enemy team as friendly, so every boss silently ended up on the player's side.
        private void SpawnBoss(Team side, bool onPlayerSide, BossRarity rarity, GlobalCommonConfig cfg)
        {
            var classDefs = GlobalHeroClassConfig.Get()?.ValidClasses?.ToList();
            if (classDefs == null || classDefs.Count == 0) return;

            // Mounted bosses are excluded unless explicitly allowed: a boss on a horse is useless
            // in a siege, and it does not fit factions that fight on foot. If every configured
            // class happens to be mounted, use them anyway rather than skipping the boss entirely.
            if (!cfg.BossAllowMounted)
            {
                var onFoot = classDefs.Where(c => !c.Mounted).ToList();
                if (onFoot.Count > 0) classDefs = onFoot;
            }
            var classDef = classDefs.SelectRandom();

            var archetype = classDef.Mounted ? BossArchetype.Cavalry
                : classDef.Slots.Any(s => s is EquipmentType.Bow or EquipmentType.Crossbow) ? BossArchetype.Archer
                : BossArchetype.Melee;
            // With Random Culture on, the NAME leads and the culture follows it: pick a figure
            // worth this rarity whose culture is present in the loaded game, then equip the boss
            // from that culture. That is what makes Sauron carry Mordor gear and Legolas carry
            // Mirkwood gear, instead of a faction name landing on unrelated equipment.
            var mainCultures = CampaignHelpers.MainCultures.ToList();
            BossNameEntry? themedEntry = cfg.BossRandomCulture
                ? BossNamePool.PickForRarity(rarity, archetype,
                    id => !string.IsNullOrEmpty(id)
                          && mainCultures.Any(c => c.StringId == id)
                          && CulturesWithEquipment.Contains(id))
                : null;

            var nameEntry = themedEntry ?? BossNamePool.Pick(archetype);

            // A themed name dictates its own culture. Without one we fall back to a plain random
            // culture (Random Culture on, non-TAOM install) or the culture of the side spawned on.
            var culture = themedEntry != null
                ? mainCultures.FirstOrDefault(c => c.StringId == themedEntry.Value.CultureId)
                : cfg.BossRandomCulture
                    ? mainCultures.SelectRandom()
                    : onPlayerSide ? Hero.MainHero.Culture
                        : (Mission.Current.PlayerEnemyTeam.TeamAgents.FirstOrDefault()?.Character as CharacterObject)?.Culture;
            culture ??= mainCultures.FirstOrDefault();

            var template = CampaignHelpers.GetWandererTemplates(culture).SelectRandom()
                ?? CampaignHelpers.AllWandererTemplates.SelectRandom();
            if (template == null) return;

            var hero = HeroCreator.CreateSpecialHero(template);
            if (hero == null) return;
            hero.ChangeState(Hero.CharacterStates.Active);
            hero.SetName(new TaleWorlds.Localization.TextObject(nameEntry.FullName), new TaleWorlds.Localization.TextObject(nameEntry.FullName));
            // Deliberately NOT calling BLTAdoptAHeroCampaignBehavior.Current.SetIsCreatedHero -
            // this hero is never tracked as an adopted BLT hero.

            // Decent baseline skills so the agent fights competently regardless of what the
            // random wanderer template started with - the HP/Armor multipliers are what actually
            // make it dangerous, this just stops it whiffing every swing.
            foreach (var skill in new[]
                     {
                         DefaultSkills.OneHanded, DefaultSkills.TwoHanded, DefaultSkills.Polearm,
                         DefaultSkills.Bow, DefaultSkills.Crossbow, DefaultSkills.Throwing,
                         DefaultSkills.Riding, DefaultSkills.Athletics,
                     })
                hero.HeroDeveloper.SetInitialSkillLevel(skill, 120);

            // Culture-filter the gear only when a culture was deliberately chosen; EquipHero already
            // falls back through lower tiers and then an unfiltered search if that culture has
            // nothing suitable for a slot, so a sparse culture cannot leave the boss half-naked.
            EquipHero.UpgradeEquipment(hero, targetTier: 5, classDef, replaceSameTier: true,
                cultureFilter: cfg.BossRandomCulture ? culture : null,
                cultureFilterSpecified: cfg.BossRandomCulture && culture != null);

            // SpawnAgent builds a PartyAgentOrigin from this, and the engine will throw if it
            // can't resolve a party/spawn position - so bail out rather than spawn with nulls.
            // Scan ALL agents of the target side for a usable party, not just the first one:
            // the first team agent is often something without a PartyAgentOrigin (a mount owner,
            // a garrison filler, a scripted agent), which made the enemy lookup return null every
            // time and silently pushed every boss onto the player's side.
            var spawnParty = ResolvePartyFor(side) ?? (onPlayerSide ? PartyBase.MainParty : null);
            if (spawnParty == null)
            {
                Log.LogFeedSystem($"[Boss] Rolled a {rarity} boss for the {(onPlayerSide ? "ally" : "enemy")} side, but that side has no party to spawn from - skipped.");
                return;
            }

            // Spawn next to an existing living agent of the target side, passing an EXPLICIT
            // position rather than letting the engine derive one from a formation. The generic
            // BLTSummonBehavior.SpawnAgent passes initialPosition: null with hasFormation: true,
            // which makes the engine look up a formation spawn frame - in any mission without
            // proper battle formations that Nullable comes back empty and SpawnAgent throws
            // ("Nullable object must have a value"), which then left the mission corrupted enough
            // to crash natively in Mission.Tick a moment later. An explicit position removes that
            // whole failure mode regardless of mission type.
            var anchor = side.ActiveAgents?.FirstOrDefault(a => a.IsActive() && a.IsHuman);
            if (anchor == null)
            {
                Log.Trace("[Boss] No living agent on the target side to spawn beside - skipping.");
                return;
            }

            var spawnPos = anchor.Position;
            spawnPos.x += 2f;
            var spawnDir = anchor.GetMovementDirection();

            Agent agent;
            try
            {
                agent = Mission.Current.SpawnTroop(
                    new PartyAgentOrigin(spawnParty, hero.CharacterObject),
                    isPlayerSide: onPlayerSide,
                    hasFormation: true,
                    spawnWithHorse: classDef.Mounted && Mission.Current.IsNavalBattle == false,
                    isReinforcement: true,
                    formationTroopCount: 1,
                    formationTroopIndex: 0,
                    isAlarmed: true,
                    wieldInitialWeapons: true,
                    initialPosition: spawnPos,
                    initialDirection: spawnDir);
            }
            catch (Exception ex)
            {
                // Never let a boss spawn take the whole mission down with it.
                Log.Error($"[Boss] Spawn failed: {ex.Message}");
                return;
            }
            if (agent == null) return;
            agent.MountAgent?.FadeIn();
            agent.FadeIn();

            float hpMult = rarity switch
            {
                BossRarity.Common => cfg.BossCommonHpMultiplier,
                BossRarity.Epic => cfg.BossEpicHpMultiplier,
                _ => cfg.BossLegendaryHpMultiplier,
            };
            float armorMult = rarity switch
            {
                BossRarity.Common => cfg.BossCommonArmorMultiplier,
                BossRarity.Epic => cfg.BossEpicArmorMultiplier,
                _ => cfg.BossLegendaryArmorMultiplier,
            };
            float scale = rarity switch
            {
                BossRarity.Common => cfg.BossCommonScale,
                BossRarity.Epic => cfg.BossEpicScale,
                _ => cfg.BossLegendaryScale,
            };
            int powerCount = rarity switch
            {
                BossRarity.Common => cfg.BossCommonPowerCount,
                BossRarity.Epic => cfg.BossEpicPowerCount,
                _ => cfg.BossLegendaryPowerCount,
            };

            // Health scaling, with two failure modes to avoid, both of which have been seen:
            //
            //  - Multiplying an already high base past 32767 wrapped into negatives ("-270 / -270"),
            //    because the engine stores health as a 16-bit value. Hence the upper clamp.
            //  - Reading BaseHealthLimit straight off a freshly spawned agent can give 0, and 0
            //    times any multiplier is 0, which the lower clamp then turned into a 1 HP boss.
            //    So establish a sane base first and only scale that.
            const float MaxSafeHealth = 30000f;
            const float DefaultBaseHealth = 100f;

            float baseHealth = agent.BaseHealthLimit;
            if (baseHealth < 1f) baseHealth = agent.HealthLimit;
            if (baseHealth < 1f) baseHealth = agent.Health;
            if (baseHealth < 1f) baseHealth = DefaultBaseHealth;

            float scaledHealth = MBMath.ClampFloat(baseHealth * hpMult, DefaultBaseHealth, MaxSafeHealth);
            agent.BaseHealthLimit = scaledHealth;
            agent.HealthLimit = scaledHealth;
            agent.Health = scaledHealth;
            ApplyArmorMultiplier(agent, armorMult);
            SetAgentScale(agent, scale);

            // Signature effect for this specific figure (black smoke on the Witch-king, and so
            // on). Only the Middle-earth roster carries these; a boss without one just spawns
            // plain. Wrapped because a bad particle name is a cosmetic problem, not a reason to
            // lose the boss.
            AgentPfx bossPfx = null;
            try
            {
                var effects = nameEntry.ParticleEffects.ToList();
                if (effects.Count > 0)
                {
                    bossPfx = new AgentPfx(agent, effects);
                    bossPfx.Start();
                }
            }
            catch (Exception ex)
            {
                Log.Trace($"[Boss] Could not start particle effect for {nameEntry.FullName}: {ex.Message}");
                bossPfx = null;
            }

            var state = new BossState
            {
                Hero = hero,
                Agent = agent,
                Pfx = bossPfx,
                Rarity = rarity,
                DisplayName = nameEntry.FullName,
                MaxHealth = agent.HealthLimit,
                ActivePowers = ForceUnlockPowers(hero, classDef, powerCount),
            };
            bosses.Add(state);

            // Force the team assignment: SpawnTroop derives it from isPlayerSide plus the origin
            // party, and a mismatched pair silently lands the agent on the wrong team.
            if (side != null && agent.Team != side) agent.SetTeam(side, true);

            Log.LogFeedEvent($"A {rarity} boss has appeared on the {(onPlayerSide ? "ally" : "enemy")} side: {nameEntry.FullName}!");
        }

        // Agent has no public armor multiplier - this goes through the same reflection technique
        // used elsewhere in this codebase (SpawnRetinue's AccessTools.Field(typeof(Agent), "_name")),
        // and silently no-ops if this game version doesn't expose it under either name tried -
        // never crash a boss spawn over a cosmetic-strength stat, HP scaling alone still matters.
        private static void ApplyArmorMultiplier(Agent agent, float multiplier)
        {
            if (multiplier == 1f) return;
            try
            {
                var method = AccessTools.Method(typeof(Agent), "SetArmorEffectivenessMultiplier")
                    ?? AccessTools.PropertySetter(typeof(Agent), "ArmorEffectivenessMultiplier");
                method?.Invoke(agent, new object[] { multiplier });
            }
            catch (Exception ex)
            {
                Log.Error($"[Boss] Couldn't apply armor multiplier: {ex.Message}");
            }
        }

        private static void SetAgentScale(Agent agent, float scale)
        {
            if (scale == 1f) return;
            try
            {
                AccessTools.Method(typeof(Agent), "SetInitialAgentScale")?.Invoke(agent, new object[] { scale });
            }
            catch (Exception ex)
            {
                Log.Error($"[Boss] Couldn't apply scale: {ex.Message}");
            }
        }

        // Bypasses the class's normal per-power Requirements (kills/battles/etc, which a
        // freshly-created boss hero can never satisfy) and force-grants the first N powers, in
        // configured order, from both the class's Active and Passive power groups.
        //
        // Deliberately does NOT go through ActivePowerGroup.Activate / PassivePowerGroup.
        // OnHeroJoinedBattle - both of those filter through GetUnlockedPowers(hero), which is
        // exactly the requirement gate we need to skip. Instead this calls the same underlying
        // per-power entry points those methods would have called, just without the filtering.
        private static List<IHeroPowerActive> ForceUnlockPowers(Hero hero, HeroClassDef classDef, int count)
        {
            var activated = new List<IHeroPowerActive>();
            if (count <= 0) return activated;
            try
            {
                foreach (var item in classDef.PassivePower?.ValidPowers?.Take(count) ?? Enumerable.Empty<PassivePowerGroupItem>())
                {
                    var power = item.Power;
                    BLTHeroPowersMissionBehavior.PowerHandler?.ConfigureHandlers(
                        hero, power as HeroPowerDefBase, handlers => power.OnHeroJoinedBattle(hero, handlers));
                }

                foreach (var item in classDef.ActivePower?.ValidPowers?.Take(count) ?? Enumerable.Empty<ActivePowerGroupItem>())
                {
                    var power = item.Power;
                    power.Activate(hero, () => { });
                    activated.Add(power);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Boss] Power unlock failed: {ex.Message}");
            }
            return activated;
        }

        public override void OnMissionTick(float dt)
        {
            SafeCall(() =>
            {
                missionTime += dt;
                if (hasRolled || missionTime < SpawnDelaySeconds) return;

                // Wait until BOTH sides actually have troops on the field, not just a fixed delay.
                // Enemy troops often stream in later than the player's, and the spawn needs the
                // target side for two things - a party for the agent origin, and a living agent to
                // place the boss beside - so rolling too early skipped every enemy-side boss.
                if (!SideIsPopulated(Mission.Current?.PlayerTeam)
                    || !SideIsPopulated(Mission.Current?.PlayerEnemyTeam))
                {
                    if (missionTime < SpawnGiveUpSeconds) return;
                    Log.LogFeedSystem("[Boss] Gave up waiting for both sides to deploy.");
                }

                RollForBosses();
            });

            SafeCall(() =>
            {
                var cfg = GlobalCommonConfig.Get();
                foreach (var state in bosses.Where(b => !b.Dead))
                {
                    if (state.Agent == null || !state.Agent.IsActive())
                    {
                        state.Dead = true;
                        continue;
                    }
                    if (cfg?.BossHpRegenerates != true)
                    {
                        // Clamp back down every tick rather than trying to intercept whatever
                        // mechanism would otherwise heal it - simplest reliable way to guarantee
                        // "never regenerates" regardless of what else touches Agent.Health.
                        if (state.Agent.Health > state.Agent.HealthLimit)
                            state.Agent.Health = state.Agent.HealthLimit;
                    }

                    // Keep the class's active powers permanently up - they're duration-based for
                    // normal heroes, but a boss is meant to have them for the whole fight.
                    foreach (var power in state.ActivePowers)
                    {
                        if (!power.IsActive(state.Hero))
                            power.Activate(state.Hero, () => { });
                    }
                }
            });
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            SafeCall(() =>
            {
                var state = bosses.FirstOrDefault(b => b.Agent == affectedAgent);
                if (state == null || state.Dead) return;
                if (agentState != AgentState.Killed && agentState != AgentState.Unconscious) return;
                state.Dead = true;
                try { state.Pfx?.Stop(); } catch { /* cosmetic only */ }
                state.Pfx = null;

                var killerHero = affectorAgent?.GetAdoptedHero();
                if (killerHero == null) return;

                var cfg = GlobalCommonConfig.Get();
                float rewardMult = state.Rarity switch
                {
                    BossRarity.Common => 1f,
                    BossRarity.Epic => 2.5f,
                    _ => 5f,
                };
                int gold = (int)(cfg.BossGoldReward * rewardMult);
                int xp = (int)(cfg.BossXPReward * rewardMult);

                // Route the reward through the same path Kill Rewards uses, rather than paying
                // gold here and leaving XP unhandled: this awards both, runs the XP through the
                // normal skill/level-up handling, and keeps the mission HUD Gold/XP counters in
                // step. Scaling arguments are neutral because the rarity multiplier above is
                // already the scaling for a boss.
                var rewards = BLTAdoptAHeroCommonMissionBehavior.Current;
                if (rewards != null)
                {
                    rewards.ApplyStreakEffects(killerHero, gold, xp,
                        subBoost: 1f, relativeLevelScaling: null, levelScalingCap: null);
                }
                else
                {
                    // No mission reward behavior present - at least make sure the gold lands.
                    BLTAdoptAHeroCampaignBehavior.Current.ChangeHeroGold(killerHero, gold);
                }

                Log.LogFeedEvent($"{killerHero.Name} slew {state.DisplayName}! +{gold}{Naming.Gold} +{xp}XP");
            });
        }

        protected override void OnEndMission()
        {
            SafeCall(bosses.Clear);
        }
    }

    // Separate MissionView (not a plain MissionBehavior, which has no MissionScreen to draw to -
    // same split BLTHeroWidgetBehavior.cs uses) purely for the boss HP bar overlay. Reads its
    // data from BLTBossMissionBehavior.Current rather than owning any boss state itself.
    //
    // NOTE: this loads a Gauntlet movie named "BLTBossBar" - that prefab XML doesn't exist yet
    // and needs to be authored (mirroring BLTHeroNametag's prefab) before this actually renders
    // anything. Flagged clearly rather than silently assumed to work.
    [DefaultView]
    public class BossBarMissionView : MissionView
    {
        public const float BarWidthPixels = 140f;

        private GauntletLayer _layer;
        private BossBarCollectionVM _vm;
        private Camera _camera;
        private bool _isInitialized;
        private bool _uiFailed;
        private readonly Dictionary<BLTBossMissionBehavior.BossState, BossBarVM> _barVMs = new();

        public override void OnMissionScreenTick(float dt)
        {
            if (_uiFailed) return;
            var bossBehavior = Mission.Current?.GetMissionBehavior<BLTBossMissionBehavior>();
            if (bossBehavior == null || bossBehavior.Bosses.Count == 0 || MissionScreen == null) return;

            if (!_isInitialized)
            {
                // If the BLTBossBar prefab is missing or malformed, LoadMovie leaves the movie
                // with a null prefab; the game then crashes later in GauntletMovie.Release() when
                // screens are cleaned up on exit. So build the layer, and only keep it if the
                // movie really loaded - otherwise throw it away and disable this view entirely.
                try
                {
                    _vm = new BossBarCollectionVM();
                    _layer = new GauntletLayer("BLTBossBarLayer", 16, false);
                    var movie = _layer.LoadMovie("BLTBossBar", _vm);
                    if (movie == null)
                    {
                        throw new InvalidOperationException("LoadMovie returned null (BLTBossBar prefab missing?)");
                    }
                    MissionScreen.AddLayer(_layer);
                    _camera = MissionScreen.CombatCamera;
                    _isInitialized = true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[Boss] HP bar UI disabled: {ex.Message}");
                    _layer = null;
                    _vm = null;
                    _uiFailed = true;
                    return;
                }
            }

            var cfg = GlobalCommonConfig.Get();
            foreach (var state in bossBehavior.Bosses)
            {
                if (!_barVMs.TryGetValue(state, out var barVm))
                {
                    barVm = new BossBarVM { Name = state.DisplayName };
                    _vm.Bars.Add(barVm);
                    _barVMs[state] = barVm;
                }

                if (state.Dead || state.Agent == null || !state.Agent.IsActive())
                {
                    barVm.IsVisible = false;
                    continue;
                }

                Vec3 pos = state.Agent.Position;
                pos.z += state.Agent.GetEyeGlobalHeight() + 0.5f;
                float x = 0f, y = 0f, z = 0f;
                MBWindowManager.WorldToScreen(_camera, pos, ref x, ref y, ref z);
                bool onScreen = z > 0f && x > 0f && y > 0f
                    && x < Screen.RealScreenResolutionWidth && y < Screen.RealScreenResolutionHeight;

                if (!onScreen)
                {
                    barVm.IsVisible = false;
                    continue;
                }

                float fraction = MBMath.ClampFloat(state.Agent.Health / Math.Max(1f, state.MaxHealth), 0f, 1f);
                barVm.IsVisible = true;
                // Width comes from config: aussielime_ reported the label being hard to see past
                // when shooting at range, so it is tunable rather than fixed.
                float barWidth = MBMath.ClampFloat(cfg.BossBarWidth, 50f, 250f);
                barVm.PositionX = x - barWidth * 0.5f;
                barVm.PositionY = y - 24f;
                barVm.FillFraction = fraction;
                barVm.BarWidth = barWidth;
                barVm.FillWidth = Math.Max(0f, barWidth * fraction);
                barVm.HealthText = $"{(int)state.Agent.Health} / {(int)state.MaxHealth}";
                barVm.Color = state.Rarity switch
                {
                    BossRarity.Common => cfg.BossCommonBarColor,
                    BossRarity.Epic => cfg.BossEpicBarColor,
                    _ => cfg.BossLegendaryBarColor,
                };
            }

            var toRemove = _barVMs.Keys.Where(s => !bossBehavior.Bosses.Contains(s)).ToList();
            foreach (var s in toRemove)
            {
                _vm.Bars.Remove(_barVMs[s]);
                _barVMs.Remove(s);
            }
        }

        public override void OnRemoveBehavior()
        {
            _barVMs.Clear();
            _vm?.Bars.Clear();
            if (_layer != null && MissionScreen != null)
                MissionScreen.RemoveLayer(_layer);
            _layer = null;
            _vm = null;
            _camera = null;
            base.OnRemoveBehavior();
        }
    }

    public class BossBarCollectionVM : ViewModel
    {
        [DataSourceProperty]
        public MBBindingList<BossBarVM> Bars { get; } = new();
    }

    public class BossBarVM : ViewModel
    {
        private string _name;
        private bool _isVisible;
        private float _positionX;
        private float _positionY;
        private float _fillFraction = 1f;
        private float _barWidth = BossBarMissionView.BarWidthPixels;
        private float _fillWidth = BossBarMissionView.BarWidthPixels;
        private string _healthText = "";
        private string _color = "#FFFFFFF0";

        // Widths are computed in C# and bound as plain pixel values - Gauntlet can't scale a
        // widget from a 0..1 fraction directly, so FillFraction alone would render nothing.
        [DataSourceProperty]
        public float BarWidth
        {
            get => _barWidth;
            set { if (_barWidth != value) { _barWidth = value; OnPropertyChanged(nameof(BarWidth)); } }
        }

        [DataSourceProperty]
        public float FillWidth
        {
            get => _fillWidth;
            set { if (_fillWidth != value) { _fillWidth = value; OnPropertyChanged(nameof(FillWidth)); } }
        }

        // e.g. "3120 / 4000" - the actual numbers, not just the bar
        [DataSourceProperty]
        public string HealthText
        {
            get => _healthText;
            set { if (_healthText != value) { _healthText = value; OnPropertyChanged(nameof(HealthText)); } }
        }

        [DataSourceProperty]
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } }
        }

        [DataSourceProperty]
        public bool IsVisible
        {
            get => _isVisible;
            set { if (_isVisible != value) { _isVisible = value; OnPropertyChanged(nameof(IsVisible)); } }
        }

        [DataSourceProperty]
        public float PositionX
        {
            get => _positionX;
            set { if (_positionX != value) { _positionX = value; OnPropertyChanged(nameof(PositionX)); } }
        }

        [DataSourceProperty]
        public float PositionY
        {
            get => _positionY;
            set { if (_positionY != value) { _positionY = value; OnPropertyChanged(nameof(PositionY)); } }
        }

        // 0..1 - the Gauntlet prefab (BLTBossBar) needs to bind this to the fill bar's width
        [DataSourceProperty]
        public float FillFraction
        {
            get => _fillFraction;
            set { if (_fillFraction != value) { _fillFraction = value; OnPropertyChanged(nameof(FillFraction)); } }
        }

        [DataSourceProperty]
        public string Color
        {
            get => _color;
            set { if (_color != value) { _color = value; OnPropertyChanged(nameof(Color)); } }
        }
    }
}
