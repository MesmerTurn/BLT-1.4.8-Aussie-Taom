using System;
using BannerlordTwitch;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace BLTAdoptAHero
{
    // What the viewer's summoned hero should preferentially engage. This only sets a bias
    // BLTSummonBehavior's tick logic acts on - it never overrides direct-threat combat, and it
    // does nothing at all unless the hero is currently a live agent in a mission (from !summon).
    public enum FocusTargetType
    {
        Off,
        Melee,
        Ranged,
    }

    [LocDisplayName("{=}Focus Target"),
     LocDescription("{=}Sets what type of enemy the viewer's summoned hero preferentially moves toward and engages during a battle - a strong preference, not a hard override. The hero still reacts normally to whoever is actually attacking them right now; this only steers who they go looking for once they're not directly threatened. Configure one command per target type (e.g. !focusmelee, !focusarchers, !focusoff)."),
     UsedImplicitly]
    internal class FocusCommand : HeroActionHandlerBase
    {
        private class Settings
        {
            [LocDisplayName("{=}Target Type"),
             LocDescription("{=}Which enemy type this command switches the hero to focus on. Use 'Off' for a command that cancels focus and returns to normal AI behaviour."),
             PropertyOrder(1), UsedImplicitly]
            public FocusTargetType TargetType { get; set; } = FocusTargetType.Melee;
        }

        protected override Type ConfigType => typeof(Settings);

        protected override void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure)
        {
            var settings = (Settings)config;

            var summonState = BLTSummonBehavior.Current?.GetHeroSummonState(adoptedHero);
            if (summonState?.CurrentAgent == null || !summonState.CurrentAgent.IsActive())
            {
                onFailure("{=}You need to be summoned into the current battle first (!summon) before you can set a focus target.".Translate());
                return;
            }

            summonState.FocusTarget = settings.TargetType;

            onSuccess(settings.TargetType switch
            {
                FocusTargetType.Off => "{=}Focus cleared - back to normal behaviour.".Translate(),
                FocusTargetType.Melee => "{=}Now focusing on melee enemies.".Translate(),
                FocusTargetType.Ranged => "{=}Now focusing on ranged/archer enemies.".Translate(),
                _ => "{=}Focus updated.".Translate(),
            });
        }
    }
}
