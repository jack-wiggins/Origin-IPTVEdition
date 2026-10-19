using System;
using Origin.Core.Iptv;
using Origin.Core.Lineup;
using Origin.Core.SchedulesDirect;
using Origin.Core.Settings;
using Origin.Core.Storage;
using Origin.Playback;

namespace Origin
{
    /// <summary>The application's long-lived services, created once at startup.</summary>
    public sealed class AppServices : IDisposable
    {
        public AppServices(AppPaths paths)
        {
            Paths = paths;
            Settings = new SettingsService(paths.SettingsFile);
            Playlists = new PlaylistStore(paths.PlaylistFile);
            Lineups = new LineupStore(paths.LineupFile);
            SchedulesDirect = new SchedulesDirectService(Settings, paths);
            Lineup = new LineupService(Settings, Playlists, Lineups, SchedulesDirect);
            Playback = new PlaybackService(Settings);
        }

        public AppPaths Paths { get; }

        public SettingsService Settings { get; }

        public PlaylistStore Playlists { get; }

        public LineupStore Lineups { get; }

        public SchedulesDirectService SchedulesDirect { get; }

        public LineupService Lineup { get; }

        public PlaybackService Playback { get; }

        public void Dispose()
        {
            Playback.Dispose();
            SchedulesDirect.Dispose();
        }
    }
}
