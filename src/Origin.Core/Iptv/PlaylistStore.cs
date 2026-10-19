using Origin.Core.Storage;

namespace Origin.Core.Iptv
{
    /// <summary>Keeps the last downloaded playlist on disk (playlist.json).</summary>
    public sealed class PlaylistStore
    {
        private readonly object _sync = new object();
        private readonly string _path;
        private PlaylistSnapshot _current;
        private bool _loaded;

        public PlaylistStore(string path)
        {
            _path = path;
        }

        /// <summary>The saved playlist, or null if none has been downloaded yet.</summary>
        public PlaylistSnapshot Load()
        {
            lock (_sync)
            {
                if (!_loaded)
                {
                    _current = JsonFile.TryLoad<PlaylistSnapshot>(_path);
                    _loaded = true;
                }

                return _current;
            }
        }

        public void Save(PlaylistSnapshot snapshot)
        {
            lock (_sync)
            {
                // Large playlists run to tens of thousands of entries; skip indentation.
                JsonFile.Save(_path, snapshot, indented: false);
                _current = snapshot;
                _loaded = true;
            }
        }
    }
}
