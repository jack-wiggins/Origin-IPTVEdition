using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Origin.Core.Diagnostics;
using Origin.Core.Iptv;
using Origin.Core.Net;

namespace Origin.Core.Xtream
{
    public sealed class XtreamException : Exception
    {
        public XtreamException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>Talks to an Xtream Codes panel: account check and M3U download.</summary>
    public sealed class XtreamClient : IDisposable
    {
        private static readonly TimeSpan AccountTimeout = TimeSpan.FromSeconds(30);

        private readonly HttpClient _http;
        private readonly bool _ownsClient;

        public XtreamClient(string userAgent = null)
            : this(OriginHttp.CreateClient(userAgent, TimeSpan.FromMinutes(10)), ownsClient: true)
        {
        }

        public XtreamClient(HttpClient http, bool ownsClient = false)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _ownsClient = ownsClient;
        }

        /// <summary>Calls player_api.php to confirm the account works.</summary>
        public async Task<XtreamAccountInfo> GetAccountInfoAsync(XtreamAccount account, CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(AccountTimeout);
                var text = await GetStringAsync(XtreamUrls.GetPlayerApiUrl(account), "check the account", timeout.Token, cancellationToken)
                    .ConfigureAwait(false);

                JObject root;
                try
                {
                    root = JObject.Parse(text);
                }
                catch (JsonException ex)
                {
                    throw new XtreamException(
                        "The server answered, but not like an Xtream Codes panel. Check the server URL (including the port).", ex);
                }

                var info = XtreamAccountInfo.FromJson(root);
                if (!info.IsAuthenticated)
                {
                    throw new XtreamException("The provider rejected the username or password.");
                }

                return info;
            }
        }

        /// <summary>Downloads and parses get.php?type=m3u_plus without holding the whole file in memory.</summary>
        public async Task<M3uPlaylist> DownloadPlaylistAsync(XtreamAccount account, IProgress<long> bytesReceived, CancellationToken cancellationToken)
        {
            var url = XtreamUrls.GetPlaylistUrl(account);
            Log.Info("Downloading playlist from " + account.Host);

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
            {
                throw Wrap(ex, account.Host);
            }

            using (response)
            {
                EnsureSuccess(response, "download the playlist");

                M3uPlaylist playlist;
                try
                {
                    using (var network = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var counting = new ProgressStream(network, bytesReceived, cancellationToken))
                    using (var reader = new StreamReader(counting, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    {
                        playlist = M3uParser.Parse(reader);
                    }
                }
                catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
                {
                    throw Wrap(ex, account.Host);
                }

                if (playlist.Entries.Count == 0)
                {
                    throw new XtreamException(playlist.HasHeader
                        ? "The provider returned an empty playlist. Your subscription may not include any channels."
                        : "The provider did not return an M3U playlist. Check the server URL, username and password.");
                }

                Log.Info("Playlist downloaded: " + playlist.Entries.Count + " entries");
                return playlist;
            }
        }

        public void Dispose()
        {
            if (_ownsClient)
            {
                _http.Dispose();
            }
        }

        private async Task<string> GetStringAsync(string url, string action, CancellationToken token, CancellationToken userToken)
        {
            try
            {
                using (var response = await _http.GetAsync(url, token).ConfigureAwait(false))
                {
                    EnsureSuccess(response, action);
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsTransportFailure(ex, userToken))
            {
                throw Wrap(ex, new Uri(url).Host);
            }
        }

        private static bool IsTransportFailure(Exception ex, CancellationToken userToken)
        {
            return ex is HttpRequestException
                   || ex is IOException
                   || ex is WebException
                   || (ex is OperationCanceledException && !userToken.IsCancellationRequested);
        }

        private static XtreamException Wrap(Exception ex, string host)
        {
            if (ex is OperationCanceledException)
            {
                return new XtreamException(host + " did not respond in time.", ex);
            }

            return new XtreamException("Could not reach " + host + ": " + OriginHttp.Describe(ex) + ".", ex);
        }

        private static void EnsureSuccess(HttpResponseMessage response, string action)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var code = (int)response.StatusCode;
            string reason;
            switch (code)
            {
                case 401:
                case 403:
                    reason = "The provider rejected the username or password, or this connection is blocked";
                    break;
                case 404:
                    reason = "This server does not look like an Xtream Codes panel. Check the server URL and port";
                    break;
                case 429:
                    reason = "The provider is limiting requests. Wait a few minutes and try again";
                    break;
                default:
                    reason = code >= 500 && code < 600
                        ? "The provider's server reported an error"
                        : "The provider refused the request";
                    break;
            }

            throw new XtreamException(reason + " (HTTP " + code + " while trying to " + action + ").");
        }

        /// <summary>Reports bytes read and lets a cancel request interrupt a long download.</summary>
        private sealed class ProgressStream : Stream
        {
            private readonly Stream _inner;
            private readonly IProgress<long> _progress;
            private readonly CancellationToken _token;
            private long _total;
            private long _lastReported;

            public ProgressStream(Stream inner, IProgress<long> progress, CancellationToken token)
            {
                _inner = inner;
                _progress = progress;
                _token = token;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => _total;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                _token.ThrowIfCancellationRequested();
                var read = _inner.Read(buffer, offset, count);
                _total += read;

                if (_progress != null && (_total - _lastReported >= 256 * 1024 || read == 0))
                {
                    _lastReported = _total;
                    _progress.Report(_total);
                }

                return read;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
