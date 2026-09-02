using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib.EventSub.Websockets.Core.EventArgs.Channel;
using TwitchLib.EventSub.Websockets.Core.EventArgs;
using TwitchLib.EventSub.Websockets;
using Microsoft.Extensions.Hosting;
using TwitchLib.EventSub.Core.SubscriptionTypes.Channel;
using BannerlordTwitch.Util;

namespace BannerlordTwitch.Twitch
{
    public class TwitchEventSubSocket : IHostedService
    {
        public delegate void ChannelPointsRewardEvent(object e, ChannelPointsCustomRewardRedemption args);
        public delegate void SubWebSocketConnectedEvent(object e, WebsocketConnectedArgs args);
        private readonly EventSubWebsocketClient _eventSubWebsocketClient;

        public SubWebSocketConnectedEvent OnEventSubServiceConnected;
        public ChannelPointsRewardEvent OnChannelPointsRewardsRedeemed;

        public string SessionId { 
            get{
                return _eventSubWebsocketClient?.SessionId;
            } 
        }

        // Deliberately takes no ILogger parameter, and builds EventSubWebsocketClient
        // reflectively rather than with `new EventSubWebsocketClient(null)`.
        //
        // Both of those would put a Microsoft.Extensions.Logging.Abstractions type
        // (ILogger<>/ILoggerFactory) into a signature baked into our compiled IL. TwitchLib
        // was built against a different version of that assembly than we are, and
        // TAOM.Dependencies bundles a third - whichever one wins at runtime, a mismatch means
        // the CLR cannot find the member and throws MissingMethodException while JIT-compiling
        // the *whole enclosing method*, before a single line of it runs. Reflection keeps
        // those types out of our IL entirely, so no version has to agree with any other.
        public TwitchEventSubSocket()
        {
            _eventSubWebsocketClient = CreateEventSubWebsocketClient();

            _eventSubWebsocketClient.WebsocketConnected += OnWebsocketConnected;
            _eventSubWebsocketClient.WebsocketDisconnected += OnWebsocketDisconnected;
            _eventSubWebsocketClient.WebsocketReconnected += OnWebsocketReconnected;
            _eventSubWebsocketClient.ErrorOccurred += OnErrorOccurred;

            _eventSubWebsocketClient.ChannelPointsCustomRewardRedemptionAdd += async (object e, ChannelPointsCustomRewardRedemptionArgs args) =>
            {
                OnChannelPointsRewardsRedeemed?.Invoke(e, args.Notification.Payload.Event);
            };

            _eventSubWebsocketClient.ChannelFollow += OnChannelFollow;
        }

        // Picks the single-parameter EventSubWebsocketClient(ILoggerFactory) overload by shape
        // rather than by signature, and passes null - null needs no type identity to match.
        private static EventSubWebsocketClient CreateEventSubWebsocketClient()
        {
            ConstructorInfo ctor = typeof(EventSubWebsocketClient).GetConstructors()
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault(c => c.GetParameters().Length == 1);

            if (ctor == null)
            {
                throw new Exception(
                    "Could not find a usable EventSubWebsocketClient constructor - TwitchLib.EventSub.Websockets.dll may be missing or the wrong version.");
            }

            return (EventSubWebsocketClient)ctor.Invoke(new object[] { null });
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _eventSubWebsocketClient.ConnectAsync();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _eventSubWebsocketClient.DisconnectAsync();
        }

        private async Task OnErrorOccurred(object sender, ErrorOccuredArgs e)
        {
            // Was empty — swallowing all websocket errors silently
            Log.Error($"[EventSub] Error: {e.Exception?.Message ?? "(no message)"}");
        }

        private async Task OnChannelFollow(object sender, ChannelFollowArgs e)
        {
            var eventData = e.Notification.Payload.Event;
        }

        private async Task OnWebsocketConnected(object sender, WebsocketConnectedArgs e)
        {
            if (!e.IsRequestedReconnect)
            {
                // Guard against NullReferenceException if nobody subscribed
                OnEventSubServiceConnected?.Invoke(sender, e);
            }
        }

        private async Task OnWebsocketDisconnected(object sender, EventArgs e)
        {
            Log.LogFeedSystem("EventSub disconnected, reconnecting…");
            // Add a small delay before each retry so we don't hammer Twitch
            while (!await _eventSubWebsocketClient.ReconnectAsync())
            {
                Log.Error("[EventSub] Reconnect attempt failed, retrying in 5s…");
                await Task.Delay(5000);
            }
        }

        private async Task OnWebsocketReconnected(object sender, EventArgs e)
        {
            // Twitch issues a brand new session id on every reconnect, and any subscription
            // made against the previous one is dead. This handler used to be empty, so after
            // the first disconnect channel points silently stopped working - Twitch answered
            // "websocket transport session does not exist or has already disconnected".
            // Re-run the same subscription path used on a fresh connect.
            Log.LogFeedSystem("[EventSub] Reconnected - re-subscribing to channel points");
            OnEventSubServiceConnected?.Invoke(sender, e as WebsocketConnectedArgs);
        }
    }
}
