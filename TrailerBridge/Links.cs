using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace TrailerBridge
{
    /// <summary>
    /// The link files: "trailers/&lt;folder name&gt; - TrailerBridge.strm" in the folder of every movie and
    /// series that has a YouTube trailer and no trailer of its own. Emby takes the file as a local trailer;
    /// the URL in it is this server's play endpoint, which Emby's own ffmpeg calls when the trailer plays.
    /// </summary>
    internal static class Links
    {
        internal const string Suffix = " - TrailerBridge.strm";
        internal const string Marker = "/TrailerBridge/Play/";
        private const string TrailersFolder = "trailers";
        private static readonly string[] VideoExtensions =
        {
            ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".flv", ".iso", ".strm", ".3gp", ".ogv", ".vob"
        };

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
            public int SharedFolder { get; set; }
            public int Failed { get; set; }
            public string Message { get; set; } = "";
            public DateTime When { get; set; } = DateTime.UtcNow;
        }

        /// <summary>The state the settings page shows: the links there are now, and the last run.</summary>
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

        internal static string PlayUrl(BaseItem item) =>
            "http://" + LinkHost() + ":" + Host.HttpPort + "/emby" + Marker + item.InternalId + "?k=" + Plugin.Instance.Key;

        private static string LinkHost()
        {
            var h = (Plugin.Instance.Configuration.LinkHost ?? "").Trim();
            if (h.Length == 0) return "127.0.0.1";
            return h.Contains(":") && !h.StartsWith("[") ? "[" + h + "]" : h;
        }

        /// <summary>The link file for a title in this folder.</summary>
        private static string LinkPath(string folder) =>
            Path.Combine(folder, TrailersFolder, Path.GetFileName(folder.TrimEnd('\\', '/')) + Suffix);

        /// <summary>
        /// The container Emby stored when it first probed the title's link ("hls", "mp4", ...), or null when it
        /// has not played it yet. Emby keeps that first probe and later tells ffmpeg the format (-f mp4).
        /// </summary>
        internal static string? ProbedContainer(BaseItem item)
        {
            var folder = item.ContainingFolderPath;
            if (string.IsNullOrEmpty(folder)) return null;
            var trailer = LibraryManager.FindByPath(LinkPath(folder), false);
            var container = trailer?.Container;
            return string.IsNullOrEmpty(container) ? null : container;
        }

        /// <summary>The YouTube video id of the item's first YouTube remote trailer.</summary>
        internal static string? YouTubeId(BaseItem item) =>
            (item.RemoteTrailers ?? Array.Empty<string>()).Select(Resolver.VideoId).FirstOrDefault(v => v != null);

        internal static bool IsBridgeFile(string path) =>
            path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) && ReadsAsOurs(path);

        private static bool ReadsAsOurs(string path)
        {
            try { return new FileInfo(path).Length < 4096 && File.ReadAllText(path).Contains(Marker); }
            catch { return false; }
        }

        private static IEnumerable<BaseItem> Candidates() =>
            LibraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", "Series" },
                Recursive = true,
                IsVirtualItem = false
            });

        /// <summary>Creates, rewrites and removes link files so they match the library now.</summary>
        internal static Outcome Update(CancellationToken ct)
        {
            lock (RunLock)
            {
                if (!Plugin.Instance.Configuration.Enabled) return RemoveAllLocked("TrailerBridge is off: ");
                var outcome = new Outcome();
                var files = new List<string>();
                var items = Candidates().ToList();

                var shared = SharedFolders(items);
                var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    var folder = item.ContainingFolderPath;
                    if (string.IsNullOrEmpty(folder) || !done.Add(folder)) continue;
                    var link = LinkFolder(item, folder, shared, outcome);
                    if (link != null) files.Add(link);
                }

                outcome.Linked = files.Count;
                outcome.Message = outcome.Linked + " titles linked (" + outcome.Created + " new, " + outcome.Updated + " rewritten, " +
                                  outcome.Removed + " removed" + (outcome.SharedFolder > 0 ? ", " + outcome.SharedFolder + " skipped in shared folders" : "") +
                                  (outcome.Failed > 0 ? ", " + outcome.Failed + " failed" : "") + ").";
                Save(new State { Files = files, Last = outcome });
                Log.Info(outcome.Message);
                return outcome;
            }
        }

        /// <summary>
        /// Folders with more than one title in them (a flat folder of movies): a trailers folder there would
        /// belong to all of them. Versions of one movie share a folder and a name.
        /// </summary>
        private static HashSet<string> SharedFolders(List<BaseItem> items) =>
            new HashSet<string>(
                items.Where(i => i is not MediaBrowser.Controller.Entities.TV.Series && !string.IsNullOrEmpty(i.ContainingFolderPath))
                     .GroupBy(i => i.ContainingFolderPath, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Select(i => (i.Name ?? "") + "|" + i.ProductionYear).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                     .Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);

        /// <summary>Makes one folder's link files match the rules; returns the link that is there now, or null.</summary>
        private static string? LinkFolder(BaseItem item, string folder, HashSet<string> shared, Outcome outcome)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;
                var trailers = Path.Combine(folder, TrailersFolder);
                var link = LinkPath(folder);
                var bridges = Directory.Exists(trailers)
                    ? Directory.GetFiles(trailers, "*" + Suffix).Where(IsBridgeFile).ToList()
                    : new List<string>();

                var mixed = (item as Video)?.IsInMixedFolder == true || shared.Contains(folder);
                var want = !mixed && YouTubeId(item) != null && !HasOwnTrailer(item, folder, trailers);
                if (mixed && YouTubeId(item) != null) outcome.SharedFolder++;

                foreach (var b in bridges.Where(b => !want || !string.Equals(b, link, StringComparison.OrdinalIgnoreCase)))
                {
                    File.Delete(b);
                    outcome.Removed++;
                    RemoveFolderIfEmpty(trailers);
                    LibraryMonitor.ReportFileSystemChanged(Directory.Exists(trailers) ? trailers : folder);
                }
                if (!want) return null;

                var content = PlayUrl(item);
                var existing = File.Exists(link) ? File.ReadAllText(link) : null;
                if (existing != content)
                {
                    Directory.CreateDirectory(trailers);
                    File.WriteAllText(link, content);
                    if (existing == null) outcome.Created++; else outcome.Updated++;
                    LibraryMonitor.ReportFileSystemChanged(trailers);
                }
                return link;
            }
            catch (Exception ex)
            {
                outcome.Failed++;
                Log.Warn("Link for " + item.Name + " (" + folder + "): " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// New titles (from the library events): makes the link for each one that has a YouTube trailer and no
        /// link yet. Titles that already have their link are left alone, so the folder refresh our own write
        /// causes does nothing.
        /// </summary>
        internal static void LinkNew(IEnumerable<long> ids)
        {
            lock (RunLock)
            {
                if (!Plugin.Instance.Configuration.Enabled) return;
                var todo = ids.Select(id => LibraryManager.GetItemById(id))
                              .Where(i => i != null && YouTubeId(i) != null && !string.IsNullOrEmpty(i.ContainingFolderPath)
                                          && !File.Exists(LinkPath(i.ContainingFolderPath)))
                              .GroupBy(i => i.ContainingFolderPath, StringComparer.OrdinalIgnoreCase)
                              .Select(g => g.First())
                              .ToList();
                if (todo.Count == 0) return;

                var shared = SharedFolders(Candidates().ToList());
                var outcome = new Outcome();
                var made = new List<string>();
                foreach (var item in todo)
                {
                    var before = outcome.Created;
                    var link = LinkFolder(item, item.ContainingFolderPath, shared, outcome);
                    if (link != null && outcome.Created > before)
                    {
                        made.Add(link);
                        Log.Info("Linked new title " + item.Name + ".");
                    }
                }
                if (made.Count == 0) return;
                var state = Load();
                state.Files = state.Files.Union(made, StringComparer.OrdinalIgnoreCase).ToList();
                Save(state);
            }
        }

        /// <summary>True when the link task has never run here (no links.json yet).</summary>
        internal static bool NeverRan => Load().Last == null;

        internal static Outcome RemoveAll()
        {
            lock (RunLock) return RemoveAllLocked("");
        }

        private static Outcome RemoveAllLocked(string prefix)
        {
            var outcome = new Outcome();
            // The files on record, and any in the library's trailers folders (a record lost or out of date).
            var paths = new HashSet<string>(Load().Files, StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Candidates().Select(i => i.ContainingFolderPath).Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var trailers = Path.Combine(folder, TrailersFolder);
                    if (Directory.Exists(trailers))
                        foreach (var f in Directory.GetFiles(trailers, "*" + Suffix)) paths.Add(f);
                }
                catch { }
            }
            foreach (var p in paths)
            {
                try
                {
                    if (!File.Exists(p) || !IsBridgeFile(p)) continue;
                    File.Delete(p);
                    outcome.Removed++;
                    var dir = Path.GetDirectoryName(p)!;
                    RemoveFolderIfEmpty(dir);
                    LibraryMonitor.ReportFileSystemChanged(Directory.Exists(dir) ? dir : Path.GetDirectoryName(dir)!);
                }
                catch (Exception ex)
                {
                    outcome.Failed++;
                    Log.Warn("Remove " + p + ": " + ex.Message);
                }
            }
            outcome.Message = prefix + outcome.Removed + " links removed" + (outcome.Failed > 0 ? ", " + outcome.Failed + " failed" : "") + ".";
            Save(new State { Last = outcome });
            Log.Info(outcome.Message);
            return outcome;
        }

        /// <summary>A trailer that is not ours: in Emby's extras, in the trailers folder, or "-trailer" beside the video.</summary>
        private static bool HasOwnTrailer(BaseItem item, string folder, string trailers)
        {
            try
            {
                if (item.GetExtras(new[] { ExtraType.Trailer }).Any(t => !string.IsNullOrEmpty(t.Path) && !t.Path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch { }
            if (Directory.Exists(trailers) &&
                Directory.EnumerateFiles(trailers).Any(f => IsVideo(f) && !f.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)))
                return true;
            return Directory.EnumerateFiles(folder).Any(f =>
            {
                var n = Path.GetFileNameWithoutExtension(f);
                return IsVideo(f) && (n.EndsWith("-trailer", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".trailer", StringComparison.OrdinalIgnoreCase)
                                      || n.EndsWith("_trailer", StringComparison.OrdinalIgnoreCase) || n.Equals("trailer", StringComparison.OrdinalIgnoreCase));
            });
        }

        private static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        private static void RemoveFolderIfEmpty(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { }
        }
    }
}
