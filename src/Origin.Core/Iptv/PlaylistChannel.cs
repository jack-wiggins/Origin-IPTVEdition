using System;
using System.Collections.Generic;
using System.Linq;

namespace Origin.Core.Iptv
{
    public enum ContentKind
    {
        Live,
        Movie,
        Series,
    }

    /// <summary>One playlist entry after import: everything Origin needs, no credentials.</summary>
    public sealed class PlaylistChannel
    {
        public const string UncategorizedGroup = "Uncategorized";

        /// <summary>Stable identity across playlist refreshes, e.g. "xc:live:12345".</summary>
        public string Key { get; set; }

        /// <summary>Position in the provider's playlist (0-based).</summary>
        public int Order { get; set; }

        public string Name { get; set; }

        public string TvgId { get; set; }

        public string TvgName { get; set; }

        public string LogoUrl { get; set; }

        public string Group { get; set; } = UncategorizedGroup;

        /// <summary>tvg-chno, if the provider numbers its channels.</summary>
        public string ChannelNumber { get; set; }

        public ContentKind Kind { get; set; }

        public StreamReference Stream { get; set; }

        /// <summary>#EXTVLCOPT options (user agent, referrer) to pass to the player.</summary>
        public List<string> PlayerOptions { get; set; }
    }

    /// <summary>The last downloaded playlist, saved so the lineup can be rebuilt without downloading again.</summary>
    public sealed class PlaylistSnapshot
    {
        public DateTime DownloadedUtc { get; set; }

        /// <summary>The provider's host name (never the full URL, which contains credentials).</summary>
        public string Source { get; set; }

        public int TotalEntries { get; set; }

        public List<PlaylistChannel> Channels { get; set; } = new List<PlaylistChannel>();

        public int Count(ContentKind kind) => Channels.Count(c => c.Kind == kind);

        /// <summary>Live TV groups in the order the provider lists them, with channel counts.</summary>
        public IReadOnlyList<PlaylistGroup> GetLiveGroups()
        {
            var groups = new List<PlaylistGroup>();
            var index = new Dictionary<string, PlaylistGroup>(StringComparer.Ordinal);
            foreach (var channel in Channels.Where(c => c.Kind == ContentKind.Live).OrderBy(c => c.Order))
            {
                if (!index.TryGetValue(channel.Group, out var group))
                {
                    group = new PlaylistGroup(channel.Group);
                    index.Add(channel.Group, group);
                    groups.Add(group);
                }

                group.ChannelCount++;
            }

            return groups;
        }
    }

    public sealed class PlaylistGroup
    {
        public PlaylistGroup(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public int ChannelCount { get; internal set; }
    }
}
