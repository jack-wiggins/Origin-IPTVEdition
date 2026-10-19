using System;
using System.Collections.Generic;
using Origin.Core.Diagnostics;
using Origin.Core.Security;
using Origin.Core.Storage;
using Origin.Core.Xtream;

namespace Origin.Core.Settings
{
    /// <summary>Loads, saves and hands out <see cref="OriginSettings"/>, and handles the saved credentials.</summary>
    public sealed class SettingsService
    {
        private readonly object _sync = new object();
        private readonly string _path;

        public SettingsService(string path)
        {
            _path = path;
            Current = JsonFile.TryLoad<OriginSettings>(path) ?? new OriginSettings();
            Normalize(Current);
            RegisterSecrets();
        }

        public OriginSettings Current { get; }

        public event EventHandler Saved;

        public void Save()
        {
            lock (_sync)
            {
                JsonFile.Save(_path, Current);
            }

            RegisterSecrets();
            Saved?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The configured Xtream Codes account, or null if it is incomplete.</summary>
        public XtreamAccount GetXtreamAccount()
        {
            var xtream = Current.Xtream;
            if (!xtream.IsConfigured)
            {
                return null;
            }

            var password = SecretProtector.Unprotect(xtream.ProtectedPassword);
            if (password == null)
            {
                return null;
            }

            return new XtreamAccount(xtream.ServerUrl, xtream.Username, password, xtream.OutputFormat);
        }

        public void SetXtreamPassword(string password)
        {
            Current.Xtream.ProtectedPassword = SecretProtector.Protect(password);
        }

        /// <summary>The SHA-1 password hash Schedules Direct expects, or null.</summary>
        public string GetSchedulesDirectPasswordHash()
        {
            return SecretProtector.Unprotect(Current.SchedulesDirect.ProtectedPasswordHash);
        }

        public void SetSchedulesDirectPasswordHash(string passwordHash)
        {
            Current.SchedulesDirect.ProtectedPasswordHash = SecretProtector.Protect(passwordHash);
        }

        private void RegisterSecrets()
        {
            Log.RegisterSecret(Current.Xtream.Username);
            Log.RegisterSecret(SecretProtector.Unprotect(Current.Xtream.ProtectedPassword));
            Log.RegisterSecret(GetSchedulesDirectPasswordHash());
        }

        private static void Normalize(OriginSettings settings)
        {
            settings.Xtream = settings.Xtream ?? new XtreamSettings();
            settings.SchedulesDirect = settings.SchedulesDirect ?? new SchedulesDirectSettings();
            settings.Lineup = settings.Lineup ?? new LineupSettings();
            settings.Playback = settings.Playback ?? new PlaybackSettings();
            settings.Interface = settings.Interface ?? new InterfaceSettings();

            if (settings.Xtream.OutputFormat != "m3u8")
            {
                settings.Xtream.OutputFormat = "ts";
            }

            if (string.IsNullOrWhiteSpace(settings.SchedulesDirect.ApiBaseUrl))
            {
                settings.SchedulesDirect.ApiBaseUrl = SchedulesDirectSettings.DefaultApiBaseUrl;
            }

            if (settings.Lineup.Groups == null)
            {
                settings.Lineup.Groups = new Dictionary<string, bool>(StringComparer.Ordinal);
            }

            if (settings.Lineup.FuzzyMatchThreshold <= 0 || settings.Lineup.FuzzyMatchThreshold > 1)
            {
                settings.Lineup.FuzzyMatchThreshold = 0.82;
            }
        }
    }
}
