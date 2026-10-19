using System;
using System.Collections.Generic;

namespace Origin.Core.Iptv
{
    /// <summary>A parsed M3U / M3U Plus playlist.</summary>
    public sealed class M3uPlaylist
    {
        public bool HasHeader { get; set; }

        /// <summary>Attributes on the #EXTM3U line, e.g. url-tvg.</summary>
        public IDictionary<string, string> HeaderAttributes { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public List<M3uEntry> Entries { get; } = new List<M3uEntry>();
    }

    /// <summary>One #EXTINF entry and its URL.</summary>
    public sealed class M3uEntry
    {
        public double Duration { get; set; } = -1;

        /// <summary>The display name after the comma on the #EXTINF line.</summary>
        public string Title { get; set; }

        public string Url { get; set; }

        public IDictionary<string, string> Attributes { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>#EXTVLCOPT lines, e.g. "http-user-agent=...".</summary>
        public IList<string> Options { get; } = new List<string>();

        /// <summary>The #EXTGRP directive, used when group-title is missing.</summary>
        public string GroupDirective { get; set; }

        public string TvgId => GetAttribute("tvg-id");

        public string TvgName => GetAttribute("tvg-name");

        public string TvgLogo => GetAttribute("tvg-logo") ?? GetAttribute("logo");

        public string GroupTitle => GetAttribute("group-title") ?? Trimmed(GroupDirective);

        public string ChannelNumber => GetAttribute("tvg-chno") ?? GetAttribute("channel-number") ?? GetAttribute("tvg-num");

        public string GetAttribute(string name)
        {
            return Attributes.TryGetValue(name, out var value) ? Trimmed(value) : null;
        }

        private static string Trimmed(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
