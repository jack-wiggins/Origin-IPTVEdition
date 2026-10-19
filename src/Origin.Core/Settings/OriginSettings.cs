using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Origin.Core.Settings
{
    /// <summary>Everything the user configures. Saved to settings.json.</summary>
    public sealed class OriginSettings
    {
        public int Version { get; set; } = 1;

        public XtreamSettings Xtream { get; set; } = new XtreamSettings();

        public SchedulesDirectSettings SchedulesDirect { get; set; } = new SchedulesDirectSettings();

        public LineupSettings Lineup { get; set; } = new LineupSettings();

        public PlaybackSettings Playback { get; set; } = new PlaybackSettings();

        public InterfaceSettings Interface { get; set; } = new InterfaceSettings();
    }

    public sealed class XtreamSettings
    {
        public string ServerUrl { get; set; }

        public string Username { get; set; }

        /// <summary>DPAPI-encrypted. Use <see cref="SettingsService.GetXtreamAccount"/> to read it.</summary>
        public string ProtectedPassword { get; set; }

        /// <summary>"ts" (MPEG-TS) or "m3u8" (HLS).</summary>
        public string OutputFormat { get; set; } = "ts";

        /// <summary>Optional. Some providers only answer to specific player user agents.</summary>
        public string UserAgent { get; set; }

        [JsonIgnore]
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ServerUrl)
            && !string.IsNullOrWhiteSpace(Username)
            && !string.IsNullOrEmpty(ProtectedPassword);
    }

    public sealed class SchedulesDirectSettings
    {
        public const string DefaultApiBaseUrl = "https://json.schedulesdirect.org/20141201/";

        public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

        public string Username { get; set; }

        /// <summary>DPAPI-encrypted SHA-1 of the password; the plain password is never stored.</summary>
        public string ProtectedPasswordHash { get; set; }

        public string Country { get; set; } = "USA";

        public string PostalCode { get; set; }

        /// <summary>The Schedules Direct lineup Origin maps the playlist onto, e.g. "USA-OTA-60030".</summary>
        public string LineupId { get; set; }

        public string LineupName { get; set; }

        [JsonIgnore]
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(ProtectedPasswordHash);
    }

    public enum ChannelNumbering
    {
        /// <summary>tvg-chno from the M3U, otherwise playlist order.</summary>
        Playlist,

        /// <summary>The Schedules Direct channel number of the matched station.</summary>
        SchedulesDirect,
    }

    public sealed class LineupSettings
    {
        public ChannelNumbering Numbering { get; set; } = ChannelNumbering.Playlist;

        /// <summary>Minimum similarity (0-1) for a name-only match to be accepted.</summary>
        public double FuzzyMatchThreshold { get; set; } = 0.82;

        /// <summary>Whether playlist groups the user has not decided on yet are included.</summary>
        public bool IncludeNewGroups { get; set; } = true;

        /// <summary>Explicit include (true) / exclude (false) choices per playlist group-title.</summary>
        public Dictionary<string, bool> Groups { get; set; } = new Dictionary<string, bool>(StringComparer.Ordinal);

        public bool IsGroupIncluded(string group)
        {
            if (Groups != null && group != null && Groups.TryGetValue(group, out var included))
            {
                return included;
            }

            return IncludeNewGroups;
        }
    }

    public sealed class PlaybackSettings
    {
        public int NetworkCachingMs { get; set; } = 1500;

        public bool HardwareDecoding { get; set; } = true;

        public string LastChannelKey { get; set; }
    }

    public enum BackgroundQuality
    {
        /// <summary>Static gradient; no DirectX rendering.</summary>
        Off,

        /// <summary>DirectX ribbons rendered at up to 1280 pixels wide and scaled.</summary>
        Balanced,

        /// <summary>DirectX ribbons rendered at up to 1920 pixels wide.</summary>
        High,
    }

    public sealed class InterfaceSettings
    {
        public bool StartFullScreen { get; set; } = true;

        public BackgroundQuality Background { get; set; } = BackgroundQuality.Balanced;
    }
}
