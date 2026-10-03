using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BannerlordTwitch.Util;

namespace BLTAdoptAHero.Coop
{
    /// <summary>
    /// A direct BLT-to-BLT link for co-op sessions, deliberately independent of whatever co-op mod
    /// is running underneath.
    ///
    /// The problem it solves: a viewer summon exists only on the machine running BLT. Co-op mods
    /// sync agents they spawned themselves from the shared troop rosters, and a summon has no such
    /// counterpart, so the other player sees nothing. Rather than trying to reach into another
    /// mod's networking, BLT carries its own spawns over its own socket: the host announces every
    /// agent it spawns, the guest replays it locally at the same position, in the same team, with
    /// the same troop.
    ///
    /// Line protocol, newline-delimited UTF-8, '|' separated - small enough to read in a log and to
    /// debug with telnet:
    ///     HELLO|1|&lt;role&gt;
    ///     SPAWN|&lt;kind&gt;|&lt;charStringId&gt;|&lt;displayName&gt;|&lt;playerSide&gt;|&lt;x&gt;|&lt;y&gt;|&lt;z&gt;|&lt;dirX&gt;|&lt;dirY&gt;|&lt;mounted&gt;|&lt;spawnId&gt;
    ///     ROSTER|&lt;spawnId&gt;,&lt;spawnId&gt;,...
    ///     MISSION|START or MISSION|END
    /// ROSTER is the repair tick: it repeats every few seconds so a guest that joined late, missed
    /// a packet, or hit an exception mid-spawn still converges on the host's picture instead of
    /// staying permanently short a hero.
    /// </summary>
    public static class CoopLink
    {
        public const int ProtocolVersion = 1;
        public const int DefaultPort = 47800;

        private static CoopHost host;
        private static CoopGuest guest;

        public static bool IsHost => host != null;
        public static bool IsGuest => guest != null;
        public static bool IsActive => IsHost || IsGuest;

        /// <summary>
        /// Started from BLTAdoptAHeroModule at game start. Which side this machine is comes from a
        /// marker file next to BLTAdoptAHero.dll, so a tester can switch roles without a new build:
        ///   BLT_COOP_HOST.txt  - optional port on the first line (default 47800)
        ///   BLT_COOP_JOIN.txt  - "address" or "address:port" of the host
        /// </summary>
        public static void StartFromConfig()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(CoopLink).Assembly.Location) ?? ".";

                string hostCfg = ReadMarker(Path.Combine(dir, "BLT_COOP_HOST.txt"));
                if (hostCfg != null)
                {
                    int port = ParsePort(hostCfg, DefaultPort);
                    host = new CoopHost(port);
                    host.Start();
                    return;
                }

                string joinCfg = ReadMarker(Path.Combine(dir, "BLT_COOP_JOIN.txt"));
                if (joinCfg != null)
                {
                    SplitAddress(joinCfg, out string address, out int port);
                    guest = new CoopGuest(address, port);
                    guest.Start();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[CoopLink] Failed to start: {ex.Message}");
            }
        }

        public static void Stop()
        {
            try { host?.Stop(); } catch { /* shutting down anyway */ }
            try { guest?.Stop(); } catch { /* shutting down anyway */ }
            host = null;
            guest = null;
        }

        public static void Send(string line)
        {
            host?.Broadcast(line);
        }

        /// <summary>Lines received by a guest, drained on the main thread by the mission behavior.</summary>
        public static bool TryDequeue(out string line)
        {
            if (guest != null) return guest.Inbox.TryDequeue(out line);
            line = null;
            return false;
        }

        private static string ReadMarker(string path)
        {
            if (!File.Exists(path)) return null;

            string value = File.ReadAllLines(path)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#"));

            // An empty host marker is still a valid "be the host, on the default port".
            return value ?? string.Empty;
        }

        private static int ParsePort(string text, int fallback)
            => int.TryParse(text, out int port) && port > 0 && port < 65536 ? port : fallback;

        private static void SplitAddress(string text, out string address, out int port)
        {
            int colon = text.LastIndexOf(':');
            if (colon > 0)
            {
                address = text.Substring(0, colon).Trim();
                port = ParsePort(text.Substring(colon + 1).Trim(), DefaultPort);
            }
            else
            {
                address = text;
                port = DefaultPort;
            }
        }
    }

    /// <summary>Host side: accepts guests and fans every line out to all of them.</summary>
    internal class CoopHost
    {
        private readonly int port;
        private readonly List<TcpClient> clients = new();
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;

        public CoopHost(int port) => this.port = port;

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            running = true;

            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "BLT CoopLink host" };
            acceptThread.Start();

            Log.LogFeedSystem($"[BLT] Co-op link: hosting on port {port}. "
                              + "The other player needs BLT_COOP_JOIN.txt pointing at this machine.");
        }

        private void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    client.NoDelay = true;

                    lock (clients) clients.Add(client);

                    // MainThreadSync: this runs on the accept thread, and the feed touches game UI.
                    MainThreadSync.Run(() => Log.LogFeedSystem(
                        $"[BLT] Co-op link: a player connected ({client.Client.RemoteEndPoint})"));

                    Send(client, $"HELLO|{CoopLink.ProtocolVersion}|host");
                }
                catch (SocketException)
                {
                    // Listener stopped, or a client vanished mid-handshake.
                    if (!running) return;
                }
                catch (Exception ex)
                {
                    Log.Error($"[CoopLink] Accept failed: {ex.Message}");
                    Thread.Sleep(1000);
                }
            }
        }

        public void Broadcast(string line)
        {
            List<TcpClient> snapshot;
            lock (clients) snapshot = clients.ToList();
            if (snapshot.Count == 0) return;

            foreach (var client in snapshot)
            {
                if (!Send(client, line))
                {
                    lock (clients) clients.Remove(client);
                    try { client.Close(); } catch { /* already gone */ }
                }
            }
        }

        private static bool Send(TcpClient client, string line)
        {
            try
            {
                if (!client.Connected) return false;
                byte[] data = Encoding.UTF8.GetBytes(line + "\n");
                client.GetStream().Write(data, 0, data.Length);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch { /* ignored */ }

            lock (clients)
            {
                foreach (var c in clients)
                {
                    try { c.Close(); } catch { /* ignored */ }
                }
                clients.Clear();
            }
        }
    }

    /// <summary>Guest side: keeps a connection to the host and queues whatever arrives.</summary>
    internal class CoopGuest
    {
        private readonly string address;
        private readonly int port;
        private Thread thread;
        private TcpClient client;
        private volatile bool running;

        public readonly ConcurrentQueue<string> Inbox = new();

        public CoopGuest(string address, int port)
        {
            this.address = address;
            this.port = port;
        }

        public void Start()
        {
            running = true;
            thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "BLT CoopLink guest" };
            thread.Start();

            Log.LogFeedSystem($"[BLT] Co-op link: connecting to {address}:{port}");
        }

        private void ReceiveLoop()
        {
            while (running)
            {
                try
                {
                    client = new TcpClient { NoDelay = true };
                    client.Connect(address, port);

                    MainThreadSync.Run(() => Log.LogFeedSystem("[BLT] Co-op link: connected to the host"));

                    using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                    string line;
                    while (running && (line = reader.ReadLine()) != null)
                    {
                        Inbox.Enqueue(line);
                    }
                }
                catch (Exception ex)
                {
                    if (!running) return;
                    Log.Trace($"[CoopLink] Connection lost ({ex.Message}), retrying in 5s");
                }
                finally
                {
                    try { client?.Close(); } catch { /* ignored */ }
                }

                // Retry rather than give up: the host often starts their game after the guest does.
                for (int i = 0; i < 50 && running; i++) Thread.Sleep(100);
            }
        }

        public void Stop()
        {
            running = false;
            try { client?.Close(); } catch { /* ignored */ }
        }
    }
}
