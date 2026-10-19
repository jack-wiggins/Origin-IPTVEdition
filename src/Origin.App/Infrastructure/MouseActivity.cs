using System;

namespace Origin.Infrastructure
{
    /// <summary>Remembers when the mouse last physically moved, to tell hovering apart from content scrolling under the cursor.</summary>
    public static class MouseActivity
    {
        private static DateTime _lastMoveUtc = DateTime.MinValue;

        public static bool MovedRecently => DateTime.UtcNow - _lastMoveUtc < TimeSpan.FromMilliseconds(150);

        public static void NoteMove()
        {
            _lastMoveUtc = DateTime.UtcNow;
        }

        public static void Reset()
        {
            _lastMoveUtc = DateTime.MinValue;
        }
    }
}
