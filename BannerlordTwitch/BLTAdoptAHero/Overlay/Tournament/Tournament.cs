using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using System.IO;
using System.Threading.Tasks;
using BannerlordTwitch.Util;
using Microsoft.AspNet.SignalR;

namespace BLTAdoptAHero
{
    public class TournamentHub : Hub
    {
        private static string GetContentPath(string fileName) => Path.Combine(
            Path.GetDirectoryName(typeof(TournamentHub).Assembly.Location) ?? ".",
            "Overlay", "Tournament", fileName);
        private static string GetContent(string fileName) => File.ReadAllText(GetContentPath(fileName));

        public static void Register()
        {
            BLTOverlay.BLTOverlay.Register("tournament", 100,
                GetContent("Tournament.css"),
                GetContent("Tournament.html"),
                GetContent("Tournament.js"));
        }

        public override Task OnConnected()
        {
            Refresh();
            return base.OnConnected();
        }

        public void Refresh()
        {
            // The overlay can poll at any moment - main menu, mid load, campaign teardown.
            // A null check on Campaign.Current isn't enough: it can be non-null while the
            // behaviour collection is still being built, and GetCampaignBehavior throws then.
            // The overlay is cosmetic, so never let it surface an error - just show nothing.
            try
            {
                RefreshImpl();
            }
            catch (Exception ex)
            {
                Log.Trace($"[Overlay] Tournament refresh skipped: {ex.Message}");
                try
                {
                    Clients.Caller.updateEntrants(0, 0);
                    Clients.Caller.updateBets(new List<int>());
                    Clients.Caller.UpdateBettingState(string.Empty);
                }
                catch
                {
                    // client went away mid-refresh, nothing to do
                }
            }
        }

        private void RefreshImpl()
        {
            Clients.Caller.setLabels(new
            {
                Tournament = "{=PI83uB8j}Tournament".Translate(),
                BettingIsOpen = "{=WPTU6AGn}Betting is OPEN".Translate(),
                BettingIsClosed = "{=PLRsZCjL}Betting is CLOSED".Translate(),
                NotTakingBets = "{=Sv04YKsL}Not taking bets".Translate(),
            });
            // The overlay (OBS browser source) can connect while sitting in the main menu,
            // where there is no campaign at all. The Current accessors below go through
            // Campaign.Current.GetCampaignBehavior<T>(), which throws in that state - the
            // null-conditional doesn't help because the throw happens inside the getter.
            if (Campaign.Current == null)
            {
                Clients.Caller.updateEntrants(0, 0);
                Clients.Caller.updateBets(new List<int>());
                Clients.Caller.UpdateBettingState(string.Empty);
                return;
            }

            (int entrants, int tournamentSize) = BLTTournamentQueueBehavior.Current?.GetTournamentQueueSize() ?? (0, 0);
            Clients.Caller.updateEntrants(entrants, tournamentSize);
            Clients.Caller.updateBets(BLTTournamentBetMissionBehavior.Current?.GetTotalBets() ?? new List<int>());
            Clients.Caller.UpdateBettingState(BLTTournamentBetMissionBehavior.Current?.CurrentBettingState.ToString() ?? string.Empty);
        }

        public static void Reset()
        {
            GlobalHost.ConnectionManager.GetHubContext<TournamentHub>()
                .Clients.All.reset();
        }

        public static void UpdateEntrants()
        {
            (int entrants, int tournamentSize) = BLTTournamentQueueBehavior.Current?.GetTournamentQueueSize() ?? (0, 0);
            GlobalHost.ConnectionManager.GetHubContext<TournamentHub>()
                .Clients.All.updateEntrants(entrants, tournamentSize);
        }

        public static void UpdateBets()
        {
            GlobalHost.ConnectionManager.GetHubContext<TournamentHub>()
                .Clients.All.updateBets(BLTTournamentBetMissionBehavior.Current?.GetTotalBets() ?? new List<int>());
            GlobalHost.ConnectionManager.GetHubContext<TournamentHub>()
                .Clients.All.updateBettingState(BLTTournamentBetMissionBehavior.Current?.CurrentBettingState.ToString() ?? "none");
        }
    }
}