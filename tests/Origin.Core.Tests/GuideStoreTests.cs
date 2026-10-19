using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Origin.Core.Guide;
using Xunit;
using static Origin.Core.Tests.LineupServiceIntegrationTests;

namespace Origin.Core.Tests
{
    public class GuideStoreTests
    {
        private static readonly DateTime Midnight = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Finds_whats_on_between_two_times_across_midnight()
        {
            using (var temp = new TempDirectory())
            {
                var store = CreateSample(temp.Path);

                var listings = store.GetListings("100", Midnight.AddMinutes(-45), Midnight.AddMinutes(45));

                // The film that started at 22:30 is still on at 23:15; the news starts at 00:30.
                Assert.Equal(new[] { "MV1", "EP2", "EP3" }, listings.Select(l => l.Airing.ProgramId));
                Assert.Equal("The Long Film", listings[0].Title);
                Assert.Equal(Midnight.AddMinutes(-90), listings[0].StartUtc);
                Assert.Equal(Midnight.AddMinutes(15), listings[0].EndUtc);
            }
        }

        [Fact]
        public void Leaves_out_programs_that_end_as_the_range_starts()
        {
            using (var temp = new TempDirectory())
            {
                var store = CreateSample(temp.Path);

                var listings = store.GetListings("100", Midnight.AddMinutes(15), Midnight.AddMinutes(30));

                Assert.Equal(new[] { "EP2" }, listings.Select(l => l.Airing.ProgramId));
                Assert.Empty(store.GetListings("100", Midnight.AddDays(3), Midnight.AddDays(4)));
                Assert.Empty(store.GetListings("other", Midnight, Midnight.AddHours(1)));
            }
        }

        [Fact]
        public void Answers_what_is_on_now_and_next()
        {
            using (var temp = new TempDirectory())
            {
                var store = CreateSample(temp.Path);

                var now = store.GetListingAt("100", Midnight.AddMinutes(20));
                var next = store.GetNextListing("100", now.EndUtc);

                Assert.Equal("Late Show", now.Title);
                Assert.Equal("S4 E12 · The One With the Guide", now.EpisodeLine);
                Assert.Equal(0.25, now.ProgressAt(Midnight.AddMinutes(18.75)), 3);
                Assert.Equal("Overnight News", next.Title);
                Assert.Equal("LIVE", next.Badge);
                Assert.Null(store.GetListingAt("100", Midnight.AddHours(5)));
                Assert.Null(store.GetNextListing("100", Midnight.AddHours(5)));
            }
        }

        [Fact]
        public void Reads_the_same_listings_back_after_a_restart()
        {
            using (var temp = new TempDirectory())
            {
                CreateSample(temp.Path);

                var reopened = new GuideStore(temp.Path);
                var film = reopened.GetListingAt("100", Midnight.AddMinutes(-30));

                Assert.Equal("The Long Film", film.Title);
                Assert.Equal("Movie · 1999", film.EpisodeLine);
                Assert.Equal("A film that runs past midnight.", film.Description);
                Assert.Equal("md5-day-2", reopened.GetDayMd5("100", "2026-10-19"));
                Assert.True(reopened.HasListings("100"));
                Assert.True(Directory.GetFiles(Path.Combine(temp.Path, "programs")).Length > 0);
            }
        }

        [Fact]
        public void Lists_a_program_given_in_both_days_once()
        {
            using (var temp = new TempDirectory())
            {
                var store = CreateSample(temp.Path);
                store.SaveDays("100", new Dictionary<string, GuideDay>
                {
                    ["2026-10-19"] = Day("md5-day-2b", Airing("MV1", Midnight.AddMinutes(-90), 105), Airing("EP2", Midnight.AddMinutes(15), 15)),
                });

                Assert.Equal(new[] { "MV1", "EP2" }, store.GetListings("100", Midnight.AddHours(-1), Midnight.AddMinutes(29)).Select(l => l.Airing.ProgramId));
            }
        }

        [Fact]
        public void Prunes_old_days_and_programs_nothing_airs()
        {
            using (var temp = new TempDirectory())
            {
                var store = CreateSample(temp.Path);
                store.SaveDays("200", new Dictionary<string, GuideDay> { ["2026-10-19"] = Day("x", Airing("EP9", Midnight, 60)) });
                store.AddPrograms(new[] { new GuideProgram { ProgramId = "EP9", Title = "Elsewhere" } });

                store.Prune(new[] { "100" }, "2026-10-19");

                var reopened = new GuideStore(temp.Path);
                Assert.Null(reopened.GetListingAt("100", Midnight.AddMinutes(-60)));
                Assert.NotNull(reopened.GetListingAt("100", Midnight.AddMinutes(20)));
                Assert.Null(reopened.GetProgram("MV1"));
                Assert.Null(reopened.GetProgram("EP9"));
                Assert.Equal("Late Show", reopened.GetProgram("EP2").Title);
                Assert.False(reopened.HasListings("200"));
            }
        }

        /// <summary>Station 100: a film from 22:30 to 00:15, a show until 00:30, then news until 01:30.</summary>
        private static GuideStore CreateSample(string directory)
        {
            var store = new GuideStore(directory);
            store.SaveDays("100", new Dictionary<string, GuideDay>
            {
                ["2026-10-18"] = Day("md5-day-1", Airing("EP0", Midnight.AddHours(-3), 30), Airing("MV1", Midnight.AddMinutes(-90), 105)),
                ["2026-10-19"] = Day("md5-day-2", Airing("EP2", Midnight.AddMinutes(15), 15), Airing("EP3", Midnight.AddMinutes(30), 60, live: true)),
            });
            store.AddPrograms(new[]
            {
                new GuideProgram { ProgramId = "EP0", Title = "Evening Show" },
                new GuideProgram { ProgramId = "MV1", Title = "The Long Film", EntityType = "Movie", MovieYear = 1999, Description = "A film that runs past midnight." },
                new GuideProgram { ProgramId = "EP2", Title = "Late Show", Season = 4, Episode = 12, EpisodeTitle = "The One With the Guide" },
                new GuideProgram { ProgramId = "EP3", Title = "Overnight News", Genres = new List<string> { "News" } },
            });
            store.Flush();
            return store;
        }

        private static GuideDay Day(string md5, params GuideAiring[] airings)
        {
            return new GuideDay { Md5 = md5, Airings = airings.ToList() };
        }

        private static GuideAiring Airing(string programId, DateTime start, int minutes, bool live = false)
        {
            return new GuideAiring { ProgramId = programId, StartUtc = start, Duration = minutes * 60, ProgramMd5 = "md5-" + programId, IsLive = live };
        }
    }

    public class GuideLayoutTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime End = Start.AddMinutes(90);

        [Fact]
        public void Clips_programs_to_the_window_and_fills_gaps()
        {
            var cells = GuideLayout.Build(new[]
            {
                Listing("A", Start.AddMinutes(-30), 60),
                Listing("B", Start.AddMinutes(45), 60),
            }, Start, End);

            Assert.Equal(3, cells.Count);

            Assert.Equal("A", cells[0].Listing.Airing.ProgramId);
            Assert.Equal(Start, cells[0].StartUtc);
            Assert.Equal(Start.AddMinutes(-30), cells[0].ProgramStartUtc);
            Assert.True(cells[0].ContinuesBefore);
            Assert.False(cells[0].ContinuesAfter);
            Assert.Equal(0, cells[0].StartFraction, 6);
            Assert.Equal(1 / 3.0, cells[0].EndFraction, 6);

            Assert.True(cells[1].IsGap);
            Assert.Equal(Start.AddMinutes(30), cells[1].StartUtc);
            Assert.Equal(Start.AddMinutes(45), cells[1].EndUtc);

            Assert.Equal("B", cells[2].Listing.Airing.ProgramId);
            Assert.True(cells[2].ContinuesAfter);
            Assert.Equal(End, cells[2].EndUtc);
            Assert.Equal(1.0, cells[2].EndFraction, 6);
        }

        [Fact]
        public void An_empty_window_is_one_gap()
        {
            var cells = GuideLayout.Build(new GuideListing[0], Start, End);

            Assert.True(cells.Single().IsGap);
            Assert.Equal(Start, cells[0].StartUtc);
            Assert.Equal(End, cells[0].EndUtc);
        }

        [Fact]
        public void Trims_overlapping_programs_and_ignores_slivers()
        {
            var cells = GuideLayout.Build(new[]
            {
                Listing("A", Start, 40),
                Listing("B", Start.AddMinutes(30), 30),
                Listing("C", Start.AddMinutes(60.5), 29.5),
            }, Start, End);

            Assert.Equal(new[] { "A", "B", "C" }, cells.Select(c => c.Listing.Airing.ProgramId));
            Assert.Equal(Start.AddMinutes(40), cells[1].StartUtc);
            Assert.Equal(Start.AddMinutes(30), cells[1].ProgramStartUtc);
            Assert.Equal(2, GuideLayout.IndexAt(cells, Start.AddMinutes(60.25)));
            Assert.Equal(1, GuideLayout.IndexAt(cells, Start.AddMinutes(45)));
            Assert.Equal(2, GuideLayout.IndexAt(cells, End.AddHours(1)));
        }

        private static GuideListing Listing(string programId, DateTime start, double minutes)
        {
            var airing = new GuideAiring { ProgramId = programId, StartUtc = start, Duration = (int)(minutes * 60) };
            return new GuideListing("100", airing, new GuideProgram { ProgramId = programId, Title = programId });
        }
    }
}
