using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Origin.Core.Iptv
{
    /// <summary>
    /// Streaming parser for M3U and the "M3U Plus" dialect Xtream Codes panels produce
    /// (tvg-id, tvg-name, tvg-logo, group-title, tvg-chno ...).
    /// </summary>
    public static class M3uParser
    {
        private static readonly Regex AttributePattern = new Regex(
            @"(?<key>[A-Za-z0-9_\-:.]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s,""']+))",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static M3uPlaylist Parse(string text)
        {
            using (var reader = new StringReader(text ?? string.Empty))
            {
                return Parse(reader);
            }
        }

        public static M3uPlaylist Parse(TextReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var playlist = new M3uPlaylist();
            M3uEntry pending = null;
            string raw;
            var first = true;

            while ((raw = reader.ReadLine()) != null)
            {
                var line = raw.Trim();
                if (first)
                {
                    line = line.TrimStart('﻿');
                    first = false;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    playlist.HasHeader = true;
                    ParseAttributes(line.Substring(7), playlist.HeaderAttributes);
                    continue;
                }

                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    pending = new M3uEntry();
                    ParseExtInf(line.Substring(8), pending);
                    continue;
                }

                if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
                {
                    if (pending != null)
                    {
                        pending.GroupDirective = line.Substring(8).Trim();
                    }

                    continue;
                }

                if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
                {
                    pending?.Options.Add(line.Substring(11).Trim());
                    continue;
                }

                if (line[0] == '#')
                {
                    // Other directives (#KODIPROP, #EXT-X-..., comments) are not needed.
                    continue;
                }

                var entry = pending ?? new M3uEntry { Title = line };
                entry.Url = line;
                playlist.Entries.Add(entry);
                pending = null;
            }

            return playlist;
        }

        /// <summary>Parses the part of an #EXTINF line after the colon.</summary>
        internal static void ParseExtInf(string text, M3uEntry entry)
        {
            // The title starts after the first comma that is not inside a quoted attribute value.
            var comma = -1;
            char? quote = null;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote.HasValue)
                {
                    if (c == quote.Value)
                    {
                        quote = null;
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == ',')
                {
                    comma = i;
                    break;
                }
            }

            var head = comma >= 0 ? text.Substring(0, comma) : text;
            entry.Title = comma >= 0 ? text.Substring(comma + 1).Trim() : null;

            var durationEnd = 0;
            while (durationEnd < head.Length && !char.IsWhiteSpace(head[durationEnd]))
            {
                durationEnd++;
            }

            if (double.TryParse(head.Substring(0, durationEnd), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
            {
                entry.Duration = duration;
            }

            ParseAttributes(head.Substring(durationEnd), entry.Attributes);

            if (string.IsNullOrWhiteSpace(entry.Title))
            {
                entry.Title = entry.TvgName;
            }
        }

        internal static void ParseAttributes(string text, IDictionary<string, string> target)
        {
            foreach (Match match in AttributePattern.Matches(text))
            {
                target[match.Groups["key"].Value] = match.Groups["value"].Value;
            }
        }
    }
}
