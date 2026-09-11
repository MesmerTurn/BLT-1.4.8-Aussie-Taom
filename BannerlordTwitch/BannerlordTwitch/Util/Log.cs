
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using Debug = TaleWorlds.Library.Debug;

namespace BannerlordTwitch.Util
{
    public static class Log
    {
        //private static readonly string LogPath = $@"C:\ProgramData\Mount and Blade II Bannerlord\logs\log{DateTime.Now:yyyyMMddHHmmss}.txt";

        public enum Level
        {
            Trace,
            Debug,
            Information,
            Warning,
            Error,
            Critical,
            None,
        }

        private class LogTraceListener : TraceListener
        {
            private string pending;
            public override void Write(string message)
            {
                pending += message;
                //Log.Trace(message);
            }

            public override void WriteLine(string message)
            {
                Log.Trace(pending + message);
                pending = string.Empty;
            }
        }

        static Log()
        {
            // Useless, just catching broken stuff in other mods:
            // System.Diagnostics.Trace.Listeners.Add(new LogTraceListener());
            // System.Diagnostics.Debug.Listeners.Add(new LogTraceListener());
        }

        public static event Action<Level, string> OnLog;

        public static void LogMessage(Level level, string str)
        {
            RaiseLogEvent(level, str);
            WriteToFile(level, str);
            MainThreadSync.Run(() => Debug.Print($"[BLT][{level}][{DateTime.Now:mmss}] {str}"));
        }

        /// <summary>
        /// Where the log file lives: beside the save games, which is the one folder a player can
        /// already find without being talked through it.
        /// </summary>
        public static string LogFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord", "BLT_Log.txt");

        private static readonly object fileLock = new();
        private static bool fileLoggingBroken;

        /// <summary>
        /// Writes the serious lines to a file on disk.
        ///
        /// This used to be commented out, which meant an error existed only in the overlay feed
        /// and in Debug.Print - and Debug.Print goes nowhere at all without a debugger attached.
        /// So every crash guard in this build was faithfully recording which clan, settlement or
        /// hero was at fault, into nothing, and players were being asked for lines they had no
        /// way to find.
        ///
        /// Errors and warnings only. Trace runs many times a second in a battle and would turn
        /// this into a performance problem rather than a diagnostic one.
        /// </summary>
        private static void WriteToFile(Level level, string str)
        {
            if (fileLoggingBroken) return;
            if (level != Level.Error && level != Level.Critical && level != Level.Warning) return;

            try
            {
                lock (fileLock)
                {
                    string path = LogFilePath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    // A campaign played for months would otherwise grow this without limit.
                    const long MaxBytes = 8 * 1024 * 1024;
                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    {
                        string previous = path + ".old";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }

                    File.AppendAllText(path,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {str}{Environment.NewLine}");
                }
            }
            catch
            {
                // A log that cannot be written must never be the reason something fails. Give up
                // permanently rather than throwing on every line from here on.
                fileLoggingBroken = true;
            }
        }

        public static void Trace(string str) => LogMessage(Level.Trace, str);

        public static void Info(string str) => LogMessage(Level.Information, str);

        public static void Fatal(string str)
        {
            LogMessage(Level.Critical, str);
            LogFeedFatal(str);
        }

        public static void Error(string str)
        {
            LogMessage(Level.Error, str);
            LogFeedError(str);
        }

        private static readonly ConcurrentBag<string> reportedExceptions = new();

        private static string GetSolutionRoot([CallerFilePath] string path = null)
            => path?.Replace("BannerlordTwitch\\Util\\Log.cs", "") ?? string.Empty;

        private static string GetExceptionStr(Exception ex)
            => ex?.GetBaseException().ToString().Replace(GetSolutionRoot(), "") ?? string.Empty;

        public static void Exception(string context, Exception ex, bool noRethrow = false)
        {
            string expId = context + (ex.GetBaseException().Message ?? "unknown");
            if (!reportedExceptions.Contains(expId))
            {
                Fatal($"{context}: {GetExceptionStr(ex)}");
                reportedExceptions.Add(expId);
            }
            else
            {
                Trace($"(repeat) {context}: {GetExceptionStr(ex)}");
            }
#if DEBUG
            if(!noRethrow) throw ex;
#endif
        }

        // public static void Screen(string str, Color color = default)
        // {
        //     MainThreadSync.Run(() =>
        //     {
        //         LogFilePrint(str);
        //         InformationManager.DisplayMessage(new InformationMessage("BLT: " + str,
        //             color == default
        //                 ? new Color(31 / 255f, 195 / 255f, 255 / 255f)
        //                 : color
        //             ,
        //             "event:/ui/notification/quest_finished"));
        //     });
        // }

        private static void LogFeedError(string str) => LogFeed("!ERROR!: " + str, LogStyle.Fail);
        private static void LogFeedFatal(string str) => LogFeed("!!FATAL!!: " + str, LogStyle.Critical);

        public static void LogFeedSystem(string str) => LogFeed(str, LogStyle.System);
        public static void LogFeedBattle(string str) => LogFeed(str, LogStyle.Battle);
        public static void LogFeedEvent(string str) => LogFeed(str, LogStyle.Event);
        public static void LogFeedResponse(string userName, params string[] messages) => LogFeed($"@{userName}: {string.Join(", ", messages)}", LogStyle.Response);
        public static void LogFeedMessage(params string[] messages) => LogFeed(string.Join(", ", messages), LogStyle.General);

        private enum LogStyle
        {
            General,
            Fail,
            Critical,
            System,
            Battle,
            Event,
            Response,
        }

        private static void LogFeed(string str, LogStyle style)
        {
            BLTModule.AddToFeed(str, style.ToString().ToLower());
            var level = style switch
            {
                LogStyle.Battle => Level.Information,
                LogStyle.General => Level.Information,
                LogStyle.Fail => Level.Error,
                LogStyle.Critical => Level.Critical,
                LogStyle.System => Level.Information,
                LogStyle.Event => Level.Information,
                LogStyle.Response => Level.Information,
                _ => throw new ArgumentOutOfRangeException(nameof(style), style, null)
            };
            LogMessage(level, str);
        }

        public enum Sound
        {
            None,
            Horns,
            Horns2,
            Horns3,
            Notification1,
        }

        public static void ShowInformation(string message, BasicCharacterObject characterObject = null, Sound sound = Sound.None)
        {
            string soundStr = sound switch
            {
                Sound.None => null,
                Sound.Horns => "event:/ui/mission/horns/attack",
                Sound.Horns2 => "event:/ui/mission/horns/move",
                Sound.Horns3 => "event:/ui/mission/horns/retreat",
                Sound.Notification1 => "event:/ui/notification/levelup",
                _ => throw new ArgumentOutOfRangeException(nameof(sound), sound, null)
            };
            MBInformationManager.AddQuickInformation(new TextObject(message), 1000, characterObject, null, soundStr);
        }

        public static long TimeFunction(Action action)
        {
            var sw = new Stopwatch();
            sw.Start();
            action();
            return sw.ElapsedMilliseconds;
        }

        private static void RaiseLogEvent(Level level, string msg) => OnLog?.Invoke(level, msg);
    }
}