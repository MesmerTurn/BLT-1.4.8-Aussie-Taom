using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using BannerlordTwitch.Models;
using BannerlordTwitch.Rewards;
using BannerlordTwitch.Util;
using BLTOverlay;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Debug = TaleWorlds.Library.Debug;


namespace BannerlordTwitch
{
    // ReSharper disable once ClassNeverInstantiated.Global
    internal class BLTModule : MBSubModuleBase
    {
        private static Harmony harmony;

        public static TwitchService TwitchService { get; private set; }

        private ExtensionReceiverService extensionReceiver;

        [DllImport("user32.dll")]
        private static extern int SetWindowText(IntPtr hWnd, string text);

        private static readonly string[] SupportedVersions = { "v1.4.7", "v1.4.8" };

        // An unhandled exception on a background thread tears the process down immediately -
        // no crash dialog, and BLT's in-game log dies with it, which is why the Configure
        // Window's Log tab looked clean right up to the moment the game vanished. Write the
        // details straight to disk instead, so there is still something to read afterwards.
        private static readonly string FatalLogPath = Path.Combine(
            Path.GetDirectoryName(typeof(BLTModule).Assembly.Location) ?? string.Empty,
            "BLT_FATAL.log");

        // Appends and closes on every call, so whatever was written survives the process
        // being killed outright - which is what happens here: a native access violation
        // gives no exception and no chance to flush a buffered writer. The last line in the
        // file is therefore the last step that completed before the crash.
        public static void Trace(string message)
        {
            try
            {
                File.AppendAllText(FatalLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostics must never be able to break the game.
            }
        }

        private static void InstallFatalErrorLogging()
        {
            string logPath = FatalLogPath;

            void Write(string kind, object error)
            {
                try
                {
                    File.AppendAllText(logPath,
                        $"==== {kind} @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===={Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
                }
                catch
                {
                    // Never let the crash logger itself throw during a crash.
                }
            }

            // If the crash is assembly/type-load corruption, the last assembly to load is the
            // single most useful clue - and it is recorded even though nothing is ever thrown.
            AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
            {
                try
                {
                    var name = args.LoadedAssembly.GetName();
                    Trace($"ASSEMBLY LOADED: {name.Name} v{name.Version}");
                }
                catch
                {
                    // ignored
                }
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Write("UnhandledException (process is terminating)", args.ExceptionObject);

            // Faulted fire-and-forget Tasks don't kill the process, but they silently hide
            // failures in exactly the async Twitch startup paths we're chasing.
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Write("UnobservedTaskException", args.Exception);
                args.SetObserved();
            };
        }

        static BLTModule()
        {
            InstallFatalErrorLogging();

            if (!SupportedVersions.Any(GameVersion.IsVersion))
            {
                MessageBox.Show("{=IO9rnFpk}This build of the mod is for game version {ExpectedVersion}. You are running game version {GameVersion}. Exiting now."
                    .Translate(
                        ("ExpectedVersion", string.Join(" / ", SupportedVersions)),
                        ("GameVersion", GameVersion.GameVersionString)),
                    "{=Oru6b9Cy}Bannerlord Twitch ERROR".Translate());
                //Application.Current.Shutdown(1);
            }

            // Set a consistent Window title so streaming software can find it
            SetWindowText(Process.GetCurrentProcess().MainWindowHandle, "Bannerlord Game Window");

            MainThreadSync.InitMainThread();

            // AssemblyHelper.Redirect("Microsoft.Extensions.Logging.Abstractions", Version.Parse("3.1.5.0"), "adb9793829ddae60");
            AssemblyHelper.Redirect("Microsoft.Owin", Version.Parse("4.2.2.0"), "31bf3856ad364e35");
            AssemblyHelper.Redirect("Microsoft.Owin.FileSystems", Version.Parse("4.2.2.0"), "31bf3856ad364e35");
            AssemblyHelper.Redirect("Microsoft.Owin.Security", Version.Parse("4.2.2.0"), "31bf3856ad364e35");
            AssemblyHelper.Redirect("Newtonsoft.Json", Version.Parse("13.0.0.0"), "30ad4fe6b2a6aeed");

            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                string folderPath = Path.GetDirectoryName(typeof(BLTModule).Assembly.Location);
                string assemblyPath = Path.Combine(folderPath ?? string.Empty, new AssemblyName(args.Name).Name + ".dll");
                if (!File.Exists(assemblyPath))
                {
                    Debug.Print($"[BLT] Couldn't resolve assembly {args.Name} with {assemblyPath}");
                    return null;
                }
                Debug.Print($"[BLT] Resolved assembly {args.Name} with {assemblyPath}");
                return Assembly.LoadFrom(assemblyPath);
            };
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            if (harmony == null)
            {
                try
                {
                    harmony = new Harmony("mod.bannerlord.bannerlordtwitch");
                    harmony.PatchAll();
                    Log.LogFeedSystem("{=45Q44kgm}Loaded v{ModVersion}".Translate(
                        ("ModVersion", Assembly.GetExecutingAssembly().GetName().Version.ToString(3))));

                    ActionManager.Init();
                    Log.LogFeedSystem("{=5G73vqNS}Action Manager initialized".Translate());
                }
                catch (Exception ex)
                {
                    Log.Exception($"Error Initialising Bannerlord Twitch: {ex.Message}", ex);
                }

                ConsoleFeedHub.Register();

                BLTOverlay.BLTOverlay.Start();
            }
        }

        private void InitializeExtensionReceiver()
        {
            try
            {
                if (TwitchService == null)
                {
                    Log.LogFeedSystem("[Overlay] TwitchService not ready - skipping receiver init");
                    return;
                }

                string channelId = TwitchService.channelId;
                string accessToken = TwitchService.authSettings.AccessToken;

                if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(accessToken))
                {
                    Log.LogFeedSystem("[Overlay] Missing channelId or accessToken");
                    return;
                }

                extensionReceiver = new ExtensionReceiverService(channelId, accessToken);
                extensionReceiver.OnMessageReceived += OnExtensionMessageReceived;
                extensionReceiver.Start();

                Log.LogFeedSystem("[Overlay] Extension receiver started");
            }
            catch (Exception ex)
            {
                Log.Exception("[Overlay] Failed to start extension receiver", ex);
            }
        }

        private void OnExtensionMessageReceived(ExtensionReceiverService.OverlayCommandMessage msg)
        {
            try
            {
                if (msg == null || string.IsNullOrWhiteSpace(msg.Command))
                    return;

                Log.LogFeedSystem($"[Overlay CMD] {msg.Command}");

                // 🚨 THIS is the entire point:
                // Send directly into your existing command pipeline
                TwitchService.ExecuteOverlayRaw(msg.Command, msg.UserName);
            }
            catch (Exception ex)
            {
                Log.Exception("[Overlay] Failed to process command", ex);
            }
        }

        public static void AddToFeed(string text, string style)
        {
            ConsoleFeedHub.SendMessage(text, style);
        }

        public override void OnGameInitializationFinished(Game game)
        {
            if (game.GameType is Campaign)
            {
                object ownerHandle = new();
                CampaignEvents.DailyTickEvent.AddNonSerializedListener(ownerHandle, () =>
                {
                    if (
#if e159
						CampaignOptions.AutoSaveInMinutes
#else
                        Campaign.Current.SaveHandler.AutoSaveInterval
#endif
                        <= 0
                    )
                    {
                        InformationManager.ShowInquiry(
                            new InquiryData(
                                "{=PhRzCo9t}Bannerlord Twitch Mod WARNING".Translate(),
                                "{=7b4tU6y9}You have auto save disabled, crashes could result in lost channel points!\nRecommended you set it to 15 minutes or less.".Translate(),
                                true, false, "{=hpFXglKx}Okay".Translate(), null,
                                () => { }, () => { }), true);
                    }

                    CampaignEvents.DailyTickEvent.ClearListeners(ownerHandle);
                });
            }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            RestartTwitchService();

            InitializeExtensionReceiver();

            try
            {
                if (game.GameType is Campaign)
                {
                    gameStarterObject.AddModel(new BLTAgentStatCalculateModel(gameStarterObject.Models
                        .OfType<AgentStatCalculateModel>().FirstOrDefault()));
                }
            }
            catch (Exception e)
            {
                Log.Exception(nameof(OnGameStart), e);
                MessageBox.Show(
                    "{=C0G8s2Lv}Error in {Location}, please report this on the discord: {Error}"
                        .Translate(
                            ("Location", $"BannerlordTwitch.{nameof(OnGameStart)}"),
                            ("Error", e.ToString())
                            ),
                    "{=cuXwwHRe}Bannerlord Twitch Mod STARTUP ERROR".Translate());
            }
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            MainThreadSync.RunQueued();
        }

        public override void OnGameEnd(Game game)
        {
            TwitchService?.Dispose();
            TwitchService = null;
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            mission.AddMissionBehavior(new BLTAgentModifierBehavior());
            mission.AddMissionBehavior(new BLTAgentPfxBehaviour());
        }

        public static bool RestartTwitchService()
        {
            TwitchService?.Dispose();
            try
            {
                TwitchService = new TwitchService();
                return true;
            }
            catch (Exception ex)
            {
                InformationManager.ShowInquiry(
                    new InquiryData(
                        "{=Sphd7XTS}Bannerlord Twitch Mod DISABLED".Translate(),
                        ex.Message,
                        true, false, "{=hpFXglKx}Okay".Translate(), null,
                        () => { }, () => { }), true);
                TwitchService = null;
                Log.Exception($"TwitchService could not start: {ex.Message}", ex);
                return false;
            }
        }
    }
}
