using System;

namespace Origin.Core.Xtream
{
    /// <summary>The three things an Xtream Codes provider gives you: server URL, username and password.</summary>
    public sealed class XtreamAccount
    {
        public XtreamAccount(string serverUrl, string username, string password, string outputFormat = "ts")
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new ArgumentException("A username is required.", nameof(username));
            }

            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("A password is required.", nameof(password));
            }

            ServerUrl = XtreamUrls.NormalizeServerUrl(serverUrl);
            Username = username.Trim();
            Password = password;
            OutputFormat = outputFormat == "m3u8" ? "m3u8" : "ts";
        }

        /// <summary>Scheme, host, port and optional path prefix, without a trailing slash.</summary>
        public string ServerUrl { get; }

        public string Username { get; }

        public string Password { get; }

        /// <summary>"ts" or "m3u8".</summary>
        public string OutputFormat { get; }

        public string Host => new Uri(ServerUrl).Host;
    }
}
