using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace TrailerBridge
{
    /// <summary>
    /// The trailer rows: one local trailer without a file for every movie and series that has a YouTube trailer
    /// and no trailer of its own. Emby lists it like a trailer in the title's folder; when it plays, Emby asks
    /// <see cref="TrailerSource"/> for its source, which is this server's play endpoint.
    /// </summary>
    internal static class Links
    {
        /// <summary>The provider id on every row: TrailerBridge=&lt;YouTube video id&gt;.</summary>
        internal const string ProviderKey = "TrailerBridge";

        // The link files of earlier versions ("trailers/<folder> - TrailerBridge.strm"): only removed now.
        internal const string Suffix = " - TrailerBridge.strm";
        internal const string Marker = "/TrailerBridge/Play/";

        private static readonly ExtraType[] TrailerTypes = { ExtraType.Trailer };
        // Between two rows written or two old link files removed, so a big library is done without a burst.
        private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(20);

        private static readonly object RunLock = new object();

        internal static ILibraryManager LibraryManager = null!;
        internal static ILibraryMonitor LibraryMonitor = null!;
        internal static IServerApplicationHost Host = null!;
        internal static IJsonSerializer Json = null!;

        internal static void Initialize(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IServerApplicationHost host, IJsonSerializer json)
        {
            LibraryManager = libraryManager;
            LibraryMonitor = libraryMonitor;
            Host = host;
            Json = json;
        }

        public class Outcome
        {
            public int Linked { get; set; }
            public int Created { get; set; }
            public int Updated { get; set; }
            public int Removed { get; set; }
            public int OldLinksRemoved { get; set; }
            public int Failed { get; set; }
            public string Message { get; set; } = "";
            public DateTime When { get; set; } = DateTime.UtcNow;
        }

        /// <summary>The last run, and the link files earlier versions wrote (removed on the first run of this one).</summary>
        public class State
        {
            public List<string> Files { get; set; } = new List<string>();
            public Outcome? Last { get; set; }
        }

        private static string StatePath => Path.Combine(Plugin.Instance.DataPath, "links.json");

        internal static State Load()
        {
            try
            {
                if (File.Exists(StatePath)) return Json.DeserializeFromFile<State>(StatePath) ?? new State();
            }
            catch (Exception ex) { Log.Warn("links.json: " + ex.Message); }
            return new State();
        }

        private static void Save(State s)
        {
            try { Json.SerializeToFile(s, StatePath); }
            catch (Exception ex) { Log.Warn("links.json: " + ex.Message); }
        }

        /// <summary>The play endpoint on this server, for Emby's own ffmpeg only (clients get Emby's stream).</summary>
        internal static string PlayUrl(BaseItem row) =>
            "http://127.0.0.1:" + Host.HttpPort + "/emby" + Marker + row.InternalId + "?k=" + Plugin.Instance.Key;

        /// <summary>The YouTube video id of the item's first YouTube remote trailer.</summary>
        internal static string? YouTubeId(BaseItem item) =>
            (item.RemoteTrailers ?? Array.Empty<string>()).Select(Resolver.VideoId).FirstOrDefault(v => v != null);

        /// <summary>The video id of one of our rows, or null for anything else.</summary>
        internal static string? RowVideoId(BaseItem? item)
        {
            if (item is not Trailer || item.ExtraType != ExtraType.Trailer || !string.IsNullOrEmpty(item.Path)) return null;
            var id = item.GetProviderId(ProviderKey);
            return Resolver.IsVideoId(id) ? id : null;
        }

        /// <summary>An old link file (by name and content).</summary>
        internal static bool IsBridgeFile(string path) =>
            path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) && ReadsAsOurs(path);

        private static bool ReadsAsOurs(string path)
        {
            try { return new FileInfo(path).Length < 4096 && File.ReadAllText(path).Contains(Marker); }
            catch { return false; }
        }

        /// <summary>Movies and series, without the plugins' own copies (HSC and the like).</summary>
        private static List<BaseItem> Titles(IEnumerable<long>? ids = null)
        {
            var items = ids == null
                ? LibraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Movie), nameof(Series) },
                    Recursive = true,
                    IsVirtualItem = false
                })
                : ids.Select(id => LibraryManager.GetItemById(id)).Where(i => i != null).ToArray();
            return items.Where(IsTitle).ToList();
        }

        internal static bool IsTitle(BaseItem? item)
        {
            if (item is not Movie && item is not Series) return false;
            if (item.ExtraType != null || item.IsVirtualItem) return false;
            var path = item.Path ?? "";
            return path.Length > 0 && !Plugin.Instance.SkipRoots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>All our rows.</summary>
        internal static List<BaseItem> Rows() =>
            LibraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Trailer) },
                ExtraTypes = TrailerTypes,
                HasAnyProviderId = new[] { ProviderKey },
                Recursive = true
            }).Where(i => RowVideoId(i) != null).ToList();

        internal static int Count()
        {
            try { return Rows().Count; }
            catch (Exception ex) { Log.Warn("Counting rows: " + ex.Message); return 0; }
        }

        /// <summary>Trailers of the titles that are not ours: anything with a path that isn't an old link file.</summary>
        private static HashSet<long> TitlesWithOwnTrailer()
        {
            var set = new HashSet<long>();
            foreach (var t in LibraryManager.GetItemList(new InternalItemsQuery
                     {
                         IncludeItemTypes = new[] { nameof(Trailer), "Video", nameof(Movie) },
                         ExtraTypes = TrailerTypes,
                         Recursive = true
                     }))
            {
                var p = t.Path ?? "";
                if (p.Length == 0 || p.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (t.ParentId != 0) set.Add(t.ParentId);
            }
            return set;
        }

        /// <summary>Makes the rows match the library: the link task, the settings page and the first run.</summary>
        internal static Outcome Update(CancellationToken ct)
        {
            lock (RunLock)
            {
                if (!Plugin.Instance.Configuration.Enabled) return RemoveAllLocked("TrailerBridge is off: ");
                var watch = Stopwatch.StartNew();
                var outcome = new Outcome();
                var rows = Rows();
                var titles = Titles();
                Sync(titles, rows, outcome, ct, removeOrphans: true);
                var state = Load();
                RemoveOldLinks(state, outcome, ct);
                outcome.Linked = Count();
                outcome.Message = outcome.Linked + " titles linked (" + outcome.Created + " new, " + outcome.Updated + " changed, " +
                                  outcome.Removed + " removed" +
                                  (outcome.OldLinksRemoved > 0 ? ", " + outcome.OldLinksRemoved + " old link files removed" : "") +
                                  (outcome.Failed > 0 ? ", " + outcome.Failed + " failed" : "") + ").";
                state.Files = new List<string>();
                state.Last = outcome;
                Save(state);
                Log.Info(outcome.Message + " " + (int)watch.Elapsed.TotalSeconds + " s.");
                return outcome;
            }
        }

        /// <summary>
        /// Brings the rows of these titles in line. Versions of a title (separate items, one presentation key)
        /// share their extras, so the group gets one row, on its first version.
        /// </summary>
        private static void Sync(List<BaseItem> titles, List<BaseItem> rows, Outcome outcome, CancellationToken ct, bool removeOrphans)
        {
            var own = TitlesWithOwnTrailer();
            var rowsByParent = rows.GroupBy(r => r.ParentId).ToDictionary(g => g.Key, g => g.ToList());
            var seen = new HashSet<long>();

            foreach (var group in titles.GroupBy(t => t.PresentationUniqueKey ?? t.InternalId.ToString(), StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var versions = group.OrderBy(t => t.InternalId).ToList();
                foreach (var v in versions) seen.Add(v.InternalId);
                try
                {
                    var videoId = versions.Select(YouTubeId).FirstOrDefault(v => v != null);
                    var want = videoId != null && !versions.Any(v => own.Contains(v.InternalId));
                    // The version that already has the row keeps it; else the first one.
                    var owner = versions.FirstOrDefault(v => rowsByParent.ContainsKey(v.InternalId)) ?? versions[0];
                    var keep = false;
                    foreach (var v in versions)
                    {
                        if (!rowsByParent.TryGetValue(v.InternalId, out var mine)) continue;
                        foreach (var row in mine)
                        {
                            if (want && v == owner && !keep && RowVideoId(row) == videoId) { keep = true; continue; }
                            Delete(row, v);
                            outcome.Removed++;
                        }
                    }
                    if (want && !keep)
                    {
                        Create(owner, videoId!);
                        if (rowsByParent.ContainsKey(owner.InternalId)) { outcome.Updated++; outcome.Removed--; }
                        else outcome.Created++;
                        Thread.Sleep(Pace);
                    }
                }
                catch (Exception ex)
                {
                    outcome.Failed++;
                    Log.Warn("Trailer of " + group.First().Name + ": " + ex.Message);
                }
            }

            if (!removeOrphans) return;
            // Rows whose title is gone, or is one of the skipped copies.
            foreach (var row in rows.Where(r => !seen.Contains(r.ParentId)))
            {
                try
                {
                    Delete(row, null);
                    outcome.Removed++;
                }
                catch (Exception ex) { outcome.Failed++; Log.Warn("Remove row " + row.InternalId + ": " + ex.Message); }
            }
        }

        private static void Create(BaseItem owner, string videoId)
        {
            var row = new Trailer
            {
                // Per title and video: the same video under two titles must not share one item or its user data.
                // GetNewItemIdFromName keeps the case, and YouTube ids differ by case.
                Id = LibraryManager.GetNewItemIdFromName(ProviderKey + "|" + owner.Id.ToString("N") + "|" + videoId, typeof(Trailer)),
                Name = owner.Name + " – Trailer",
                ExtraType = ExtraType.Trailer,
                IsLocked = true,
                DateCreated = DateTimeOffset.UtcNow
            };
            row.SetProviderId(ProviderKey, videoId);
            row.SetParent(owner);
            LibraryManager.CreateItems(new List<BaseItem> { row }, owner, null, LibraryManager.GetCollectionFolders(owner),
                false, CancellationToken.None);
        }

        private static void Delete(BaseItem row, BaseItem? owner)
        {
            LibraryManager.DeleteItem(row, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }, owner, false);
        }

        /// <summary>
        /// New titles and titles whose metadata changed (from the library events): their row is made, changed
        /// or removed. A title whose own trailer just arrived loses its row.
        /// </summary>
        internal static void LinkNew(IEnumerable<long> ids)
        {
            lock (RunLock)
            {
                if (!Plugin.Instance.Configuration.Enabled) return;
                var titles = Titles(ids);
                if (titles.Count == 0) return;
                // The other versions of these titles too, so a group is judged whole.
                var keys = new HashSet<string>(titles.Select(t => t.PresentationUniqueKey ?? ""), StringComparer.Ordinal);
                if (titles.Count < 50)
                {
                    var all = Titles().Where(t => keys.Contains(t.PresentationUniqueKey ?? "")).ToList();
                    titles = all.Union(titles).GroupBy(t => t.InternalId).Select(g => g.First()).ToList();
                }
                var ids2 = new HashSet<long>(titles.Select(t => t.InternalId));
                var rows = Rows().Where(r => ids2.Contains(r.ParentId)).ToList();
                var outcome = new Outcome();
                Sync(titles, rows, outcome, CancellationToken.None, removeOrphans: false);
                if (outcome.Created + outcome.Updated + outcome.Removed > 0)
                    Log.Info("New titles: " + outcome.Created + " linked, " + outcome.Updated + " changed, " + outcome.Removed + " removed.");
            }
        }

        /// <summary>True when the link task has never run here (no links.json yet).</summary>
        internal static bool NeverRan => Load().Last == null;

        /// <summary>True when link files of an earlier version may still be there.</summary>
        internal static bool HasOldLinks()
        {
            if (Load().Files.Count > 0) return true;
            try { return OldLinkItems().Count > 0; }
            catch { return false; }
        }

        internal static Outcome RemoveAll()
        {
            lock (RunLock) return RemoveAllLocked("");
        }

        private static Outcome RemoveAllLocked(string prefix)
        {
            var outcome = new Outcome();
            foreach (var row in Rows())
            {
                try
                {
                    Delete(row, null);
                    outcome.Removed++;
                }
                catch (Exception ex)
                {
                    outcome.Failed++;
                    Log.Warn("Remove row " + row.InternalId + ": " + ex.Message);
                }
            }
            var state = Load();
            RemoveOldLinks(state, outcome, CancellationToken.None);
            outcome.Message = prefix + outcome.Removed + " trailer links removed" +
                              (outcome.OldLinksRemoved > 0 ? ", " + outcome.OldLinksRemoved + " old link files removed" : "") +
                              (outcome.Failed > 0 ? ", " + outcome.Failed + " failed" : "") + ".";
            Save(new State { Last = outcome });
            Log.Info(outcome.Message);
            return outcome;
        }

        /// <summary>The library's items for old link files (found by name; the file's content decides).</summary>
        private static List<BaseItem> OldLinkItems() =>
            LibraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Trailer), "Video", nameof(Movie) },
                ExtraTypes = TrailerTypes,
                Recursive = true
            }).Where(i => (i.Path ?? "").EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>
        /// Removes the link files earlier versions wrote (the ones on record and any the library knows by name),
        /// their library items and the trailers folders left empty. The library monitor is told beforehand, so
        /// no folder gets scanned for it.
        /// </summary>
        private static void RemoveOldLinks(State state, Outcome outcome, CancellationToken ct)
        {
            var items = new Dictionary<string, BaseItem?>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in state.Files) items[f] = null;
            try
            {
                foreach (var i in OldLinkItems()) items[i.Path] = i;
            }
            catch (Exception ex) { Log.Warn("Old link files: " + ex.Message); }
            if (items.Count == 0) return;
            var watch = Stopwatch.StartNew();

            foreach (var pair in items)
            {
                ct.ThrowIfCancellationRequested();
                var path = pair.Key;
                try
                {
                    var exists = File.Exists(path);
                    if (exists && !IsBridgeFile(path)) continue;
                    var dir = Path.GetDirectoryName(path)!;
                    if (exists)
                    {
                        var touched = new[] { path, dir, Path.GetDirectoryName(dir) }.Where(p => !string.IsNullOrEmpty(p)).ToArray();
                        foreach (var p in touched) LibraryMonitor.ReportFileSystemChangeBeginning(p!);
                        try
                        {
                            File.Delete(path);
                            RemoveFolderIfEmpty(dir);
                        }
                        finally
                        {
                            foreach (var p in touched) LibraryMonitor.ReportFileSystemChangeComplete(p!, false);
                        }
                    }
                    var item = pair.Value ?? LibraryManager.FindByPath(path, false);
                    if (item != null) Delete(item, null);
                    if (exists || item != null) outcome.OldLinksRemoved++;
                    Thread.Sleep(Pace);
                }
                catch (Exception ex)
                {
                    outcome.Failed++;
                    Log.Warn("Old link file " + path + ": " + ex.Message);
                }
            }
            Log.Info(outcome.OldLinksRemoved + " old link files removed in " + (int)watch.Elapsed.TotalSeconds + " s.");
        }

        private static void RemoveFolderIfEmpty(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && string.Equals(Path.GetFileName(dir), "trailers", StringComparison.OrdinalIgnoreCase)
                    && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { }
        }
    }
}
