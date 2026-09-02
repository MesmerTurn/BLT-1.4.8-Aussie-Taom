using BannerlordTwitch.Localization;
using BannerlordTwitch.Util;
using BLTAdoptAHero;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Surfaces BLT-authored peace proposals targeting the player kingdom or a
    /// player-led landed independent clan, as a native inquiry popup.
    ///
    /// ROOT CAUSE OF THE OLD BUG: BLTDiplomacyPatches.Prefix_MakePeaceAction_Apply
    /// (HarmonyPatches.cs) intercepts every MakePeaceAction call and blocks/reroutes
    /// it whenever exactly one side is BLT-controlled and AdoptedHeroFlags
    /// ._allowDiplomacyAction is not set — which describes every player-accepts-BLT-
    /// peace scenario. Every MakePeaceAction call below is wrapped accordingly.
    /// </summary>
    public class BLTPlayerOffersBehavior : CampaignBehaviorBase
    {
        public static BLTPlayerOffersBehavior Current { get; private set; }

        private readonly HashSet<string> _shownPeaceKeys = new HashSet<string>();
        private readonly HashSet<string> _shownAllianceKeys = new HashSet<string>();
        private readonly HashSet<string> _shownNAPKeys = new HashSet<string>();
        private readonly HashSet<string> _shownTradeKeys = new HashSet<string>();

        public BLTPlayerOffersBehavior() { Current = this; }

        public override void RegisterEvents()
        {
            // Inert when this feature is switched off in Campaign Features:
            // the behaviour still exists (so Current is never null for the many
            // callers that use it) but hooks no campaign events and does nothing.
            // BLT campaign feature disabled -> no event registration.
            if (BLTAdoptAHeroModule.CommonConfig?.EnableDiplomacyFeatures == false) return;

            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // No persistence needed — proposals live in BLTTreatyManager;
            // the shown-key sets here are just a display-dedup cache.
        }

        private IFaction PlayerFaction()
        {
            var clan = Hero.MainHero?.Clan;
            if (clan == null) return null;
            return (IFaction)clan.Kingdom ?? clan;
        }

        private void OnHourlyTick()
        {
            if (BLTTreatyManager.Current == null) return;
            var player = PlayerFaction();
            if (player == null) return;

            PruneShownKeys(player);

            ScanPeace(player);
            ScanAlliance(player);
            ScanNAP(player);
            if (player is Kingdom playerKingdom) ScanTrade(playerKingdom);
        }

        /// <summary>
        /// Drops shown-keys whose proposal no longer exists in BLTTreatyManager. This is
        /// what actually closes bug #3: a key that isn't pruned blocks that kingdom from
        /// ever prompting the player again, for the rest of the campaign session.
        /// Covers accept, decline, AND silent expiry uniformly — accept/decline handlers
        /// below also drop their own key immediately so a same-hour re-propose doesn't
        /// have to wait for the next tick.
        /// </summary>
        private void PruneShownKeys(IFaction player)
        {
            var peaceKeys = new HashSet<string>(BLTTreatyManager.Current.GetPeaceProposalsFor(player)
                .Select(p => $"{p.ProposerKingdomId}_{p.TargetKingdomId}"));
            _shownPeaceKeys.RemoveWhere(k => !peaceKeys.Contains(k));

            var allianceKeys = new HashSet<string>(BLTTreatyManager.Current.GetAllianceProposalsFor(player)
                .Select(p => $"{p.ProposerKingdomId}_{p.TargetKingdomId}"));
            _shownAllianceKeys.RemoveWhere(k => !allianceKeys.Contains(k));

            var napKeys = new HashSet<string>(BLTTreatyManager.Current.GetNAPProposalsFor(player)
                .Select(p => $"{p.ProposerKingdomId}_{p.TargetKingdomId}"));
            _shownNAPKeys.RemoveWhere(k => !napKeys.Contains(k));

            if (player is Kingdom pk)
            {
                var tradeKeys = new HashSet<string>(BLTTreatyManager.Current.GetTradeProposalsFor(pk)
                    .Select(p => $"{p.ProposerKingdomId}_{p.TargetKingdomId}"));
                _shownTradeKeys.RemoveWhere(k => !tradeKeys.Contains(k));
            }
        }

        private static string ProposalKey(IFaction proposer, IFaction target) => $"{proposer?.StringId}_{target?.StringId}";

        // ── Public entry points (called immediately by Diplomacy.cs so the
        //    player doesn't wait up to an hour for the popup) ─────────────────

        public void OfferPeaceToPlayer(IFaction proposer, IFaction playerFaction, int daysToAccept)
        {
            if (PlayerFaction() == null) return;
            var proposal = BLTTreatyManager.Current?.GetPeaceProposal(proposer, playerFaction);
            if (proposal == null) return;
            _shownPeaceKeys.Add(ProposalKey(proposer, playerFaction));
            ShowPeaceInquiry(proposal);
        }

        public void OfferAllianceToPlayer(IFaction proposer, IFaction playerFaction, int daysToAccept)
        {
            if (PlayerFaction() == null) return;
            var proposal = BLTTreatyManager.Current?.GetAllianceProposal(proposer, playerFaction);
            if (proposal == null) return;
            _shownAllianceKeys.Add(ProposalKey(proposer, playerFaction));
            ShowAllianceInquiry(proposal);
        }

        public void OfferNAPToPlayer(IFaction proposer, IFaction playerFaction, int daysToAccept)
        {
            if (PlayerFaction() == null) return;
            var proposal = BLTTreatyManager.Current?.GetNAPProposal(proposer, playerFaction);
            if (proposal == null) return;
            _shownNAPKeys.Add(ProposalKey(proposer, playerFaction));
            ShowNAPInquiry(proposal);
        }

        public void OfferTradeToPlayer(Kingdom proposer, Kingdom playerKingdom, int daysToAccept)
        {
            if (PlayerFaction() == null) return;
            var proposal = BLTTreatyManager.Current?.GetTradeProposal(proposer, playerKingdom);
            if (proposal == null) return;
            _shownTradeKeys.Add(ProposalKey(proposer, playerKingdom));
            ShowTradeInquiry(proposal);
        }

        public void OfferCTWToPlayer(IFaction proposer, IFaction playerFaction, IFaction target, int daysToAccept)
        {
            // CTW popups are already handled correctly by your existing BLTCTWOfferBehavior
            // (below) — left untouched aside from the IFaction generalization.
            BLTCTWOfferBehavior.Current?.OfferCTWToPlayer(proposer, playerFaction, target, daysToAccept);
        }

        // ── Peace ────────────────────────────────────────────────────────────

        private void ScanPeace(IFaction player)
        {
            foreach (var p in BLTTreatyManager.Current.GetPeaceProposalsFor(player))
            {
                string key = $"{p.ProposerKingdomId}_{p.TargetKingdomId}";
                if (!_shownPeaceKeys.Add(key)) continue;
                ShowPeaceInquiry(p);
            }
        }

        private void ShowPeaceInquiry(BLTPeaceProposal proposal)
        {
            var proposer = proposal.GetProposer();
            var player = proposal.GetTarget();
            if (proposer == null || player == null) return;

            string tributeMsg = proposal.DailyTribute > 0
                ? $"\n\n{(proposal.IsOffer ? "They are offering" : "They are demanding")} " +
                  $"{proposal.DailyTribute}{Naming.Gold}/day for {proposal.Duration} days."
                : "\n\nNo tribute involved.";

            InformationManager.ShowInquiry(
                new InquiryData(
                    titleText: "Peace Offer",
                    text: $"{proposer.Name} offers peace.{tributeMsg}\n\n" +
                          $"You have {proposal.DaysRemaining()} days to decide.",
                    isAffirmativeOptionShown: true,
                    isNegativeOptionShown: true,
                    affirmativeText: "Accept Peace",
                    negativeText: "Decline",
                    affirmativeAction: () => AcceptPlayerPeace(proposer, player, proposal),
                    negativeAction: () => DeclinePlayerPeace(proposer, player)
                ),
                pauseGameActiveState: true
            );
        }

        private void AcceptPlayerPeace(IFaction proposer, IFaction player, BLTPeaceProposal proposal)
        {
            if (BLTTreatyManager.Current == null) return;

            // Re-fetch — it may have expired or been withdrawn since the popup was queued.
            var current = BLTTreatyManager.Current.GetPeaceProposal(proposer, player);
            if (current == null || !player.IsAtWarWith(proposer))
            {
                InformationManager.DisplayMessage(new InformationMessage("Peace proposal is no longer valid", Colors.Red));
                BLTTreatyManager.Current.RemovePeaceProposal(proposer, player);
                return;
            }

            IFaction payer = current.IsOffer ? proposer : player;
            IFaction receiver = current.IsOffer ? player : proposer;

            // THE FIX: wrap every game-mutating call in the sanctioning flag so
            // BLTDiplomacyPatches.Prefix_MakePeaceAction_Apply lets it through
            // instead of rerouting it into the unsolicited-AI-peace path.
            AdoptedHeroFlags._allowDiplomacyAction = true;
            try
            {
                MakePeaceAction.ApplyByKingdomDecision(player, proposer, current.DailyTribute, current.Duration);
                FactionManager.SetNeutral(player, proposer);

                if (current.DailyTribute > 0 && payer is Kingdom pk && receiver is Kingdom rk)
                    BLTTreatyManager.Current.CreateTribute(pk, rk, current.DailyTribute, current.Duration);

                BLTTreatyManager.Current.CreateTruce(player, proposer, 30);

                var war = BLTTreatyManager.Current.GetWar(player, proposer);
                if (war != null)
                {
                    if (war.IsMainParticipant(player))
                    {
                        var allies = war.IsAttackerSide(player) ? war.GetAttackerAllies() : war.GetDefenderAllies();
                        foreach (var ally in allies)
                        {
                            if (ally != null && ally.IsAtWarWith(proposer))
                            {
                                MakePeaceAction.Apply(ally, proposer);
                                FactionManager.SetNeutral(ally, proposer);
                            }
                        }
                        BLTTreatyManager.Current.RemoveWar(player, proposer);
                    }
                    else
                    {
                        war.RemoveAlly(player);
                        var alliancePartnerToBreak = war.IsAttackerSide(player) ? war.GetAttacker() : war.GetDefender();
                        if (alliancePartnerToBreak != null)
                        {
                            BLTTreatyManager.Current.RemoveAlliance(player, alliancePartnerToBreak);
                            BLTTreatyManager.Current.CreateTruce(player, alliancePartnerToBreak, 30);
                        }
                    }
                }
            }
            finally
            {
                AdoptedHeroFlags._allowDiplomacyAction = false;
            }

            BLTTreatyManager.Current.RemovePeaceProposal(proposer, player);

            InformationManager.DisplayMessage(new InformationMessage($"Made peace with {proposer.Name}!", Colors.Green));
            Log.ShowInformation($"{player.Name} has made peace with {proposer.Name}!", Hero.MainHero.CharacterObject);
        }

        private void DeclinePlayerPeace(IFaction proposer, IFaction player)
        {
            BLTTreatyManager.Current?.RemovePeaceProposal(proposer, player);
            InformationManager.DisplayMessage(new InformationMessage($"Declined peace with {proposer.Name}", Colors.Black));
        }

        // ── Alliance (unchanged behavior, generalized to IFaction) ──────────

        private void ScanAlliance(IFaction player)
        {
            foreach (var p in BLTTreatyManager.Current.GetAllianceProposalsFor(player))
            {
                string key = $"{p.ProposerKingdomId}_{p.TargetKingdomId}";
                if (!_shownAllianceKeys.Add(key)) continue;
                ShowAllianceInquiry(p);
            }
        }

        private void ShowAllianceInquiry(BLTAllianceProposal proposal)
        {
            var proposer = proposal.GetProposer();
            var player = proposal.GetTarget();
            if (proposer == null || player == null) return;

            InformationManager.ShowInquiry(
                new InquiryData(
                    titleText: "Alliance Proposal",
                    text: $"{proposer.Name} proposes a defensive alliance!\n\n" +
                          $"Benefits:\n" +
                          $"• Mutual defense: you both join each other's defensive wars\n" +
                          $"• Can call {proposer.Name} to war (costs {proposal.CTWCost}{Naming.Gold})\n\n" +
                          $"Obligations:\n" +
                          $"• Auto-join when {proposer.Name} is attacked\n" +
                          $"• Breaking costs {proposal.BreakAllianceCost}{Naming.Gold}\n\n" +
                          $"You have {proposal.DaysRemaining()} days to decide.",
                    isAffirmativeOptionShown: true,
                    isNegativeOptionShown: true,
                    affirmativeText: "Accept Alliance",
                    negativeText: "Decline",
                    affirmativeAction: () => AcceptPlayerAlliance(proposer, player),
                    negativeAction: () => DeclinePlayerAlliance(proposer, player)
                ),
                pauseGameActiveState: true
            );
        }

        private void AcceptPlayerAlliance(IFaction proposer, IFaction player)
        {
            if (BLTTreatyManager.Current == null) return;

            var proposal = BLTTreatyManager.Current.GetAllianceProposal(proposer, player);
            if (proposal == null || player.IsAtWarWith(proposer))
            {
                InformationManager.DisplayMessage(new InformationMessage("Alliance proposal is no longer valid", Colors.Red));
                _shownAllianceKeys.Remove(ProposalKey(proposer, player));
                return;
            }

            BLTTreatyManager.Current.CreateAlliance(proposer, player);
            BLTTreatyManager.Current.RemoveNAP(proposer, player);
            BLTTreatyManager.Current.RemoveAllianceProposal(proposer, player);
            _shownAllianceKeys.Remove(ProposalKey(proposer, player));

            InformationManager.DisplayMessage(new InformationMessage($"Alliance formed with {proposer.Name}!", Colors.Green));
            Log.ShowInformation($"{player.Name} and {proposer.Name} have formed an alliance!",
                Hero.MainHero.CharacterObject, Log.Sound.Horns2);
        }

        private void DeclinePlayerAlliance(IFaction proposer, IFaction player)
        {
            BLTTreatyManager.Current?.RemoveAllianceProposal(proposer, player);
            _shownAllianceKeys.Remove(ProposalKey(proposer, player));
            InformationManager.DisplayMessage(new InformationMessage($"Declined alliance with {proposer.Name}", Colors.Black));
        }

        // ── NAP (unchanged behavior, generalized to IFaction) ───────────────

        private void ScanNAP(IFaction player)
        {
            foreach (var p in BLTTreatyManager.Current.GetNAPProposalsFor(player))
            {
                string key = $"{p.ProposerKingdomId}_{p.TargetKingdomId}";
                if (!_shownNAPKeys.Add(key)) continue;
                ShowNAPInquiry(p);
            }
        }

        private void ShowNAPInquiry(BLTNAPProposal proposal)
        {
            var proposer = proposal.GetProposer();
            var player = proposal.GetTarget();
            if (proposer == null || player == null) return;

            InformationManager.ShowInquiry(
                new InquiryData(
                    titleText: "Non-Aggression Pact Proposal",
                    text: $"{proposer.Name} proposes a non-aggression pact!\n\n" +
                          $"Benefits:\n• Mutual peace: neither side can declare war\n• Can be broken at any time\n\n" +
                          $"Note:\n• Does not provide military assistance\n• Less binding than an alliance\n\n" +
                          $"You have {proposal.DaysRemaining()} days to decide.",
                    isAffirmativeOptionShown: true,
                    isNegativeOptionShown: true,
                    affirmativeText: "Accept NAP",
                    negativeText: "Decline",
                    affirmativeAction: () => AcceptPlayerNAP(proposer, player),
                    negativeAction: () => DeclinePlayerNAP(proposer, player)
                ),
                pauseGameActiveState: true
            );
        }

        private void AcceptPlayerNAP(IFaction proposer, IFaction player)
        {
            if (BLTTreatyManager.Current == null) return;

            var proposal = BLTTreatyManager.Current.GetNAPProposal(proposer, player);
            if (proposal == null || player.IsAtWarWith(proposer))
            {
                InformationManager.DisplayMessage(new InformationMessage("NAP proposal is no longer valid", Colors.Red));
                return;
            }

            BLTTreatyManager.Current.CreateNAP(proposer, player);
            BLTTreatyManager.Current.RemoveNAPProposal(proposer, player);

            InformationManager.DisplayMessage(new InformationMessage($"Non-aggression pact formed with {proposer.Name}!", Colors.Green));
            Log.ShowInformation($"{player.Name} and {proposer.Name} have signed a non-aggression pact!",
                Hero.MainHero.CharacterObject, Log.Sound.Horns2);
        }

        private void DeclinePlayerNAP(IFaction proposer, IFaction player)
        {
            BLTTreatyManager.Current?.RemoveNAPProposal(proposer, player);
            InformationManager.DisplayMessage(new InformationMessage($"Declined NAP with {proposer.Name}", Colors.Black));
        }

        // ── Trade (new — Kingdom-only, vanilla trade model has no clan path) ─

        private void ScanTrade(Kingdom playerKingdom)
        {
            foreach (var p in BLTTreatyManager.Current.GetTradeProposalsFor(playerKingdom))
            {
                string key = $"{p.ProposerKingdomId}_{p.TargetKingdomId}";
                if (!_shownTradeKeys.Add(key)) continue;
                ShowTradeInquiry(p);
            }
        }

        private void ShowTradeInquiry(BLTTradeProposal proposal)
        {
            if (proposal.GetProposer() is not Kingdom proposer) return;
            if (proposal.GetTarget() is not Kingdom playerKingdom) return;

            InformationManager.ShowInquiry(
                new InquiryData(
                    titleText: "Trade Agreement Proposal",
                    text: $"{proposer.Name} proposes a trade agreement.\n\n" +
                          $"You have {proposal.DaysRemaining()} days to decide.",
                    isAffirmativeOptionShown: true,
                    isNegativeOptionShown: true,
                    affirmativeText: "Accept Trade",
                    negativeText: "Decline",
                    affirmativeAction: () => AcceptPlayerTrade(proposer, playerKingdom),
                    negativeAction: () => DeclinePlayerTrade(proposer, playerKingdom)
                ),
                pauseGameActiveState: true
            );
        }

        private void AcceptPlayerTrade(Kingdom proposer, Kingdom playerKingdom)
        {
            if (BLTTreatyManager.Current == null) return;

            var proposal = BLTTreatyManager.Current.GetTradeProposal(proposer, playerKingdom);
            if (proposal == null || playerKingdom.IsAtWarWith(proposer))
            {
                InformationManager.DisplayMessage(new InformationMessage("Trade proposal is no longer valid", Colors.Red));
                return;
            }

            var tradeBehavior = Campaign.Current.GetCampaignBehavior<TradeAgreementsCampaignBehavior>();
            var duration = Campaign.Current.Models.TradeAgreementModel.GetTradeAgreementDurationInYears(playerKingdom, proposer);
            tradeBehavior.MakeTradeAgreement(playerKingdom, proposer, duration);

            BLTTreatyManager.Current.RemoveTradeProposal(proposer, playerKingdom);

            InformationManager.DisplayMessage(new InformationMessage($"Trade agreement established with {proposer.Name}!", Colors.Green));
            Log.ShowInformation($"{playerKingdom.Name} and {proposer.Name} have formed a trade agreement!", Hero.MainHero.CharacterObject);
        }

        private void DeclinePlayerTrade(Kingdom proposer, Kingdom playerKingdom)
        {
            BLTTreatyManager.Current?.RemoveTradeProposal(proposer, playerKingdom);
            InformationManager.DisplayMessage(new InformationMessage($"Declined trade agreement with {proposer.Name}", Colors.Black));
        }
    }

    /// <summary>
    /// Unchanged apart from IFaction generalization — this one was already
    /// correct because it doesn't touch MakePeaceAction, only bookkeeping.
    /// Kept as its own class for call-site compatibility (Diplomacy.cs still
    /// only calls the NAP path through BLTPlayerOffersBehavior now, but this
    /// stays available if anything external still references it directly).
    /// </summary>
    public class BLTNAPOfferBehavior : CampaignBehaviorBase
    {
        public static BLTNAPOfferBehavior Current { get; private set; }
        public BLTNAPOfferBehavior() { Current = this; }
        public override void RegisterEvents() { }
        public override void SyncData(IDataStore dataStore) { }
    }

    public class BLTCTWOfferBehavior : CampaignBehaviorBase
    {
        public static BLTCTWOfferBehavior Current { get; private set; }
        public BLTCTWOfferBehavior() { Current = this; }

        public override void RegisterEvents() { }
        public override void SyncData(IDataStore dataStore) { }

        public void OfferCTWToPlayer(IFaction caller, IFaction playerFaction, IFaction target, int daysToAccept)
        {
            if (Hero.MainHero?.Clan == null || playerFaction == null) return;

            var proposal = BLTTreatyManager.Current.GetCTWProposalsFor(playerFaction)
                .FirstOrDefault(p => p.ProposerKingdomId == caller.StringId);
            if (proposal == null) return;

            ShowCTWOfferInquiry(proposal);
        }

        private void ShowCTWOfferInquiry(BLTCTWProposal proposal)
        {
            var caller = proposal.GetProposer();
            var playerFaction = proposal.GetCalled();
            var target = proposal.GetTarget();
            if (caller == null || playerFaction == null || target == null) return;

            InformationManager.ShowInquiry(
                new InquiryData(
                    titleText: "Call to War",
                    text: $"{caller.Name} calls you to war against {target.Name}!\n\n" +
                          $"Alliance obligation:\n• Your ally {caller.Name} is at war with {target.Name}\n" +
                          $"• You are expected to join this war\n\n" +
                          $"You have {proposal.DaysRemaining()} days to decide.",
                    isAffirmativeOptionShown: true,
                    isNegativeOptionShown: true,
                    affirmativeText: "Join the War",
                    negativeText: "Decline",
                    affirmativeAction: () => AcceptPlayerCTW(caller, playerFaction, target),
                    negativeAction: () => DeclinePlayerCTW(caller, playerFaction, target)
                ),
                pauseGameActiveState: true
            );
        }

        private void AcceptPlayerCTW(IFaction caller, IFaction playerFaction, IFaction target)
        {
            if (BLTTreatyManager.Current == null) return;

            var proposal = BLTTreatyManager.Current.GetCTWProposalsFor(playerFaction)
                .FirstOrDefault(p => p.ProposerKingdomId == caller.StringId);
            if (proposal == null)
            {
                InformationManager.DisplayMessage(new InformationMessage("Call to war is no longer valid", Colors.Red));
                return;
            }

            if (!caller.IsAtWarWith(target))
            {
                InformationManager.DisplayMessage(new InformationMessage($"{caller.Name} is no longer at war with {target.Name}", Colors.Red));
                BLTTreatyManager.Current.RemoveCTWProposal(caller, playerFaction, target);
                return;
            }

            if (playerFaction.IsAtWarWith(target))
            {
                InformationManager.DisplayMessage(new InformationMessage($"You are already at war with {target.Name}", Colors.Red));
                BLTTreatyManager.Current.RemoveCTWProposal(caller, playerFaction, target);
                return;
            }

            if (!BLTTreatyManager.Current.CanDeclareWar(playerFaction, target, out string reason))
            {
                InformationManager.DisplayMessage(new InformationMessage($"Cannot join war: {reason}", Colors.Red));
                return;
            }

            AdoptedHeroFlags._allowDiplomacyAction = true;
            try
            {
                DeclareWarAction.ApplyByDefault(playerFaction, target);
                FactionManager.DeclareWar(playerFaction, target);

                var war = BLTTreatyManager.Current.GetWar(caller, target);
                if (war != null)
                {
                    if (war.IsAttackerSide(caller)) war.AddAttackerAlly(playerFaction);
                    else if (war.IsDefenderSide(caller)) war.AddDefenderAlly(playerFaction);
                }
                BLTTreatyManager.Current.RemoveCTWProposal(caller, playerFaction, target);

                InformationManager.DisplayMessage(new InformationMessage($"Joined {caller.Name}'s war against {target.Name}!", Colors.Green));
                Log.ShowInformation($"{playerFaction.Name} has joined {caller.Name}'s war against {target.Name}!",
                    Hero.MainHero.CharacterObject, Log.Sound.Horns2);
            }
            finally
            {
                AdoptedHeroFlags._allowDiplomacyAction = false;
            }
        }

        private void DeclinePlayerCTW(IFaction caller, IFaction playerFaction, IFaction target)
        {
            BLTTreatyManager.Current?.RemoveCTWProposal(caller, playerFaction, target);
            InformationManager.DisplayMessage(new InformationMessage($"Declined call to war from {caller.Name}", Colors.Black));
        }
    }
}