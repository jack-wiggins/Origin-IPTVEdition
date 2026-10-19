using System;
using System.Collections.Generic;
using System.Linq;
using Origin.Core.Storage;

namespace Origin.Core.Lineup
{
    /// <summary>Holds Origin's lineup in memory and on disk (lineup.json).</summary>
    public sealed class LineupStore
    {
        private readonly object _sync = new object();
        private readonly string _path;
        private OriginLineup _current;

        public LineupStore(string path)
        {
            _path = path;
            _current = JsonFile.TryLoad<OriginLineup>(path) ?? new OriginLineup();
            _current.Channels = _current.Channels ?? new List<OriginChannel>();
        }

        /// <summary>Raised after every change, possibly on a background thread.</summary>
        public event EventHandler Changed;

        public OriginLineup Current
        {
            get
            {
                lock (_sync)
                {
                    return _current;
                }
            }
        }

        /// <summary>Channels shown in the guide, in channel-number order.</summary>
        public IReadOnlyList<OriginChannel> VisibleChannels => Current.Channels.Where(c => c.Enabled).ToList();

        public OriginChannel FindByKey(string key)
        {
            return key == null ? null : Current.Channels.FirstOrDefault(c => c.Key == key);
        }

        /// <summary>The visible channel with this number, or else the next one up (like typing a number on a set-top box).</summary>
        public OriginChannel FindByNumber(string number)
        {
            var wanted = ChannelNumber.SortKey(number);
            if (wanted == double.MaxValue)
            {
                return null;
            }

            var visible = VisibleChannels;
            return visible.FirstOrDefault(c => ChannelNumber.SortKey(c.Number) >= wanted) ?? visible.LastOrDefault();
        }

        public void Replace(OriginLineup lineup)
        {
            lock (_sync)
            {
                JsonFile.Save(_path, lineup, indented: false);
                _current = lineup;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Applies a change to one channel and saves.</summary>
        public bool UpdateChannel(string key, Action<OriginChannel> change)
        {
            lock (_sync)
            {
                var channel = _current.Channels.FirstOrDefault(c => c.Key == key);
                if (channel == null)
                {
                    return false;
                }

                change(channel);
                JsonFile.Save(_path, _current, indented: false);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }
}
