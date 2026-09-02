using System;
using System.Diagnostics;
using BannerlordTwitch.Util;
using Microsoft.Owin.Logging;

namespace BLTOverlay
{
    public class LoggerFactory : ILoggerFactory
    {
        public Microsoft.Owin.Logging.ILogger Create(string name)
        {
            return new Logger(name);
        }

        private class Logger : Microsoft.Owin.Logging.ILogger
        {
            private readonly string name;

            internal Logger(string name)
            {
                this.name = name;
            }

            public bool WriteCore(TraceEventType eventType, int eventId, object state, Exception exception, Func<object, Exception, string> formatter)
            {
                // According to docs http://katanaproject.codeplex.com/SourceControl/latest#src/Microsoft.Owin/Logging/ILogger.cs
                // "To check IsEnabled call WriteCore with only TraceEventType and check the return value, no event will be written."
                if (state == null)
                {
                    return true;
                }
                var level = eventType switch
                {
                    TraceEventType.Critical => Log.Level.Critical,
                    TraceEventType.Error => Log.Level.Error,
                    TraceEventType.Warning => Log.Level.Warning,
                    TraceEventType.Information => Log.Level.Information,
                    _ => Log.Level.Trace
                };

                // The overlay's local web server reports a client hanging up as an Error, so
                // every time OBS or a browser tab closes, refreshes or navigates away the log
                // fills with "The specified network name is no longer available". Nothing is
                // wrong - a viewer simply disconnected - so demote these to Trace and keep the
                // Error level meaningful.
                if (level == Log.Level.Error && IsClientDisconnect(exception))
                {
                    level = Log.Level.Trace;
                }
                Log.LogMessage(level, $"[{name}]" + formatter(state, exception));
                return true;
            }

            // Win32/socket codes that all mean "the other end went away mid-request".
            private static bool IsClientDisconnect(Exception exception)
            {
                for (var ex = exception; ex != null; ex = ex.InnerException)
                {
                    // SocketException derives from Win32Exception, so this covers both.
                    int code = ex is System.ComponentModel.Win32Exception w32 ? w32.NativeErrorCode : 0;

                    switch (code)
                    {
                        case 64:    // ERROR_NETNAME_DELETED - "network name is no longer available"
                        case 995:   // ERROR_OPERATION_ABORTED
                        case 1236:  // ERROR_CONNECTION_ABORTED
                        case 10053: // WSAECONNABORTED
                        case 10054: // WSAECONNRESET
                            return true;
                    }
                }

                return false;
            }
        }
    }
}