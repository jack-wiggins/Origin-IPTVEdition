using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Origin.Core.Xtream;

namespace Origin.Core.Iptv
{
    /// <summary>Turns parsed M3U entries into <see cref="PlaylistChannel"/>s, removing credentials from stream URLs.</summary>
    public static class PlaylistImporter
    {
        private static readonly string[] KindSegments = { "live", "movie", "series" };
        private static readonly string[] VideoFileExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v" };

        public static List<PlaylistChannel> Import(M3uPlaylist playlist, XtreamAccount account)
        {
            var channels = new List<PlaylistChannel>(playlist.Entries.Count);
            var keys = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < playlist.Entries.Count; i++)
            {
                var entry = playlist.Entries[i];
                if (string.IsNullOrWhiteSpace(entry.Url))
                {
                    continue;
                }

                var channel = Import(entry, i, account);

                // Providers often list the same stream in several groups; keep each listing distinct.
                if (keys.TryGetValue(channel.Key, out var seen))
                {
                    keys[channel.Key] = seen + 1;
                    channel.Key = channel.Key + "~" + (seen + 1);
                }
                else
                {
                    keys[channel.Key] = 1;
                }

                channels.Add(channel);
            }

            return channels;
        }

        internal static PlaylistChannel Import(M3uEntry entry, int order, XtreamAccount account)
        {
            var stream = ParseStream(entry.Url.Trim(), account);
            var kind = GetKind(stream);

            var name = entry.Title ?? entry.TvgName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Channel " + (order + 1);
            }

            return new PlaylistChannel
            {
                Key = stream.IsXtream
                    ? "xc:" + (stream.Segment ?? "live") + ":" + stream.StreamId
                    : "url:" + ShortHash(stream.DirectUrl),
                Order = order,
                Name = name.Trim(),
                TvgId = entry.TvgId,
                TvgName = entry.TvgName,
                LogoUrl = entry.TvgLogo,
                Group = entry.GroupTitle ?? PlaylistChannel.UncategorizedGroup,
                ChannelNumber = entry.ChannelNumber,
                Kind = kind,
                Stream = stream,
                PlayerOptions = entry.Options.Count > 0 ? entry.Options.ToList() : null,
            };
        }

        /// <summary>
        /// Recognises http://host:port/[live|movie|series/]{username}/{password}/{id}[.ext] for the
        /// given account. Anything else is kept as a direct URL.
        /// </summary>
        internal static StreamReference ParseStream(string url, XtreamAccount account)
        {
            var direct = new StreamReference { DirectUrl = url };
            if (account == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Query))
            {
                return direct;
            }

            var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();

            if (segments.Length < 3)
            {
                return direct;
            }

            var userIndex = segments.Length - 3;
            if (!string.Equals(segments[userIndex], account.Username, StringComparison.Ordinal)
                || !string.Equals(segments[userIndex + 1], account.Password, StringComparison.Ordinal))
            {
                return direct;
            }

            var file = segments[segments.Length - 1];
            var extension = Path.GetExtension(file);
            var id = string.IsNullOrEmpty(extension) ? file : file.Substring(0, file.Length - extension.Length);
            if (id.Length == 0 || !id.All(char.IsDigit))
            {
                return direct;
            }

            string segment = null;
            var prefixEnd = userIndex;
            if (userIndex > 0 && KindSegments.Contains(segments[userIndex - 1], StringComparer.OrdinalIgnoreCase))
            {
                segment = segments[userIndex - 1].ToLowerInvariant();
                prefixEnd = userIndex - 1;
            }

            var baseUrl = new StringBuilder(uri.GetLeftPart(UriPartial.Authority));
            for (var i = 0; i < prefixEnd; i++)
            {
                baseUrl.Append('/').Append(Uri.EscapeDataString(segments[i]));
            }

            return new StreamReference
            {
                BaseUrl = baseUrl.ToString(),
                Segment = segment,
                StreamId = id,
                Extension = string.IsNullOrEmpty(extension) ? null : extension.Substring(1),
            };
        }

        private static ContentKind GetKind(StreamReference stream)
        {
            if (stream.IsXtream)
            {
                switch (stream.Segment)
                {
                    case "movie":
                        return ContentKind.Movie;
                    case "series":
                        return ContentKind.Series;
                    default:
                        return ContentKind.Live;
                }
            }

            string extension;
            try
            {
                var path = Uri.TryCreate(stream.DirectUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : stream.DirectUrl;
                extension = Path.GetExtension(path);
            }
            catch (ArgumentException)
            {
                extension = null;
            }

            return extension != null && VideoFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                ? ContentKind.Movie
                : ContentKind.Live;
        }

        private static string ShortHash(string text)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var builder = new StringBuilder(16);
                for (var i = 0; i < 8; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}
