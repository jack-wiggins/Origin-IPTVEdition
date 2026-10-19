using System;
using System.Text;
using Newtonsoft.Json;
using Origin.Core.Xtream;

namespace Origin.Core.Iptv
{
    /// <summary>
    /// Where a channel streams from, stored without credentials. Xtream URLs embed the username
    /// and password, so they are kept as parts and rebuilt with the saved account at play time.
    /// </summary>
    public sealed class StreamReference
    {
        /// <summary>Scheme and authority (plus any path prefix) of the stream, e.g. http://host:8080.</summary>
        public string BaseUrl { get; set; }

        /// <summary>"live", "movie", "series" or null for panels that omit the segment.</summary>
        public string Segment { get; set; }

        public string StreamId { get; set; }

        /// <summary>"ts", "m3u8", "mp4" ... or null.</summary>
        public string Extension { get; set; }

        /// <summary>For playlists that are not Xtream-shaped: the URL exactly as listed.</summary>
        public string DirectUrl { get; set; }

        [JsonIgnore]
        public bool IsXtream => BaseUrl != null && StreamId != null;

        public string Resolve(XtreamAccount account)
        {
            if (!IsXtream)
            {
                return DirectUrl;
            }

            if (account == null)
            {
                throw new InvalidOperationException("The IPTV provider account is not set up, so this channel cannot be played.");
            }

            var url = new StringBuilder(BaseUrl.TrimEnd('/')).Append('/');
            if (!string.IsNullOrEmpty(Segment))
            {
                url.Append(Segment).Append('/');
            }

            url.Append(Uri.EscapeDataString(account.Username)).Append('/')
               .Append(Uri.EscapeDataString(account.Password)).Append('/')
               .Append(StreamId);

            if (!string.IsNullOrEmpty(Extension))
            {
                url.Append('.').Append(Extension);
            }

            return url.ToString();
        }
    }
}
