using System;
using System.Collections.Generic;
using System.Linq;

namespace Origin.Core.Guide
{
    /// <summary>One block in a guide row: a program, or a stretch with no listings.</summary>
    public sealed class GuideCell
    {
        public GuideCell(GuideListing listing, DateTime startUtc, DateTime endUtc, DateTime windowStartUtc, DateTime windowEndUtc)
        {
            Listing = listing;
            StartUtc = startUtc;
            EndUtc = endUtc;
            ContinuesBefore = listing != null && listing.StartUtc < windowStartUtc;
            ContinuesAfter = listing != null && listing.EndUtc > windowEndUtc;

            var window = (windowEndUtc - windowStartUtc).TotalSeconds;
            StartFraction = (startUtc - windowStartUtc).TotalSeconds / window;
            EndFraction = (endUtc - windowStartUtc).TotalSeconds / window;
        }

        /// <summary>Null for a gap in the listings.</summary>
        public GuideListing Listing { get; }

        public bool IsGap => Listing == null;

        /// <summary>Where the block starts on screen: the program's start, or the window's start if it began earlier.</summary>
        public DateTime StartUtc { get; }

        public DateTime EndUtc { get; }

        /// <summary>The program's real start (before the window for a program already under way).</summary>
        public DateTime ProgramStartUtc => Listing?.StartUtc ?? StartUtc;

        /// <summary>The program's real end (after the window for a program that runs on).</summary>
        public DateTime ProgramEndUtc => Listing?.EndUtc ?? EndUtc;

        public bool ContinuesBefore { get; }

        public bool ContinuesAfter { get; }

        /// <summary>0-1 across the window.</summary>
        public double StartFraction { get; }

        public double EndFraction { get; }

        public bool Contains(DateTime utc) => StartUtc <= utc && utc < EndUtc;
    }

    /// <summary>Lays a station's listings out across the guide's time window.</summary>
    public static class GuideLayout
    {
        /// <summary>Gaps shorter than this are left as empty space rather than drawn as a "no listings" block.</summary>
        private static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Blocks that cover [<paramref name="windowStartUtc"/>, <paramref name="windowEndUtc"/>) from left to
        /// right: each listing clipped to the window, and gaps where nothing is listed. Overlapping
        /// listings are trimmed so blocks never overlap.
        /// </summary>
        public static IReadOnlyList<GuideCell> Build(IEnumerable<GuideListing> listings, DateTime windowStartUtc, DateTime windowEndUtc)
        {
            var cells = new List<GuideCell>();
            if (windowEndUtc <= windowStartUtc)
            {
                return cells;
            }

            var cursor = windowStartUtc;
            foreach (var listing in (listings ?? Enumerable.Empty<GuideListing>()).OrderBy(l => l.StartUtc))
            {
                var start = listing.StartUtc > cursor ? listing.StartUtc : cursor;
                var end = listing.EndUtc < windowEndUtc ? listing.EndUtc : windowEndUtc;
                if (end <= start)
                {
                    continue;
                }

                if (start - cursor >= MinimumGap)
                {
                    cells.Add(new GuideCell(null, cursor, start, windowStartUtc, windowEndUtc));
                }

                cells.Add(new GuideCell(listing, start, end, windowStartUtc, windowEndUtc));
                cursor = end;
            }

            if (windowEndUtc - cursor >= MinimumGap || cells.Count == 0)
            {
                cells.Add(new GuideCell(null, cursor, windowEndUtc, windowStartUtc, windowEndUtc));
            }

            return cells;
        }

        /// <summary>The cell under <paramref name="utc"/>, or the nearest one.</summary>
        public static int IndexAt(IReadOnlyList<GuideCell> cells, DateTime utc)
        {
            if (cells.Count == 0)
            {
                return -1;
            }

            for (var i = 0; i < cells.Count; i++)
            {
                if (utc < cells[i].EndUtc)
                {
                    return i;
                }
            }

            return cells.Count - 1;
        }
    }
}
