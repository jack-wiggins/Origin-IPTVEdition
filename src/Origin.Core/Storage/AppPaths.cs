using System;
using System.IO;
using System.Linq;

namespace Origin.Core.Storage
{
    /// <summary>Locations of everything Origin keeps on disk.</summary>
    public sealed class AppPaths
    {
        public AppPaths(string root)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
            Logs = Path.Combine(root, "logs");
            Cache = Path.Combine(root, "cache");

            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Logs);
            Directory.CreateDirectory(Cache);
        }

        /// <summary>%LOCALAPPDATA%\OriginHTPC (not "Origin", which EA's game launcher uses).</summary>
        public static AppPaths CreateDefault()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new AppPaths(Path.Combine(localAppData, "OriginHTPC"));
        }

        public string Root { get; }

        public string Logs { get; }

        public string Cache { get; }

        public string SettingsFile => Path.Combine(Root, "settings.json");

        /// <summary>The last downloaded M3U, parsed and with credentials removed.</summary>
        public string PlaylistFile => Path.Combine(Root, "playlist.json");

        /// <summary>Origin's channel lineup: playlist channels mapped onto Schedules Direct stations.</summary>
        public string LineupFile => Path.Combine(Root, "lineup.json");

        /// <summary>Downloaded schedules and program details (see <c>GuideStore</c>).</summary>
        public string GuideDirectory => Path.Combine(Cache, "guide");

        public string GetSchedulesDirectLineupFile(string lineupId)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(lineupId.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return Path.Combine(Cache, "sd-lineup-" + safe + ".json");
        }
    }
}
