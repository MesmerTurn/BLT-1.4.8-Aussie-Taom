using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BannerlordTwitch.Util;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.AgentOrigins;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace BLTAdoptAHero.Coop
{
    /// <summary>
    /// Carries BLT's own spawns between two BLT instances over <see cref="CoopLink"/>.
    ///
    /// Host: every agent BLT spawns is announced with its troop, team, position and facing, and the
    /// full list is repeated every few seconds as a repair tick.
    /// Guest: replays those spawns locally, keyed by spawn id so a repeated announcement can never
    /// spawn the same hero twice - which is the failure mode that matters, since a duplicated hero
    /// is worse than a missing one.
    ///
    /// Everything is best effort. A co-op session that desyncs is a nuisance; a mission behavior
    /// that throws takes the battle down, so every entry point swallows its exceptions.
    /// </summary>
    public class CoopSpawnRelayBehavior : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private const float RosterInterval = 5f;

        private static CoopSpawnRelayBehavior current;

        private readonly Dictionary<string, PendingSpawn> hostSpawns = new();
        private readonly HashSet<string> guestApplied = new();
        private float nextRoster = RosterInterval;
        private int spawnCounter;

        private class PendingSpawn
        {
            public string Line;
            public Agent Agent;
        }

        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            current = this;

            if (CoopLink.IsHost)
            {
                CoopLink.Send("MISSION|START");
            }
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();

            if (CoopLink.IsHost) CoopLink.Send("MISSION|END");

            hostSpawns.Clear();
            guestApplied.Clear();
            current = null;
        }

        /// <summary>
        /// Called from BLTSummonBehavior/boss spawns via the public AgentSpawned event. Position is
        /// read here rather than at announce time because an agent that has only just been built
        /// may not have been placed yet on the very first frame.
        /// </summary>
        public static void AnnounceHostSpawn(Agent agent, CharacterObject troop, BLTAgentSpawnKind kind, bool onPlayerSide)
        {
            if (!CoopLink.IsHost || agent == null || troop == null) return;

            var self = current;
            if (self == null) return;

            try
            {
                string id = $"{Mission.Current?.CurrentTime:0.000}-{++self.spawnCounter}";
                var pos = agent.Position;
                var dir = agent.GetMovementDirection();

                string line = string.Join("|", new[]
                {
                    "SPAWN",
                    kind.ToString(),
                    troop.StringId ?? "?",
                    Sanitize(agent.Name),
                    onPlayerSide ? "1" : "0",
                    F(pos.x), F(pos.y), F(pos.z),
                    F(dir.x), F(dir.y),
                    agent.HasMount ? "1" : "0",
                    id,
                });

                self.hostSpawns[id] = new PendingSpawn { Line = line, Agent = agent };
                CoopLink.Send(line);
            }
            catch (Exception ex)
            {
                Log.Trace($"[CoopLink] Announce failed: {ex.Message}");
            }
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);

            try
            {
                if (CoopLink.IsGuest) DrainInbox();

                if (!CoopLink.IsHost) return;

                nextRoster -= dt;
                if (nextRoster > 0f) return;
                nextRoster = RosterInterval;

                // Drop entries whose agent is gone so the repair tick doesn't resurrect the dead.
                foreach (string dead in hostSpawns
                             .Where(kv => kv.Value.Agent == null || !kv.Value.Agent.IsActive())
                             .Select(kv => kv.Key).ToList())
                {
                    hostSpawns.Remove(dead);
                }

                foreach (var pending in hostSpawns.Values)
                {
                    CoopLink.Send(pending.Line);
                }
            }
            catch (Exception ex)
            {
                Log.Trace($"[CoopLink] Tick failed: {ex.Message}");
                nextRoster = 30f;
            }
        }

        private void DrainInbox()
        {
            while (CoopLink.TryDequeue(out string line))
            {
                try
                {
                    HandleLine(line);
                }
                catch (Exception ex)
                {
                    Log.Trace($"[CoopLink] Bad line '{line}': {ex.Message}");
                }
            }
        }

        private void HandleLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            string[] p = line.Split('|');
            switch (p[0])
            {
                case "HELLO":
                    Log.LogFeedSystem($"[BLT] Co-op link: host protocol v{(p.Length > 1 ? p[1] : "?")}");
                    return;

                case "MISSION":
                    // A new battle on the host: forget what we replayed in the previous one.
                    if (p.Length > 1 && p[1] == "START") guestApplied.Clear();
                    return;

                case "SPAWN":
                    ApplyGuestSpawn(p);
                    return;
            }
        }

        private void ApplyGuestSpawn(string[] p)
        {
            if (p.Length < 12) return;

            string id = p[11];
            if (!guestApplied.Add(id)) return; // Already replayed - this is a repair-tick repeat.

            var mission = Mission.Current;
            if (mission == null || mission.CurrentState != Mission.State.Continuing)
            {
                // Not in a battle yet; let the repair tick bring it round again.
                guestApplied.Remove(id);
                return;
            }

            string charId = p[2];
            var troop = MBObjectManager.Instance?.GetObject<CharacterObject>(charId);
            if (troop == null)
            {
                Log.Trace($"[CoopLink] Unknown troop '{charId}' - the two installs may differ");
                return;
            }

            bool onPlayerSide = p[4] == "1";
            bool mounted = p[10] == "1";

            var position = new Vec3(P(p[5]), P(p[6]), P(p[7]));
            var direction = new Vec2(P(p[8]), P(p[9]));

            var party = onPlayerSide
                ? PartyBase.MainParty
                : mission.PlayerEnemyTeam?.ActiveAgents?
                      .Select(a => (a.Origin as PartyAgentOrigin)?.Party)
                      .FirstOrDefault(x => x != null)
                  ?? PartyBase.MainParty;

            try
            {
                var agent = mission.SpawnTroop(
                    new PartyAgentOrigin(party, troop),
                    isPlayerSide: onPlayerSide,
                    hasFormation: true,
                    spawnWithHorse: mounted,
                    isReinforcement: true,
                    formationTroopCount: 1,
                    formationTroopIndex: 0,
                    isAlarmed: true,
                    wieldInitialWeapons: true,
                    initialPosition: position,
                    initialDirection: direction);

                if (agent == null)
                {
                    guestApplied.Remove(id);
                    return;
                }

                agent.MountAgent?.FadeIn();
                agent.FadeIn();

                Log.Trace($"[CoopLink] Replayed {p[1]} '{p[3]}' ({charId}) on "
                          + $"{(onPlayerSide ? "player" : "enemy")} side");
            }
            catch (Exception ex)
            {
                // Let the repair tick try again rather than losing the hero for the whole battle.
                guestApplied.Remove(id);
                Log.Trace($"[CoopLink] Replay failed for '{charId}': {ex.Message}");
            }
        }

        private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static float P(string value)
            => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

        private static string Sanitize(string name)
            => string.IsNullOrEmpty(name) ? "?" : name.Replace("|", "/").Replace("\n", " ");
    }
}
