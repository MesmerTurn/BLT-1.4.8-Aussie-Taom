using System;
using BannerlordTwitch;
using BannerlordTwitch.Rewards;
using TaleWorlds.CampaignSystem;

namespace BLTAdoptAHero
{
    public abstract class HeroActionHandlerBase : ActionHandlerBase
    {
        protected override void ExecuteInternal(ReplyContext context, object config, Action<string> onSuccess, Action<string> onFailure)
        {
            // Keep the viewer's roles current here too - rewards and channel-point actions
            // come through this base rather than HeroCommandHandlerBase.
            ViewerRoles.Update(context);
            var adoptedHero = BLTAdoptAHeroCampaignBehavior.Current.GetAdoptedHero(context.UserName);
            BLTAdoptAHeroCampaignBehavior.RefreshAdoptedName(adoptedHero, context.UserName);

            if (adoptedHero == null)
            {
                onFailure(AdoptAHero.NoHeroMessage);
                return;
            }
            ExecuteInternal(adoptedHero, context, config, onSuccess, onFailure);
        }

        protected abstract void ExecuteInternal(Hero adoptedHero, ReplyContext context, object config,
            Action<string> onSuccess, Action<string> onFailure);
    }
}