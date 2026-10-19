using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Origin.Core.Diagnostics;
using Origin.Core.Guide;
using Origin.Core.Iptv;
using Origin.Core.SchedulesDirect;
using Origin.Core.Settings;
using Origin.Core.Xtream;

namespace Origin.Core.Lineup
{
    public sealed class LineupUpdateOptions
    {
        /// <summary>Download a fresh M3U. When false, the saved playlist is reused (e.g. after changing groups).</summary>
        public bool DownloadPlaylist { get; set; } = true;

        /// <summary>Download the Schedules Direct lineup again. When false, the cached copy is used.</summary>
        public bool RefreshSchedulesDirect { get; set; } = true;

        /// <summary>
        /// Download guide listings for the matched stations. When <see cref="RefreshSchedulesDirect"/>
        /// is false, only stations that have no listings yet are downloaded.
        /// </summary>
        public bool DownloadGuide { get; set; } = true;
    }

    public sealed class LineupUpdateProgress
    {
        public LineupUpdateProgress(string stage, string detail = null)
        {
            Stage = stage;
            Detail = detail;
        }

        public string Stage { get; }

        public string Detail { get; }
    }

    public sealed class LineupUpdateResult
    {
        public OriginLineup Lineup { get; set; }

        public int PlaylistEntries { get; set; }

        public int LiveEntries { get; set; }

        public int MovieEntries { get; set; }

        public int SeriesEntries { get; set; }

        public int Channels => Lineup.Channels.Count;

        public int Mapped => Lineup.MappedCount;

        public int Unmapped => Lineup.UnmappedCount;

        public int ManualOverrides => Lineup.Channels.Count(c => c.Override != MappingOverride.None);

        public bool UsedSchedulesDirect { get; set; }

        /// <summary>Null when the guide was not downloaded (nothing matched, or Schedules Direct could not be reached).</summary>
        public GuideUpdateResult Guide { get; set; }

        public List<string> Warnings { get; } = new List<string>();

        public TimeSpan Duration { get; set; }
    }

    public sealed class LineupUpdateException : Exception
    {
        public LineupUpdateException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// Runs "Update lineup" (download the M3U, fetch the Schedules Direct lineup, match, save,
    /// then download the guide) and the manual edits made on the channel mapping screen.
    /// </summary>
    public sealed class LineupService
    {
        private readonly SettingsService _settings;
        private readonly PlaylistStore _playlists;
        private readonly LineupStore _lineups;
        private readonly SchedulesDirectService _schedulesDirect;
        private readonly GuideDownloader _guide;
        private readonly SemaphoreSlim _updateGate = new SemaphoreSlim(1, 1);

        public LineupService(SettingsService settings, PlaylistStore playlists, LineupStore lineups, SchedulesDirectService schedulesDirect, GuideDownloader guide)
        {
            _settings = settings;
            _playlists = playlists;
            _lineups = lineups;
            _schedulesDirect = schedulesDirect;
            _guide = guide;
        }

        public bool IsUpdating => _updateGate.CurrentCount == 0;

        public async Task<LineupUpdateResult> UpdateAsync(LineupUpdateOptions options, IProgress<LineupUpdateProgress> progress, CancellationToken cancellationToken)
        {
            if (!await _updateGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new LineupUpdateException("The lineup is already being updated.");
            }

            try
            {
                return await RunUpdateAsync(options ?? new LineupUpdateOptions(), progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _updateGate.Release();
            }
        }

        /// <summary>A matcher over the cached Schedules Direct lineup, for manual matching. Null if none is cached.</summary>
        public ChannelMatcher CreateMatcherFromCache()
        {
            var lineupId = _settings.Current.SchedulesDirect.LineupId;
            var map = _schedulesDirect.LoadCachedLineupMap(lineupId);
            return map == null ? null : new ChannelMatcher(ChannelMatcher.FromLineupMap(map), _settings.Current.Lineup.FuzzyMatchThreshold);
        }

        public void SetManualStation(string channelKey, LineupStation station)
        {
            _lineups.UpdateChannel(channelKey, channel =>
            {
                channel.Override = MappingOverride.Station;
                channel.Mapping = station.ToMapping(MatchMethod.Manual, 1.0);
            });
        }

        public void SetNoMatch(string channelKey)
        {
            _lineups.UpdateChannel(channelKey, channel =>
            {
                channel.Override = MappingOverride.NoMatch;
                channel.Mapping = null;
            });
        }

        /// <summary>Drops a manual choice and lets automatic matching decide again.</summary>
        public void ResetToAutomatic(string channelKey, ChannelMatcher matcher)
        {
            var source = _playlists.Load()?.Channels.FirstOrDefault(c => c.Key == channelKey);
            _lineups.UpdateChannel(channelKey, channel =>
            {
                channel.Override = MappingOverride.None;
                if (matcher == null)
                {
                    channel.Mapping = null;
                    return;
                }

                var match = matcher.Match(channel.Name, channel.TvgId, source?.TvgName, source?.ChannelNumber);
                channel.Mapping = match.IsMatch ? match.Station.ToMapping(match.Method, match.Confidence) : null;
            });
        }

        public void SetEnabled(string channelKey, bool enabled)
        {
            _lineups.UpdateChannel(channelKey, channel => channel.Enabled = enabled);
        }

        private async Task<LineupUpdateResult> RunUpdateAsync(LineupUpdateOptions options, IProgress<LineupUpdateProgress> progress, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var settings = _settings.Current;
            var result = new LineupUpdateResult();

            // 1. The playlist.
            PlaylistSnapshot playlist;
            if (options.DownloadPlaylist)
            {
                playlist = await DownloadPlaylistAsync(progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                playlist = _playlists.Load()
                           ?? throw new LineupUpdateException("No playlist has been downloaded yet. Run Update Lineup first.");
            }

            result.PlaylistEntries = playlist.TotalEntries;
            result.LiveEntries = playlist.Count(ContentKind.Live);
            result.MovieEntries = playlist.Count(ContentKind.Movie);
            result.SeriesEntries = playlist.Count(ContentKind.Series);

            if (result.LiveEntries == 0)
            {
                throw new LineupUpdateException("The playlist has no live TV channels (only movies or series).");
            }

            // 2. The Schedules Direct lineup the playlist is mapped onto.
            var sd = settings.SchedulesDirect;
            SdLineupMap map = null;
            var keepPreviousMatches = false;

            if (string.IsNullOrEmpty(sd.LineupId))
            {
                result.Warnings.Add("No Schedules Direct lineup is selected, so channels were added without guide matches.");
            }
            else
            {
                if (options.RefreshSchedulesDirect && _schedulesDirect.IsConfigured)
                {
                    try
                    {
                        progress?.Report(new LineupUpdateProgress("Downloading the Schedules Direct lineup", sd.LineupName ?? sd.LineupId));
                        map = await _schedulesDirect.DownloadLineupMapAsync(sd.LineupId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (SdApiException ex)
                    {
                        Log.Warn("Schedules Direct lineup download failed", ex);
                        map = _schedulesDirect.LoadCachedLineupMap(sd.LineupId);
                        result.Warnings.Add(ex.Message + (map != null ? " The last saved copy of the lineup was used." : string.Empty));
                    }
                }
                else
                {
                    map = _schedulesDirect.LoadCachedLineupMap(sd.LineupId);
                }

                if (map == null)
                {
                    keepPreviousMatches = true;
                    result.Warnings.Add("The Schedules Direct lineup is not available right now, so existing matches were kept.");
                }
            }

            result.UsedSchedulesDirect = map != null;

            // 3. Copy the live channels into Origin's lineup and match them.
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new LineupUpdateProgress(
                map != null ? "Matching channels to Schedules Direct" : "Building the channel lineup",
                result.LiveEntries.ToString("N0") + " live channels"));

            var lineup = await Task.Run(
                () => LineupComposer.Compose(playlist, settings.Lineup, map, sd.LineupId, sd.LineupName, _lineups.Current, keepPreviousMatches),
                cancellationToken).ConfigureAwait(false);

            if (lineup.Channels.Count == 0)
            {
                result.Warnings.Add("Every playlist group is turned off, so the lineup is empty. Choose groups under Settings › channel groups.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new LineupUpdateProgress("Saving the lineup"));
            _lineups.Replace(lineup);

            result.Lineup = lineup;
            Log.Info($"Lineup updated: {result.Channels} channels, {result.Mapped} matched, {result.Unmapped} unmatched in {stopwatch.Elapsed.TotalSeconds:0.0}s");

            // 4. Guide listings for the matched stations. The lineup is saved by now, so problems here are warnings.
            if (options.DownloadGuide)
            {
                result.Guide = await DownloadGuideAsync(lineup, options, result, progress, cancellationToken).ConfigureAwait(false);
            }

            result.Duration = stopwatch.Elapsed;
            return result;
        }

        /// <summary>
        /// Brings the guide up to date for the lineup's matched stations. Never fails the update:
        /// if Schedules Direct is down, or the user cancels, the guide keeps what it had.
        /// </summary>
        private async Task<GuideUpdateResult> DownloadGuideAsync(
            OriginLineup lineup, LineupUpdateOptions options, LineupUpdateResult result, IProgress<LineupUpdateProgress> progress, CancellationToken cancellationToken)
        {
            var stations = lineup.Channels
                .Where(c => c.Enabled && c.IsMapped)
                .Select(c => c.Mapping.StationId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (stations.Count > 0 && !_schedulesDirect.IsConfigured)
            {
                result.Warnings.Add("Sign in to Schedules Direct under Settings to download guide listings.");
                return null;
            }

            try
            {
                var guide = await _guide.UpdateAsync(stations, onlyMissing: !options.RefreshSchedulesDirect, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (guide.Stations > 0 && guide.StationsWithListings == 0)
                {
                    result.Warnings.Add("Schedules Direct has no guide listings for your channels yet. Try updating again later.");
                }

                return guide;
            }
            catch (SdApiException ex)
            {
                Log.Warn("Guide download failed", ex);
                result.Warnings.Add(ex.Message + " The guide keeps the listings it already had.");
            }
            catch (JsonException ex)
            {
                Log.Warn("Guide download failed", ex);
                result.Warnings.Add("Schedules Direct sent guide data Origin could not read. The guide keeps the listings it already had.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn("Guide download failed", ex);
                result.Warnings.Add("The guide listings could not be saved: " + ex.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result.Warnings.Add("The guide download was cancelled. The guide shows what had been downloaded so far.");
            }

            return null;
        }

        private async Task<PlaylistSnapshot> DownloadPlaylistAsync(IProgress<LineupUpdateProgress> progress, CancellationToken cancellationToken)
        {
            var account = _settings.GetXtreamAccount()
                          ?? throw new LineupUpdateException("Set up your IPTV provider first (Settings › iptv provider).");

            const string stage = "Downloading your playlist";
            progress?.Report(new LineupUpdateProgress(stage, account.Host));

            var bytes = new Progress<long>(count => progress?.Report(new LineupUpdateProgress(stage, (count / 1048576.0).ToString("0.0") + " MB received")));

            M3uPlaylist m3u;
            try
            {
                using (var client = new XtreamClient(_settings.Current.Xtream.UserAgent))
                {
                    m3u = await client.DownloadPlaylistAsync(account, bytes, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (XtreamException ex)
            {
                throw new LineupUpdateException(ex.Message, ex);
            }

            progress?.Report(new LineupUpdateProgress("Reading the playlist", m3u.Entries.Count.ToString("N0") + " entries"));
            var snapshot = new PlaylistSnapshot
            {
                DownloadedUtc = DateTime.UtcNow,
                Source = account.Host,
                TotalEntries = m3u.Entries.Count,
                Channels = PlaylistImporter.Import(m3u, account),
            };

            _playlists.Save(snapshot);
            return snapshot;
        }
    }
}
