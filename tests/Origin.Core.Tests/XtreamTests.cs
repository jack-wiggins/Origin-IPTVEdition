using System;
using System.Linq;
using Origin.Core.Iptv;
using Origin.Core.Xtream;
using Xunit;

namespace Origin.Core.Tests
{
    public class XtreamTests
    {
        [Theory]
        [InlineData("line.example.com:8080", "http://line.example.com:8080")]
        [InlineData("http://line.example.com:8080/", "http://line.example.com:8080")]
        [InlineData("https://line.example.com", "https://line.example.com")]
        [InlineData("http://line.example.com:8080/get.php?username=a&password=b&type=m3u_plus", "http://line.example.com:8080")]
        [InlineData("http://line.example.com/panel/player_api.php", "http://line.example.com/panel")]
        public void Normalizes_server_urls(string input, string expected)
        {
            Assert.Equal(expected, XtreamUrls.NormalizeServerUrl(input));
        }

        [Theory]
        [InlineData("")]
        [InlineData("ftp://example.com")]
        [InlineData("http://")]
        public void Rejects_invalid_server_urls(string input)
        {
            Assert.Throws<FormatException>(() => XtreamUrls.NormalizeServerUrl(input));
        }

        [Fact]
        public void Extracts_credentials_from_a_pasted_playlist_link()
        {
            var ok = XtreamUrls.TryExtractCredentials(
                "http://line.example.com:8080/get.php?username=jack&password=p%40ss&type=m3u_plus&output=ts",
                out var server, out var user, out var pass);

            Assert.True(ok);
            Assert.Equal("http://line.example.com:8080", server);
            Assert.Equal("jack", user);
            Assert.Equal("p@ss", pass);
            Assert.False(XtreamUrls.TryExtractCredentials("http://line.example.com:8080", out _, out _, out _));
        }

        [Fact]
        public void Builds_playlist_url()
        {
            var account = new XtreamAccount("line.example.com:8080", "jack", "p@ss", "m3u8");
            Assert.Equal(
                "http://line.example.com:8080/get.php?username=jack&password=p%40ss&type=m3u_plus&output=m3u8",
                XtreamUrls.GetPlaylistUrl(account));
        }

        [Fact]
        public void Import_removes_credentials_and_resolves_them_again()
        {
            var account = new XtreamAccount("http://line.example.com:8080", "jack", "secret");
            var playlist = M3uParser.Parse(
                "#EXTM3U\n" +
                "#EXTINF:-1 group-title=\"US\",ESPN\nhttp://cdn.example.com:80/live/jack/secret/12345.ts\n" +
                "#EXTINF:-1 group-title=\"US\",Legacy\nhttp://line.example.com:8080/jack/secret/555\n" +
                "#EXTINF:-1 group-title=\"Movies\",Film\nhttp://line.example.com:8080/movie/jack/secret/9.mkv\n" +
                "#EXTINF:-1 group-title=\"Series\",Show S01E01\nhttp://line.example.com:8080/series/jack/secret/10.mp4\n" +
                "#EXTINF:-1 group-title=\"Other\",Somebody else\nhttp://other.example.com/bob/hunter2/1.ts\n" +
                "#EXTINF:-1 group-title=\"US 2\",ESPN again\nhttp://cdn.example.com:80/live/jack/secret/12345.ts\n");

            var channels = PlaylistImporter.Import(playlist, account);

            Assert.Equal(6, channels.Count);
            Assert.DoesNotContain(channels, c => (c.Stream.BaseUrl ?? string.Empty).Contains("secret"));

            var espn = channels[0];
            Assert.Equal("xc:live:12345", espn.Key);
            Assert.Equal(ContentKind.Live, espn.Kind);
            Assert.Equal("http://cdn.example.com", espn.Stream.BaseUrl);
            Assert.Equal("ts", espn.Stream.Extension);
            Assert.Equal("http://cdn.example.com/live/jack/secret/12345.ts", espn.Stream.Resolve(account));

            var legacy = channels[1];
            Assert.Null(legacy.Stream.Segment);
            Assert.Equal("http://line.example.com:8080/jack/secret/555", legacy.Stream.Resolve(account));

            Assert.Equal(ContentKind.Movie, channels[2].Kind);
            Assert.Equal(ContentKind.Series, channels[3].Kind);

            var foreign = channels[4];
            Assert.False(foreign.Stream.IsXtream);
            Assert.Equal("http://other.example.com/bob/hunter2/1.ts", foreign.Stream.Resolve(account));
            Assert.StartsWith("url:", foreign.Key);

            Assert.Equal("xc:live:12345~2", channels[5].Key);
            Assert.Equal(channels.Count, channels.Select(c => c.Key).Distinct().Count());
        }
    }
}
