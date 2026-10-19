using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace Origin.Core.Net
{
    /// <summary>Shared HTTP setup for every network call Origin makes.</summary>
    public static class OriginHttp
    {
        public const string DefaultUserAgent = "Origin/0.1 (Windows; HTPC)";

        private static int _initialized;

        /// <summary>
        /// Windows 7 and 8 do not offer TLS 1.2 to .NET applications unless asked, and
        /// Schedules Direct (and most IPTV panels behind HTTPS) refuse anything older.
        /// </summary>
        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 1)
            {
                return;
            }

            var current = ServicePointManager.SecurityProtocol;
            var modernOs = Environment.OSVersion.Version >= new Version(10, 0);
            if (!(modernOs && current == SecurityProtocolType.SystemDefault))
            {
                ServicePointManager.SecurityProtocol = current
                    | SecurityProtocolType.Tls12
                    | SecurityProtocolType.Tls11
                    | SecurityProtocolType.Tls;
            }

            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 16);
            ServicePointManager.Expect100Continue = false;
        }

        public static HttpClient CreateClient(string userAgent, TimeSpan timeout)
        {
            Initialize();

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = false,
            };

            var client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent.Trim());
            return client;
        }

        /// <summary>Turns a transport failure into a sentence that is useful on a TV screen.</summary>
        public static string Describe(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is WebException web)
                {
                    switch (web.Status)
                    {
                        case WebExceptionStatus.NameResolutionFailure:
                            return "the server name could not be found";
                        case WebExceptionStatus.ConnectFailure:
                            return "the server refused the connection or is offline";
                        case WebExceptionStatus.Timeout:
                            return "the connection timed out";
                        case WebExceptionStatus.TrustFailure:
                        case WebExceptionStatus.SecureChannelFailure:
                            return "a secure (TLS) connection could not be established";
                    }
                }

                if (current is IOException && current.InnerException == null)
                {
                    return "the connection was interrupted";
                }
            }

            return exception.GetBaseException().Message;
        }
    }
}
