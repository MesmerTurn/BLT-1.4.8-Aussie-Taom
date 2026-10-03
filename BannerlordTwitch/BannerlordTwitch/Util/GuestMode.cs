using System;
using System.IO;
using System.Linq;

namespace BannerlordTwitch.Util
{
    /// <summary>
    /// Co-op guest mode: drop BLT_GUEST_MODE.txt next to BannerlordTwitch.dll with the host's
    /// Twitch channel name on the first line (lines starting with # are ignored).
    ///
    /// BLT then joins that channel anonymously - read only, no auth, no channel points, no chat
    /// replies - and only runs commands locally, so the second player in a co-op session sees the
    /// same summons and retinue as the host.
    ///
    /// Just as important is what a guest must NOT do: the campaign itself belongs to the host and
    /// is synced by the co-op mod, so anything that writes campaign state from the guest side is
    /// skipped. Replacing clan banners on the guest was enough to take the game down.
    /// </summary>
    public static class GuestMode
    {
        private const string MarkerFile = "BLT_GUEST_MODE.txt";

        private static bool read;
        private static string channel;

        public static string Channel
        {
            get
            {
                if (!read)
                {
                    read = true;
                    channel = ReadChannel();
                }
                return channel;
            }
        }

        public static bool IsActive => !string.IsNullOrEmpty(Channel);

        private static string ReadChannel()
        {
            try
            {
                string path = Path.Combine(
                    Path.GetDirectoryName(typeof(GuestMode).Assembly.Location) ?? string.Empty,
                    MarkerFile);
                if (!File.Exists(path)) return null;

                return File.ReadAllLines(path)
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#"))
                    ?.TrimStart('@')
                    .ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to read {MarkerFile}: {ex.Message}");
                return null;
            }
        }
    }
}
