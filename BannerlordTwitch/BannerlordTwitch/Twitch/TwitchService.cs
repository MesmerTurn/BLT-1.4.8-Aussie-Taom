using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BannerlordTwitch.Dummy;
using BannerlordTwitch.Extension;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Rewards;
using BannerlordTwitch.Testing;
using BannerlordTwitch.Twitch;
using BannerlordTwitch.Util;
using BLTOverlay;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using TwitchLib.Api;
using TwitchLib.Api.Core.Enums;
using TwitchLib.Api.Core.Exceptions;
using TwitchLib.Api.Helix.Models.ChannelPoints.GetCustomReward;
using TwitchLib.Api.Helix.Models.ChannelPoints.UpdateCustomRewardRedemptionStatus;
using TwitchLib.Api.Interfaces;
using TwitchLib.Client.Models;
using TwitchLib.EventSub.Core.SubscriptionTypes.Channel;
using TwitchLib.EventSub.Websockets;
using TwitchLib.EventSub.Websockets.Client;
using TwitchLib.EventSub.Websockets.Core.EventArgs;
using TwitchLib.EventSub.Websockets.Core.EventArgs.Channel;
using TwitchLib.PubSub;
using TwitchLib.PubSub.Events;
using TwitchLib.PubSub.Models.Responses.Messages.Redemption;

namespace BannerlordTwitch
{
    public class ReplyContext
    {
        [UsedImplicitly] public string UserName { get; private set; }
        [UsedImplicitly] public string ReplyId { get; private set; }
        [UsedImplicitly] public string Args { get; private set; }
        [UsedImplicitly] public int Bits { get; private set; }
        [UsedImplicitly] public double BitsInDollars { get; private set; }
        [UsedImplicitly] public int SubscribedMonthCount { get; private set; }
        [UsedImplicitly] public bool IsBroadcaster { get; private set; }
        [UsedImplicitly] public bool IsModerator { get; private set; }
        [UsedImplicitly] public bool IsSubscriber { get; private set; }
        [UsedImplicitly] public bool IsVip { get; private set; }
        [UsedImplicitly] public string RedemptionId { get; private set; }
        [UsedImplicitly] public ActionBase Source { get; private set; }

        public string ArgsErrorMessage(string args)
        {
            if (Source is Command cmd)
            {
                return "{=JSW1ryNl}Usage: !{Name} {Args}".Translate(("Name", cmd.Name), ("Args", args));
            }
            else
            {
                return "{=mdhbHYNM}Usage: {Args}".Translate(("Args", args));
            }
        }

        private static string CleanDisplayName(string str) => str.Replace(" ", "").Replace(@"\s", "");

        public static ReplyContext FromMessage(ActionBase source, ChatMessage msg, string args) =>
            new()
            {
                UserName = CleanDisplayName(msg.DisplayName),
                ReplyId = msg.Id,
                Args = args,
                Bits = msg.Bits,
                BitsInDollars = msg.BitsInDollars,
                SubscribedMonthCount = msg.SubscribedMonthCount,
                IsBroadcaster = msg.IsBroadcaster,
                IsModerator = msg.IsModerator,
                IsSubscriber = msg.IsSubscriber,
                IsVip = msg.IsVip,
                Source = source,
            };

        public static ReplyContext FromRedemption(ActionBase source, ChannelPointsCustomRewardRedemption redemption) =>
            new()
            {
                UserName = CleanDisplayName(redemption.UserName),
                Args = redemption.UserInput,
                RedemptionId = redemption.Id,
                Source = source,
            };

        public static ReplyContext FromUser(ActionBase source, string userName, string args = null) =>
            new()
            {
                UserName = CleanDisplayName(userName),
                Args = args,
                Source = source,
            };

        public static ReplyContext FromOverlay(
            ActionBase source,
            string userName,
            string args = null) =>
            new()
            {
                UserName = CleanDisplayName(userName),
                Args = args,
                Source = source,
                IsModerator = true,      // ← IMPORTANT (see below)
                IsBroadcaster = true     // overlay = trusted
            };
    }

    // https://twitchtokengenerator.com/
    // https://twitchtokengenerator.com/quick/AAYotwZPvU
    internal partial class TwitchService : IDisposable
    {
        private TwitchEventSubSocket eventsub;
        private readonly TwitchAPI api;
        public string channelId;
        public readonly AuthSettings authSettings;

        private TwitchPubSub pubSub;

        private readonly Settings settings;
        private CancellationToken token;

        private readonly ConcurrentDictionary<string, ChannelPointsCustomRewardRedemption> redemptionCache = new();
        private Bot bot;

        // ── Extension PubSub ─────────────────────────────────────────────────
        private ExtensionPubSubService extensionPubSub;
        private LocalRelayService localRelay;

        // TwitchAPI's constructor takes an ILoggerFactory, which lives in
        // Microsoft.Extensions.Logging.Abstractions. TwitchLib.Api was built against v5.0.0.0
        // of that assembly, we build against v9.0.0.0, and TAOM.Dependencies bundles v2.0.0.0
        // and installs an AssemblyResolve hook that hands back whatever is already loaded.
        // Whenever the version TwitchAPI's ctor signature resolves to at runtime differs from
        // the one baked into our IL at compile time, the CLR can't match the ctor and throws
        // MissingMethodException - and because the JIT compiles a whole method before running
        // any of it, that killed this entire constructor before its first line executed.
        //
        // Building the instance reflectively removes the compile-time signature from our IL
        // altogether: we find whatever 'http' parameter the ctor actually has at runtime and
        // pass null for the rest, so no type identity has to match for the other parameters.
        private static TwitchAPI CreateTwitchApi(object http)
        {
            ConstructorInfo ctor = typeof(TwitchAPI).GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault(c => c.GetParameters().Any(p => p.Name == "http"));

            if (ctor == null)
            {
                throw new Exception(
                    "Could not find a usable TwitchAPI constructor - TwitchLib.Api.dll may be missing or the wrong version.");
            }

            object[] args = ctor.GetParameters()
                .Select(p => p.Name == "http" ? http : null)
                .ToArray();

            return (TwitchAPI)ctor.Invoke(args);
        }

        internal class TwitchUserInfo
        {
            public string Id;
            public string Login;
            public string BroadcasterType;
        }

        // A ladder of progressively higher level network operations against a host that has
        // nothing to do with Twitch. Each rung is written to disk before it runs, so whichever
        // line the log ends on names the exact network primitive that kills the process.
        // Established so far: any outbound HTTPS request is fatal, on any thread, regardless of
        // Twitch - so the question now is whether plain sockets work and only TLS is broken.
        private static async Task ProbeHttpsAsync()
        {
            try
            {
                BLTModule.Trace("PROBE 1: DNS resolve example.com");
                var addresses = System.Net.Dns.GetHostAddresses("example.com");
                BLTModule.Trace($"PROBE 1 OK: resolved to {addresses.FirstOrDefault()}");

                BLTModule.Trace("PROBE 2: raw TCP connect to example.com:80");
                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    await tcp.ConnectAsync("example.com", 80);
                    BLTModule.Trace($"PROBE 2 OK: connected={tcp.Connected}");
                }

                BLTModule.Trace("PROBE 3: raw TCP connect to example.com:443");
                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    await tcp.ConnectAsync("example.com", 443);
                    BLTModule.Trace($"PROBE 3 OK: connected={tcp.Connected}");
                }

                BLTModule.Trace("PROBE 4: plain HTTP GET http://example.com");
                using (var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var r = await plain.GetAsync("http://example.com");
                    BLTModule.Trace($"PROBE 4 OK: status {(int)r.StatusCode}");
                }

                BLTModule.Trace("PROBE 5: HTTPS GET https://example.com (TLS handshake)");
                using (var secure = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var r = await secure.GetAsync("https://example.com");
                    BLTModule.Trace($"PROBE 5 OK: status {(int)r.StatusCode}");
                }

                BLTModule.Trace("ALL PROBES PASSED - outbound networking is healthy");
            }
            catch (Exception ex)
            {
                BLTModule.Trace($"PROBE FAILED (managed exception) {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Deliberately NOT api.Helix.Users.GetUsersAsync().
        //
        // TwitchLib.Api.Helix binds to TAOM.Dependencies' Microsoft.Extensions.* v2.0.0.0,
        // while BLT binds to its own v9.0.0.0 - both versions end up loaded in the process at
        // once (confirmed in the assembly load trace). The first real Helix call then executes
        // across that split identity and kills the process with a native access violation in
        // clr.dll: no managed exception, no crash dialog, nothing written to any log.
        //
        // We only need three fields, so ask Twitch over plain HTTP and skip the broken layer
        // entirely. HttpClient and Newtonsoft are unaffected by the version conflict.
        private static async Task<TwitchUserInfo> GetCurrentUserAsync(string clientId, string accessToken)
        {
            string token = (accessToken ?? string.Empty).Replace("oauth:", string.Empty);

            BLTModule.Trace($"GetCurrentUser: TLS protocol = {System.Net.ServicePointManager.SecurityProtocol}");

            // .NET Framework 4.8 can still default to protocols Twitch no longer accepts.
            // Force TLS 1.2 so the handshake can't fall back to something rejected.
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                BLTModule.Trace("GetCurrentUser: TLS 1.2 enabled");
            }
            catch (Exception tlsEx)
            {
                BLTModule.Trace($"GetCurrentUser: could not set TLS 1.2: {tlsEx.Message}");
            }

            BLTModule.Trace("GetCurrentUser: creating HttpClient");
            using (var http = new HttpClient())
            {
                BLTModule.Trace("GetCurrentUser: setting headers");
                http.DefaultRequestHeaders.Add("Client-ID", clientId);
                http.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);

                BLTModule.Trace("GetCurrentUser: sending HTTPS request to api.twitch.tv");
                var response = await http.GetAsync("https://api.twitch.tv/helix/users");
                BLTModule.Trace($"GetCurrentUser: response {(int)response.StatusCode}");
                string body = await response.Content.ReadAsStringAsync();
                BLTModule.Trace("GetCurrentUser: body read");

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Twitch /helix/users returned {(int)response.StatusCode}: {body}");
                }

                var first = JObject.Parse(body)["data"]?.FirstOrDefault();
                if (first == null)
                {
                    throw new Exception("Twitch /helix/users returned no user - is the access token valid?");
                }

                return new TwitchUserInfo
                {
                    Id = (string)first["id"],
                    Login = (string)first["login"],
                    BroadcasterType = (string)first["broadcaster_type"] ?? string.Empty,
                };
            }
        }

        // Drop a file with one of these names next to BannerlordTwitch.dll to switch that
        // feature off without needing a new build - used to isolate which Twitch component
        // is responsible for the native crash, and to keep the rest of BLT usable meanwhile.
        //   BLT_DISABLE_CHATBOT.txt   - skips the Twitch chat bot (!commands)
        //   BLT_DISABLE_EVENTSUB.txt  - skips channel points / EventSub
        private static bool IsDisabled(string markerFileName)
        {
            try
            {
                string path = Path.Combine(
                    Path.GetDirectoryName(typeof(TwitchService).Assembly.Location) ?? string.Empty,
                    markerFileName);
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        public TwitchService()
        {
            BLTModule.Trace("TwitchService ctor: START");
            settings = Settings.Load();
            if (settings == null)
            {
                throw new Exception($"Failed to load action/command settings, please use the BLT Configure Window to configure the mod");
            }

            authSettings = AuthSettings.Load();
            if (authSettings == null)
            {
                throw new Exception($"You need to authorize via the BLT Configure Window, then restart. If the window isn't open then you need to enable the BLTConfigure module.");
            }

            if (authSettings.DebugSpoofAffiliate)
            {
                Log.LogFeedSystem($"Affiliate spoofing enabled");
                affiliateSpoofing = new Dummy.AffiliateSpoofingHttpCallHandler();
                api = CreateTwitchApi(affiliateSpoofing);
                affiliateSpoofing.OnRewardRedeemed += OnRewardRedeemedInternal;
            }
            else
            {
                BLTModule.Trace("TwitchService ctor: creating CustomTwitchHttpClient");
                var httpHandler = new CustomTwitchHttpClient();
                BLTModule.Trace("TwitchService ctor: creating TwitchAPI");
                api = CreateTwitchApi(httpHandler);
                BLTModule.Trace("TwitchService ctor: TwitchAPI created OK");
            }

            //api.Settings.Secret = SECRET;
            api.Settings.SkipDynamicScopeValidation = true;
            api.Settings.ClientId = authSettings.ClientID;
            api.Settings.AccessToken = authSettings.AccessToken;

            // Task.Run so that even the synchronous part of the request (DNS, connection setup)
            // happens on a thread pool thread. This used to begin on Bannerlord's main thread
            // while the campaign was still initialising deep in native engine code, and that is
            // where the process was dying - natively, with no managed exception to catch.
            BLTModule.Trace("TwitchService ctor: starting Twitch lookup on background thread");
            string clientIdCopy = authSettings.ClientID;
            string accessTokenCopy = authSettings.AccessToken;
            Task.Run(async () =>
            {
                await ProbeHttpsAsync();
                return await GetCurrentUserAsync(clientIdCopy, accessTokenCopy);
            }).ContinueWith(t =>
            {
                BLTModule.Trace($"GetUsersAsync returned (faulted={t.IsFaulted})");
                MainThreadSync.Run(() =>
                {
                    BLTModule.Trace("GetUsers continuation: running on main thread");
                    if (t.IsFaulted)
                    {
                        Log.Fatal($"Service init failed: {t.Exception?.GetBaseException().Message}");
                        return;
                    }

                    var user = t.Result;

                    BLTModule.Trace($"GetUsers continuation: got channel id");
                    Log.Info($"Channel ID is {user.Id}");
                    channelId = user.Id;

                    // ── Init extension PubSub (requires channelId) ────────────
                    // Each service is wrapped in its own try/catch so a failure in
                    // either one cannot prevent bot and eventsub from initialising.
                    BLTModule.Trace($"GetUsers continuation: ExtensionConfigured={authSettings.ExtensionConfigured}");
                    if (authSettings.ExtensionConfigured)
                    {
                        try
                        {
                            extensionPubSub = new ExtensionPubSubService(
                                new CustomTwitchHttpClient(),
                                authSettings.ExtensionClientId,
                                authSettings.ExtensionSecret,
                                channelId,
                                authSettings.AccessToken,
                                authSettings.ClientID);
                            Log.Info("[Extension] PubSub service ready");
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[Extension] PubSub init failed: {ex.Message}");
                        }

                        try
                        {
                            localRelay = new LocalRelayService();
                            Log.Info("[LocalRelay] Service started — OBS source: http://localhost:3000");
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[LocalRelay] Init failed — OBS overlay may not function: {ex.Message}");
                        }
                    }
                    else
                    {
                        Log.Info("[Extension] ExtensionClientId/ExtensionSecret not configured — PubSub disabled");
                    }

                    BLTModule.Trace("GetUsers continuation: reached chatbot step");
                    // Connect the chatbot
                    if (IsDisabled("BLT_DISABLE_CHATBOT.txt"))
                    {
                        Log.LogFeedSystem("[BLT] Chat bot DISABLED by BLT_DISABLE_CHATBOT.txt");
                    }
                    else
                    {
                        bot = new Bot(user.Login, authSettings);
                    }

                    if (string.IsNullOrEmpty(user.BroadcasterType))
                    {
                        Log.Error($"You must be a Twitch Partner or Affiliate to use the channel points system. You can still use the chat commands (you may need to add some in the configure window to get full functionality).");
                        return;
                    }

                    BLTModule.Trace("GetUsers continuation: reached eventsub step");
                    if (IsDisabled("BLT_DISABLE_EVENTSUB.txt"))
                    {
                        Log.LogFeedSystem("[BLT] Channel points / EventSub DISABLED by BLT_DISABLE_EVENTSUB.txt");
                    }
                    else
                    {
                        eventsub = new TwitchEventSubSocket();

                        //send authSettings.AccessToken
                        eventsub.OnEventSubServiceConnected += OnEventSubConnected;
                        eventsub.OnChannelPointsRewardsRedeemed += OnRewardRedeemed;
                        RegisterRewardsAsync();

                        _ = eventsub.StartAsync(token);
                    }

                    /**

                    
                    // Create new instance of PubSub Client
                    pubSub = new TwitchPubSub();

                    // Subscribe to Events
                    // Whisper isn't supported without verified bot
                    //_pubSub.OnWhisper += OnWhisper;
                    pubSub.OnPubSubServiceConnected += OnPubSubServiceConnected;
                    pubSub.OnChannelPointsRewardRedeemed += OnRewardRedeemed;
                    pubSub.ListenToChannelPoints(channelId);
                    // pubSub.OnRewardRedeemed += OnRewardRedeemed;
                    pubSub.OnLog += (_, args) =>
                    {
                        if (args.Data.Contains("PONG")) return;
                        try
                        {
                            Log.Trace(args.Data);
                        }
                        catch
                        {
                            // ignored
                        }
                    };

                    // pubSub.OnPubSubServiceClosed += OnOnPubSubServiceClosed;
                    RegisterRewardsAsync();

                    // Connect
                    pubSub.Connect();
                    pubSub.SendTopics(authSettings.AccessToken);
                    **/
                });
            });
        }

        private async void OnEventSubConnected(object o, WebsocketConnectedArgs args)
        {
            // Read SessionId fresh on every attempt rather than once: the socket may still be
            // settling when this fires, and after a reconnect the old id is already invalid.
            // Twitch rejects a stale id with "websocket transport session does not exist".
            const int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    string sessionId = eventsub?.SessionId;
                    if (string.IsNullOrEmpty(sessionId))
                    {
                        await Task.Delay(500);
                        continue;
                    }

                    var conditions = new Dictionary<string, string>
                    {
                        { "broadcaster_user_id", channelId }
                    };
                    var subscriptionResponse = await api.Helix.EventSub.CreateEventSubSubscriptionAsync(
                        "channel.channel_points_custom_reward_redemption.add",
                        "1",
                        conditions,
                        EventSubTransportMethod.Websocket,
                        sessionId);

                    Log.Info($"[EventSub] Subscribed to channel points, status: {subscriptionResponse.Subscriptions?.FirstOrDefault()?.Status}");
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == maxAttempts)
                    {
                        Log.Error($"[EventSub] Failed to subscribe to channel points after {maxAttempts} attempts: {ex.Message}");
                        return;
                    }

                    Log.Info($"[EventSub] Subscribe attempt {attempt} failed ({ex.Message}) - retrying");
                    await Task.Delay(1000);
                }
            }

            Log.Error("[EventSub] Gave up subscribing to channel points - no valid websocket session");
        }

        /**
        private void OnPubSubServiceConnected(object sender, EventArgs e)
        {
            Log.LogFeedSystem("{=BiYZ1CbN}TwitchService connected".Translate());

#pragma warning disable 618
            // Obsolete warning disabled because no new version has yet been written!
            pubSub.ListenToRewards(channelId);
#pragma warning restore 618
            pubSub.SendTopics(authSettings.AccessToken);
        }**/

        // NOTE: `async void`. Any exception that escapes this method is rethrown on a thread
        // pool thread where no try/catch can reach it, which terminates the game instantly
        // with no crash dialog and nothing written to any log. The whole body is therefore
        // wrapped defensively below - a failure to set up channel point rewards must never
        // be able to take the process down with it.
        private async void RegisterRewardsAsync()
        {
            try
            {
                await RegisterRewardsAsyncCore();
            }
            catch (Exception ex)
            {
                Log.Exception("Failed to register channel point rewards", ex, noRethrow: true);
            }
        }

        private async Task RegisterRewardsAsyncCore()
        {
            await RemoveRewardsAsync();

            Log.Info("Creating rewards");

            GetCustomRewardsResponse existingRewards = null;
            try
            {
                existingRewards = await api.Helix.ChannelPoints.GetCustomRewardAsync(channelId, accessToken: authSettings.AccessToken, onlyManageableRewards: true);
            }
            catch (Exception e)
            {
                Log.Error($"ERROR: Couldn't retrieve existing rewards: {e.Message}");
            }

            var failures = new List<string>();
            foreach (var rewardDef in settings.EnabledRewards.Where(r => existingRewards == null || existingRewards.Data.All(e => e.Title != r.RewardSpec?.Title.ToString())))
            {
                try
                {
                    if (rewardDef.RewardSpec.Cost <= 0)
                    {
                        throw new Exception("Cost must be greater than 0, it must NOT be 0");
                    }
                    if (rewardDef.RewardSpec.GlobalCooldownSeconds is <= 0)
                    {
                        throw new Exception("Global Cooldown must be either blank or greater than 0, it must NOT be 0");
                    }
                    if (rewardDef.RewardSpec.MaxPerUserPerStream is <= 0)
                    {
                        throw new Exception("Max Per User Per Stream must be either blank or greater than 0, it must NOT be 0");
                    }
                    if (rewardDef.RewardSpec.MaxPerStream is <= 0)
                    {
                        throw new Exception("Max Per Stream must be either blank or greater than 0, it must NOT be 0");
                    }

                    var createdReward = (await api.Helix.ChannelPoints.CreateCustomRewardsAsync(channelId, rewardDef.RewardSpec.GetTwitchSpec(), authSettings.AccessToken)).Data.First();
                    Log.Info($"Created reward {createdReward.Title} ({createdReward.Id})");
                }
                catch (Exception e)
                {
                    // Read the title defensively: a null RewardSpec is one of the things that
                    // lands us in here, and throwing from inside a catch block would escape
                    // this async void method and kill the process.
                    string title = rewardDef.RewardSpec?.Title?.ToString() ?? "(unnamed reward)";
                    Log.Error($"Couldn't create reward {title}: {e.Message}");
                    failures.Add($"{title}: {e.Message}");
                }
            }

            if (failures.Any())
            {
                // Execution resumed on a thread pool thread after the awaits above. Touching
                // Bannerlord's UI from anywhere but the main thread crashes the engine
                // natively - no managed exception, no crash dialog - so marshal it back.
                MainThreadSync.Run(() =>
                    InformationManager.ShowInquiry(
                        new InquiryData(
                            "Bannerlord Twitch",
                            $"Failed to create some of the channel rewards:\n" + string.Join("\n", failures),
                            true, false, "Okay", null,
                            () => { }, () => { }), true));
            }
        }

        // This used to block on .Result / Task.WaitAll. It is called during game start, on the
        // main thread, so those blocking waits stalled Bannerlord's entire render loop on live
        // Twitch HTTP calls - the game appeared frozen, and a main thread wedged that long can
        // take the engine down natively (no managed exception, nothing in any log). Awaited now.
        private async Task RemoveRewardsAsync()
        {
            Log.Info("Removing existing rewards");
            try
            {
                var allRewards = await api.Helix.ChannelPoints.GetCustomRewardAsync(
                    channelId, accessToken: authSettings.AccessToken, onlyManageableRewards: true);
                if (allRewards == null)
                {
                    throw new Exception($"Couldn't retrieve channel point rewards");
                }

                var deletions = allRewards.Data.Select(async r =>
                {
                    try
                    {
                        await api.Helix.ChannelPoints.DeleteCustomRewardAsync(
                            channelId, r.Id, accessToken: authSettings.AccessToken);
                        Log.Info($"Removed reward {r.Title}");
                    }
                    catch (Exception e)
                    {
                        Log.Info($"Failed to remove {r.Title}: {e.Message}");
                    }
                }).ToArray();

                // Keep the original 5s ceiling so a hanging Twitch API can't stall startup.
                await Task.WhenAny(Task.WhenAll(deletions), Task.Delay(TimeSpan.FromSeconds(5)));

                Log.LogFeedSystem($"All rewards removed");
            }
            catch (Exception e)
            {
                Log.LogFeedSystem($"Failed to remove all rewards: {e.Message}");
            }
        }

        private void OnRewardRedeemed(object sender, ChannelPointsCustomRewardRedemption redeemedArgs)
        {
            if (redeemedArgs.BroadcasterUserId == channelId)
            {
                OnRewardRedeemedInternal(sender, redeemedArgs);
            }
        }

        private void OnRewardRedeemedInternal(object sender, ChannelPointsCustomRewardRedemption redemption)
        {
            MainThreadSync.Run(() =>
            {
                var reward = settings.Rewards.FirstOrDefault(r => r.RewardSpec.Title.ToString() == redemption.Reward.Title);
                if (reward == null)
                {
                    Log.Info($"Reward {redemption.Reward.Title} not owned by this extension, ignoring it");
                    // We don't cancel redemptions we don't know about!
                    // RedemptionCancelled(e.RedemptionId, $"Reward {e.RewardRedeemed.Redemption.Reward.Title} not found");
                    return;
                }

                if (redemption.Status.ToLower() != "unfulfilled")
                {
                    Log.Info($"Reward {redemption.Reward.Title} status {redemption.Status} is not interesting, " +
                             $"ignoring it");
                    return;
                }

                Log.Info($"Redemption of {redemption.Reward.Title} from {redemption.UserName} received!");

                var context = ReplyContext.FromRedemption(reward, redemption);
#if !DEBUG
                try
                {
#endif
                    redemptionCache.TryAdd(redemption.Id, redemption);
                    ActionManager.HandleReward(reward.Handler, context, reward.HandlerConfig);
#if !DEBUG
                }
                catch (Exception e)
                {
                    Log.Error($"Exception happened while trying to enqueue redemption {redemption.Id}: {e.Message}");
                    RedemptionCancelled(context, $"Exception occurred: {e.Message}");
                }
#endif
            });
        }

        public bool TestRedeem(string rewardName, string user, string message)
        {
            var reward = settings?.EnabledRewards.FirstOrDefault(r => string.Equals(r.RewardSpec.Title.ToString(), rewardName, StringComparison.CurrentCultureIgnoreCase));
            if (reward == null)
            {
                Log.Error($"Reward {rewardName} not found!");
                return false;
            }

            if (affiliateSpoofing == null)
            {
                Log.Error($"You must enable Affiliate Spoofing on the Auth tab in the configure window to test redemption or perform sim testing!");
                return false;
            }

            return affiliateSpoofing.FakeRedeem(reward.RewardSpec.Title.ToString(), user, message);
        }

        // private void ShowMessage(string screenMsg, string botMsg, string userToAt)
        // {
        //     Log.Screen(screenMsg);
        //     SendChat($"@{userToAt}: {botMsg}");
        // }
        //
        // private void ShowMessageFail(string screenMsg, string botMsg, string userToAt)
        // {
        //     Log.ScreenFail(screenMsg);
        //     SendChat($"@{userToAt}: {botMsg}");
        // }

        // public void SendChat(params string[] message)
        // {
        //     Log.Trace($"[chat] {string.Join(" - ", message)}");
        //     bot.SendChat(message);
        // }

        public bool IsSimTesting => simTest != null;

        public void SendReply(ReplyContext context, params string[] messages)
        {
            if (context.Source.RespondInOverlay || IsSimTesting)
                Log.LogFeedResponse(context.UserName, messages);

            if (context.Source.RespondInTwitch && !IsSimTesting)
            {
                if (context.UserName != null)
                {
                    bot.SendChatReply(context.UserName, messages);
                    Log.Trace($"[TwitchService] Reply to {context.UserName}: {string.Join(", ", messages)}");
                }
                else
                {
                    bot.SendChat(messages);
                }
            }

            if (context.Source.RespondInExtension && !IsSimTesting)
            {
                // Twitch Extension PubSub (if configured)
                if (extensionPubSub != null)
                {
                    if (context.UserName != null)
                        _ = extensionPubSub.SendWhisperToUserNameAsync(context.UserName, messages);
                    else
                        _ = extensionPubSub.SendBroadcastAsync(messages);
                }

                // Local relay — always active, no configuration needed
                if (localRelay != null)
                    _ = localRelay.SendReplyAsync(context.UserName, messages);
            }
        }

        public void SendNonReply(ReplyContext context, params string[] messages)
        {
            if (context.Source.RespondInOverlay || IsSimTesting)
            {
                Log.LogFeedMessage(messages);
            }
            if (context.Source.RespondInTwitch && !IsSimTesting)
            {
                bot.SendChat(messages);
            }

            // Extension PubSub: non-replies are always broadcast (no specific user)
            if (context.Source.RespondInExtension && extensionPubSub != null && !IsSimTesting)
            {
                _ = extensionPubSub.SendBroadcastAsync(messages);
            }
        }

        public void SendChat(params string[] messages)
        {
            if (!IsSimTesting)
            {
                bot.SendChat(messages);
            }
            else
            {
                Log.LogFeedMessage("[CHAT]".Yield().Concat(messages).ToArray());
            }

            Log.Trace($"[{nameof(TwitchService)}] Chat: {string.Join(", ", messages)}");
        }

        /// <summary>
        /// Call this from the BLTOverlay /register endpoint after validating
        /// the viewer's JWT. Pre-warms the userId cache so first whisper is instant.
        /// </summary>
        public void RegisterExtensionUser(string userName, string userId)
        {
            extensionPubSub?.RegisterUser(userName, userId);
        }

        private void ShowCommandHelp()
        {
            MainThreadSync.Run(() =>
            {
                var help = "{=luOJS8dL}Commands: ".Translate().Yield()
                    .Concat(settings.EnabledCommands.Where(c => !c.HideHelp)
                        .Select(c => LocString.IsNullOrEmpty(c.Help) ? $"!{c.Name}" : $"!{c.Name} - {c.Help}")
                    )
                    .ToList();
                if (settings.EnabledRewards.Any())
                {
                    help.Add("{=0o3dPQSk}Also see Channel Point Rewards".Translate());
                }

                bot.SendChat(help.ToArray());
            });
        }

        public void ExecuteCommand(string cmdName, ChatMessage chatMessage, string args)
        {
            MainThreadSync.Run(() =>
            {
                var cmd = this.settings.GetCommand(cmdName);
                if (cmd == null)
                {
                    Log.Trace($"[{nameof(TwitchService)}] Couldn't find command {cmdName}");
                    return;
                }

                var context = ReplyContext.FromMessage(cmd, chatMessage, args);
                if (cmd.ModeratorOnly && !chatMessage.IsModerator && !chatMessage.IsBroadcaster)
                {
                    Log.Info($"[{nameof(TwitchService)}] Blocked command '{cmdName}' from '{chatMessage.DisplayName}' — not mod or broadcaster");
                    SendReply(context,
                        "{=X9J4K2L8}@{DisplayName}, Only moderators and broadcaster can use this command"
                            .Translate(("DisplayName", chatMessage.DisplayName)));
                    return;
                }

#if !DEBUG
                try
                {
#endif
                    ActionManager.HandleCommand(cmd.Handler, context, cmd.HandlerConfig);
#if !DEBUG
                }
                catch (Exception e)
                {
                    Log.Exception($"Command {cmdName} failed with exception {e.Message}, game might be unstable now!", e);
                }
#endif
            });
        }

        public void ExecuteOverlayRaw(string rawCommand, string userName)
        {
            if (string.IsNullOrWhiteSpace(rawCommand))
                return;

            // Match Bot.cs parsing EXACTLY
            var parts = rawCommand.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            string cmdName = parts[0];
            string args = parts.Length > 1 ? parts[1] : string.Empty;

            ExecuteOverlayCommand(cmdName, userName, args);
        }

        public void ExecuteOverlayCommand(string cmdName, string userName, string args)
        {
            MainThreadSync.Run(() =>
            {
                var cmd = this.settings.GetCommand(cmdName);
                if (cmd == null)
                {
                    Log.Trace($"[Overlay] Unknown command '{cmdName}'");
                    return;
                }

                var context = ReplyContext.FromOverlay(cmd, userName, args);

                if (cmd.ModeratorOnly && !context.IsModerator && !context.IsBroadcaster)
                {
                    Log.Info($"[Overlay] Blocked '{cmdName}' from '{context.UserName}' (not mod)");
                    SendReply(context,
                        "{=X9J4K2L8}@{DisplayName}, Only moderators and broadcaster can use this command"
                            .Translate(("DisplayName", context.UserName)));
                    return;
                }

#if !DEBUG
                try
                {
#endif
                    ActionManager.HandleCommand(cmd.Handler, context, cmd.HandlerConfig);
#if !DEBUG
                }
                catch (Exception e)
                {
                    Log.Exception($"Overlay command {cmdName} failed: {e.Message}", e);
                }
#endif
            });
        }

        public void ExecuteOverlayWire(BltWireMessage wire)
        {
            if (wire == null || wire.Kind != "command" || string.IsNullOrWhiteSpace(wire.Command))
                return;

            ExecuteOverlayCommand(
                wire.Command,
                wire.User?.Name,
                wire.Args ?? string.Empty);
        }

        public bool TestCommand(string cmdName, string userName, string args)
        {
            var cmd = this.settings.GetCommand(cmdName);
            if (cmd == null)
                return false;
            var context = ReplyContext.FromUser(cmd, userName, args);
            ActionManager.HandleCommand(cmd.Handler, context, cmd.HandlerConfig);
            return true;
        }

        public void RedemptionComplete(ReplyContext context, string info = null)
        {
            if (!redemptionCache.TryRemove(context.RedemptionId, out var redemption))
            {
                Log.Error($"RedemptionComplete failed: redemption {context.RedemptionId} not known!");
                return;
            }
            if (!string.IsNullOrEmpty(info))
            {
                ActionManager.SendReply(context, info);
            }

            if (affiliateSpoofing == null)
            {
                if (!settings.DisableAutomaticFulfillment && (context.Source as Reward)?.RewardSpec?.DisableAutomaticFulfillment != true)
                {
                    _ = SetRedemptionStatusAsync(redemption, CustomRewardRedemptionStatus.FULFILLED);
                }
                else
                {
                    Log.Info($"Skipped marking {redemption.Reward.Title} for {redemption.UserName} as fulfilled as DisableAutomaticFulfillment is set");
                }
            }
            else
            {
                Log.Trace($"(skipped setting redemption status for test redemption)");
            }
        }

        public void RedemptionCancelled(ReplyContext context, string reason = null)
        {
            if (!redemptionCache.TryRemove(context.RedemptionId, out var redemption))
            {
                Log.Error($"RedemptionCancelled failed: redemption {context.RedemptionId} not known!");
                return;
            }
            if (!string.IsNullOrEmpty(reason))
            {
                ActionManager.SendReply(context, reason);
            }

            if (affiliateSpoofing == null)
            {
                _ = SetRedemptionStatusAsync(redemption, CustomRewardRedemptionStatus.CANCELED);
            }
            else
            {
                Log.Trace($"(skipped setting redemption status for test redemption)");
            }
        }

        private async Task SetRedemptionStatusAsync(ChannelPointsCustomRewardRedemption redemption, CustomRewardRedemptionStatus status)
        {
            try
            {
                await api.Helix.ChannelPoints.UpdateRedemptionStatusAsync(
                    redemption.BroadcasterUserId,
                    redemption.Reward.Id,
                    new List<string> { redemption.Id },
                    new UpdateCustomRewardRedemptionStatusRequest { Status = status },
                    authSettings.AccessToken
                );
            }
            catch (Exception e)
            {
                Log.Error($"Failed to set redemption status of {redemption.Id} ({redemption.Reward.Title} for {redemption.UserName}) to {status}: {e.Message}");
            }
        }

        // private void OnOnPubSubServiceClosed(object sender, EventArgs e)
        // {
        //     Log.ScreenFail("PubSub Service closed, attempting reconnect...");
        //     pubSub.Connect();
        // }

        public object FindGlobalConfig(string id) => settings?.GlobalConfigs?.FirstOrDefault(c => c.Id == id)?.Config;

        private static SimulationTest simTest;
        private readonly AffiliateSpoofingHttpCallHandler affiliateSpoofing;

        public bool StartSim()
        {
            StopSim();
            simTest = new(settings);
            return true;
        }

        public bool StopSim()
        {
            if (simTest != null)
            {
                Log.LogFeedSystem($"Sim stopped");
                simTest.Stop();
                simTest = null;
                return true;
            }

            return false;
        }

        private void ReleaseUnmanagedResources()
        {
            StopSim();
            // Disposal path: bounded so shutdown can't hang on a slow Twitch API.
            RemoveRewardsAsync().Wait(TimeSpan.FromSeconds(5));
            bot?.Dispose();
            _ = eventsub?.StopAsync(token);
            //pubSub?.Disconnect();
            Log.LogFeedSystem("{=mEcBdqNC}TwitchService stopped".Translate());
        }

        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~TwitchService()
        {
            ReleaseUnmanagedResources();
        }
    }
}