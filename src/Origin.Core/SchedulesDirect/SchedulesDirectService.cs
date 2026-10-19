using System;
using System.Threading;
using System.Threading.Tasks;
using Origin.Core.Diagnostics;
using Origin.Core.Settings;
using Origin.Core.Storage;

namespace Origin.Core.SchedulesDirect
{
    /// <summary>
    /// Keeps one signed-in Schedules Direct session for the app, re-signing in when the token
    /// expires, and caches lineup maps on disk so the lineup can be rebuilt offline.
    /// </summary>
    public sealed class SchedulesDirectService : IDisposable
    {
        private readonly SettingsService _settings;
        private readonly AppPaths _paths;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private SdClient _client;
        private string _clientBaseUrl;
        private string _signedInUser;

        public SchedulesDirectService(SettingsService settings, AppPaths paths)
        {
            _settings = settings;
            _paths = paths;
        }

        public bool IsConfigured => _settings.Current.SchedulesDirect.IsConfigured;

        public bool IsSignedIn => _client != null && _client.HasValidToken;

        /// <summary>Signs in with explicit credentials (from the settings screen). Does not save them.</summary>
        public async Task SignInAsync(string username, string passwordHash, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var client = GetClient();
                client.SignOut();
                await client.LoginAsync(username.Trim(), passwordHash, cancellationToken).ConfigureAwait(false);
                _signedInUser = username.Trim();
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Runs <paramref name="call"/> with a valid session, signing in (again) with the saved credentials if needed.</summary>
        public async Task<T> CallAsync<T>(Func<SdClient, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
        {
            var client = await EnsureSignedInAsync(forceNewToken: false, cancellationToken).ConfigureAwait(false);
            try
            {
                return await call(client, cancellationToken).ConfigureAwait(false);
            }
            catch (SdApiException ex) when (ex.IsTokenProblem)
            {
                Log.Info("Schedules Direct token rejected; signing in again.");
                client = await EnsureSignedInAsync(forceNewToken: true, cancellationToken).ConfigureAwait(false);
                return await call(client, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<SdLineupMap> DownloadLineupMapAsync(string lineupId, CancellationToken cancellationToken)
        {
            var map = await CallAsync((client, ct) => client.GetLineupMapAsync(lineupId, ct), cancellationToken).ConfigureAwait(false);
            map.DownloadedUtc = DateTime.UtcNow;
            JsonFile.Save(_paths.GetSchedulesDirectLineupFile(lineupId), map, indented: false);
            Log.Info("Schedules Direct lineup " + lineupId + ": " + map.Stations.Count + " stations, " + map.Map.Count + " channels");
            return map;
        }

        /// <summary>The last downloaded copy of a lineup, or null.</summary>
        public SdLineupMap LoadCachedLineupMap(string lineupId)
        {
            return string.IsNullOrEmpty(lineupId) ? null : JsonFile.TryLoad<SdLineupMap>(_paths.GetSchedulesDirectLineupFile(lineupId));
        }

        public void Dispose()
        {
            _client?.Dispose();
            _gate.Dispose();
        }

        private async Task<SdClient> EnsureSignedInAsync(bool forceNewToken, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var settings = _settings.Current.SchedulesDirect;
                var client = GetClient();
                var sameUser = string.Equals(_signedInUser, settings.Username?.Trim(), StringComparison.OrdinalIgnoreCase);

                if (!forceNewToken && client.HasValidToken && (sameUser || !settings.IsConfigured))
                {
                    return client;
                }

                if (!settings.IsConfigured)
                {
                    throw new SdApiException("Schedules Direct is not set up. Add your account in Settings.");
                }

                var hash = _settings.GetSchedulesDirectPasswordHash();
                if (hash == null)
                {
                    throw new SdApiException("The saved Schedules Direct password could not be read. Enter it again in Settings.");
                }

                await client.LoginAsync(settings.Username.Trim(), hash, cancellationToken).ConfigureAwait(false);
                _signedInUser = settings.Username.Trim();
                return client;
            }
            finally
            {
                _gate.Release();
            }
        }

        private SdClient GetClient()
        {
            var baseUrl = _settings.Current.SchedulesDirect.ApiBaseUrl;
            if (_client == null || !string.Equals(_clientBaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                _client?.Dispose();
                _client = new SdClient(baseUrl);
                _clientBaseUrl = baseUrl;
            }

            return _client;
        }
    }
}
