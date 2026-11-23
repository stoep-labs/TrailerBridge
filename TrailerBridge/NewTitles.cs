using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TrailerBridge
{
    /// <summary>
    /// Links new titles without waiting for the daily task: titles added or updated (the YouTube trailer
    /// usually comes with the metadata refresh after the add) are collected and linked 45 seconds later, and a
    /// title whose own trailer arrives loses its row. On a server where the link task never ran, or where link
    /// files of an earlier version are left, it runs once two minutes after startup, in the background.
    /// </summary>
    public class NewTitles : IServerEntryPoint
    {
        private static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);

        private readonly ILibraryManager _library;
        private readonly ConcurrentDictionary<long, byte> _pending = new ConcurrentDictionary<long, byte>();
        private readonly Timer _timer;
        private int _scheduled;
        private int _running;

        public NewTitles(ILogManager logManager, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor,
            IServerApplicationHost host, IJsonSerializer json, IApplicationPaths paths)
        {
            Log.Initialize(logManager);
            Links.Initialize(libraryManager, libraryMonitor, host, json);
            _library = libraryManager;
            _timer = new Timer(_ => Process(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Run()
        {
            _library.ItemAdded += OnItemChanged;
            _library.ItemUpdated += OnItemChanged;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(FirstRunDelay).ConfigureAwait(false);
                    if (!Plugin.Instance.Configuration.Enabled)
                    {
                        // Off with rows or old link files left (turned off while the server was down): remove them.
                        if (Links.HasOldLinks() || Links.Count() > 0) Links.Update(CancellationToken.None);
                        return;
                    }
                    if (Links.NeverRan || Links.HasOldLinks())
                    {
                        Log.Info("First run of this version: creating trailers" + (Links.NeverRan ? "." : " and removing the old link files."));
                        Links.Update(CancellationToken.None);
                    }
                }
                catch (Exception ex) { Log.Warn("First run: " + ex.Message); }
            });
        }

        private void OnItemChanged(object? sender, ItemChangeEventArgs e)
        {
            try
            {
                var item = e.Item;
                if (item == null || !Plugin.Instance.Configuration.Enabled) return;
                long id;
                if (Links.IsTitle(item))
                {
                    if (Links.YouTubeId(item) == null) return;
                    id = item.InternalId;
                }
                else if (item.ExtraType == ExtraType.Trailer && !string.IsNullOrEmpty(item.Path) && item.ParentId != 0
                         && !item.Path.EndsWith(Links.Suffix, StringComparison.OrdinalIgnoreCase))
                    id = item.ParentId; // the title's own trailer: its row goes
                else return;

                _pending[id] = 0;
                if (Interlocked.Exchange(ref _scheduled, 1) == 0) _timer.Change(BatchDelay, Timeout.InfiniteTimeSpan);
            }
            catch { }
        }

        private void Process()
        {
            if (Interlocked.Exchange(ref _running, 1) == 1) { _timer.Change(BatchDelay, Timeout.InfiniteTimeSpan); return; }
            try
            {
                Interlocked.Exchange(ref _scheduled, 0);
                var ids = _pending.Keys.ToList();
                foreach (var id in ids) _pending.TryRemove(id, out _);
                if (ids.Count > 0) Links.LinkNew(ids);
            }
            catch (Exception ex) { Log.Warn("New titles: " + ex.Message); }
            finally { Interlocked.Exchange(ref _running, 0); }
        }

        public void Dispose()
        {
            _library.ItemAdded -= OnItemChanged;
            _library.ItemUpdated -= OnItemChanged;
            _timer.Dispose();
        }
    }
}
