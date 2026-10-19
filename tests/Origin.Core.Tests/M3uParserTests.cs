using System.Linq;
using Origin.Core.Iptv;
using Xunit;

namespace Origin.Core.Tests
{
    public class M3uParserTests
    {
        [Fact]
        public void Parses_m3u_plus_entries()
        {
            const string text = "﻿#EXTM3U url-tvg=\"http://host/xmltv.php\" x-tvg-url=\"http://host/epg.xml\"\r\n" +
                                "#EXTINF:-1 tvg-id=\"ESPN.us\" tvg-name=\"US: ESPN HD\" tvg-logo=\"http://logo/espn.png\" group-title=\"USA | Sports\" tvg-chno=\"206\",US: ESPN HD\r\n" +
                                "http://host:8080/live/user/pass/12345.ts\r\n" +
                                "\r\n" +
                                "#EXTINF:-1 tvg-id=\"\" tvg-name=\"Movie, The\" group-title=\"Movies, Action\",Movie, The (2020)\n" +
                                "http://host:8080/movie/user/pass/777.mkv\n";

            var playlist = M3uParser.Parse(text);

            Assert.True(playlist.HasHeader);
            Assert.Equal("http://host/xmltv.php", playlist.HeaderAttributes["url-tvg"]);
            Assert.Equal(2, playlist.Entries.Count);

            var espn = playlist.Entries[0];
            Assert.Equal(-1, espn.Duration);
            Assert.Equal("US: ESPN HD", espn.Title);
            Assert.Equal("ESPN.us", espn.TvgId);
            Assert.Equal("http://logo/espn.png", espn.TvgLogo);
            Assert.Equal("USA | Sports", espn.GroupTitle);
            Assert.Equal("206", espn.ChannelNumber);
            Assert.Equal("http://host:8080/live/user/pass/12345.ts", espn.Url);

            var movie = playlist.Entries[1];
            Assert.Equal("Movie, The (2020)", movie.Title);
            Assert.Equal("Movies, Action", movie.GroupTitle);
            Assert.Null(movie.TvgId);
        }

        [Fact]
        public void Supports_extgrp_vlcopt_and_single_quotes()
        {
            const string text = "#EXTM3U\n" +
                                "#EXTINF:0 tvg-id='abc',Channel One\n" +
                                "#EXTGRP:News\n" +
                                "#EXTVLCOPT:http-user-agent=Mozilla/5.0\n" +
                                "http://example.com/one.m3u8\n";

            var entry = M3uParser.Parse(text).Entries.Single();

            Assert.Equal("abc", entry.TvgId);
            Assert.Equal("News", entry.GroupTitle);
            Assert.Equal("http-user-agent=Mozilla/5.0", entry.Options.Single());
            Assert.Equal(0, entry.Duration);
        }

        [Fact]
        public void Falls_back_to_tvg_name_and_accepts_bare_urls()
        {
            const string text = "#EXTINF:-1 tvg-name=\"Fallback Name\"\n" +
                                "http://example.com/a.ts\n" +
                                "http://example.com/b.ts\n";

            var playlist = M3uParser.Parse(text);

            Assert.False(playlist.HasHeader);
            Assert.Equal(2, playlist.Entries.Count);
            Assert.Equal("Fallback Name", playlist.Entries[0].Title);
            Assert.Equal("http://example.com/b.ts", playlist.Entries[1].Title);
        }
    }
}
