using System;
using BannerlordTwitch.Util;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero
{
    public enum BLTAgentSpawnKind
    {
        Hero,
        Retinue,
        EliteRetinue,
        Boss,
    }

    /// <summary>
    /// Public integration point for other mods (e.g. co-op/multiplayer sync).
    /// Raised on the machine running BLT, right after BLT spawns an agent mid-mission,
    /// before any power/boss stat changes are applied to it.
    /// </summary>
    public class BLTAgentSpawnedEventArgs : EventArgs
    {
        public Agent Agent { get; }
        public CharacterObject Troop { get; }
        public BLTAgentSpawnKind Kind { get; }
        /// <summary>The adopted hero this agent belongs to (itself for Hero/Boss, the owner for retinue).</summary>
        public Hero OwnerHero { get; }
        public bool OnPlayerSide { get; }

        public BLTAgentSpawnedEventArgs(Agent agent, CharacterObject troop, BLTAgentSpawnKind kind, Hero ownerHero, bool onPlayerSide)
        {
            Agent = agent;
            Troop = troop;
            Kind = kind;
            OwnerHero = ownerHero;
            OnPlayerSide = onPlayerSide;
        }
    }

    public static class BLTAgentSpawnEvents
    {
        public static event EventHandler<BLTAgentSpawnedEventArgs> AgentSpawned;

        internal static void Raise(Agent agent, CharacterObject troop, BLTAgentSpawnKind kind, Hero ownerHero, bool onPlayerSide)
        {
            var handlers = AgentSpawned;
            if (agent == null || handlers == null) return;

            // Call each subscriber separately so one broken mod can't stop the others or the spawn.
            foreach (EventHandler<BLTAgentSpawnedEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(null, new BLTAgentSpawnedEventArgs(agent, troop, kind, ownerHero, onPlayerSide));
                }
                catch (Exception ex)
                {
                    Log.Error($"[AgentSpawned] Subscriber {handler.Method.DeclaringType?.FullName} threw: {ex.Message}");
                }
            }
        }
    }
}
