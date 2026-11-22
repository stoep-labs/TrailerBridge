using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TrailerBridge
{
    /// <summary>
    /// YouTube video id -> what Emby's ffmpeg plays: an HLS playlist of one variant up to the chosen quality
    /// (YouTube's own, video and audio as separate streams), else the progressive mp4 (360p, video and audio
    /// in one file). Resolved with yt-dlp at play time and kept until shortly before it expires.
    /// </summary>
    internal static class Resolver
    {
        private static readonly Regex VideoIdPattern = new Regex(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
        private static readonly Regex YouTubeUrl = new Regex(
            @"^(?:https?://)?(?:www\.|m\.|music\.)?(?:youtube\.com/(?:watch\?(?:.*&)?v=|embed/|v/|shorts/)|youtu\.be/|youtube-nocookie\.com/embed/)([A-Za-z0-9_-]{11})(?:[?&#/].*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MasterUrl = new Regex(@"https://manifest\.googlevideo\.com/api/manifest/hls_variant/[^""\s]+", RegexOptions.Compiled);

        // Progressive mp4 first (itag 18 = 360p H.264/AAC), then any single file with both streams; the last
        // resort only keeps yt-dlp from failing so the HLS master still comes through.
        private const string Format = "18/b/b*";

        private static readonly ConcurrentDictionary<string, Resolved> Cache = new ConcurrentDictionary<string, Resolved>();
        // One yt-dlp per video at a time: Emby's ffprobe and ffmpeg ask within milliseconds of each other.
        private static readonly ConcurrentDictionary<string, Lazy<Task<Resolved>>> Pending = new ConcurrentDictionary<string, Lazy<Task<Resolved>>>();

        internal sealed class Resolved
        {
            /// <summary>Master playlist for Emby's ffmpeg, or null when there is no HLS at this quality.</summary>
            public string? Playlist;
            /// <summary>Progressive mp4 (video and audio in one file), or "" when YouTube has none.</summary>
            public string Url = "";
            public DateTime ValidUntilUtc;
        }

        /// <summary>The YouTube video id in a trailer URL, or null when it is not a YouTube video.</summary>
        internal static string? VideoId(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var m = YouTubeUrl.Match(url!.Trim());
            return m.Success ? m.Groups[1].Value : null;
        }

        internal static bool IsVideoId(string? id) => id != null && VideoIdPattern.IsMatch(id);

        /// <summary>1080, 720 or 360 from the setting.</summary>
        internal static int Quality(int setting) => setting >= 1080 ? 1080 : setting >= 720 ? 720 : 360;

        internal static void Forget(string videoId)
        {
            foreach (var key in Cache.Keys)
                if (key.StartsWith(videoId + "@", StringComparison.Ordinal)) Cache.TryRemove(key, out _);
        }

        /// <summary>The progressive mp4 URL (for the proxy and LAN clients, which get bytes, not a playlist).</summary>
        internal static async Task<string> GetUrlAsync(string videoId, CancellationToken ct)
        {
            var r = await GetAsync(videoId, 360, ct).ConfigureAwait(false);
            if (r.Url.Length == 0) throw new InvalidOperationException("YouTube has no single-file format for " + videoId);
            return r.Url;
        }

        internal static async Task<Resolved> GetAsync(string videoId, int quality, CancellationToken ct)
        {
            if (!IsVideoId(videoId)) throw new ArgumentException("Not a YouTube video id");
            quality = Quality(quality);
            var key = videoId + "@" + quality;
            if (Cache.TryGetValue(key, out var hit) && hit.ValidUntilUtc > DateTime.UtcNow) return hit;
            var lazy = Pending.GetOrAdd(key, k => new Lazy<Task<Resolved>>(() => ResolveAsync(videoId, quality)));
            try
            {
                var r = await lazy.Value.ConfigureAwait(false);
                Cache[key] = r;
                return r;
            }
            finally
            {
                Pending.TryRemove(key, out _);
            }
        }

        private static async Task<Resolved> ResolveAsync(string videoId, int quality)
        {
            // Not tied to one request: the next caller for the same video waits for this one.
            var ct = CancellationToken.None;
            var started = DateTime.UtcNow;
            var ytdlp = await YtDlp.EnsureAsync(ct).ConfigureAwait(false);
            var js = await YtDlp.JsRuntimeArgsAsync(ct).ConfigureAwait(false);
            var args = js + "--no-warnings --no-playlist --no-config -f " + YtDlp.Quote(Format) +
                       " --print " + YtDlp.Quote("%(available_at|0)s|%(protocol)s|%(acodec)s|%(vcodec)s|%(url)s") +
                       (quality > 360 ? " --print " + YtDlp.Quote("%(formats.:.manifest_url)j") : "") +
                       " -- " + YtDlp.Quote("https://www.youtube.com/watch?v=" + videoId);
            var run = await YtDlp.RunAsync(ytdlp, args, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            var lines = run.Out.Replace("\r", "").Split('\n');
            var fields = lines[0].Trim().Split(new[] { '|' }, 5);
            if (run.Exit != 0 || fields.Length < 5)
                throw new InvalidOperationException("yt-dlp could not resolve " + videoId + ": " + FirstLine(run.Err));

            var r = new Resolved();
            var url = fields[4].Trim();
            if (fields[1] == "https" && fields[2] != "none" && fields[3] != "none" && IsGoogleVideo(url)) r.Url = url;
            string? master = null;
            if (quality > 360 && lines.Length > 1)
            {
                var m = MasterUrl.Match(lines[1]);
                if (m.Success && Hls.IsManifestUrl(m.Value)) master = m.Value;
            }

            // YouTube only serves the URLs from "available_at" on (a few seconds after they are handed out).
            if (long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var availableAt) && availableAt > 0)
            {
                var wait = DateTimeOffset.FromUnixTimeSeconds(availableAt) - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero && wait < TimeSpan.FromSeconds(30)) await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            var chosen = "";
            if (master != null)
            {
                try
                {
                    var v = await ChooseVariantAsync(master, quality, ct).ConfigureAwait(false);
                    if (v != null)
                    {
                        r.Playlist = Hls.Build(v);
                        chosen = "HLS " + v.Describe;
                    }
                }
                catch (Exception ex) { Log.Warn("HLS for " + videoId + ": " + ex.Message); }
            }
            if (r.Playlist == null)
            {
                if (r.Url.Length == 0) throw new InvalidOperationException("YouTube has no playable format for " + videoId);
                await WaitUntilLiveAsync(r.Url, ct).ConfigureAwait(false);
                chosen = "progressive 360p" + (quality > 360 ? " (no HLS up to " + quality + "p)" : "");
            }

            var until = DateTime.UtcNow.AddHours(5);
            if (r.Url.Length > 0) until = Min(until, ValidUntil(r.Url));
            if (r.Playlist != null && master != null) until = Min(until, ValidUntil(master));
            r.ValidUntilUtc = until;
            Log.Info("Resolved " + videoId + " at " + quality + "p: " + chosen + " in " + (int)(DateTime.UtcNow - started).TotalMilliseconds + " ms");
            return r;
        }

        /// <summary>
        /// The best variant whose playlists load, are not encrypted (some masters mix in DRM) and whose first
        /// segment answers (some masters list segments YouTube refuses).
        /// </summary>
        private static async Task<Hls.Variant?> ChooseVariantAsync(string masterUrl, int quality, CancellationToken ct)
        {
            var master = await GetTextAsync(masterUrl, ct).ConfigureAwait(false);
            foreach (var v in Hls.Candidates(master ?? "", quality).Take(3))
            {
                var ok = true;
                foreach (var u in new[] { v.Uri }.Concat(v.AudioUris))
                {
                    var media = await GetTextAsync(u, ct).ConfigureAwait(false);
                    var segment = media == null ? null : Hls.FirstSegment(media);
                    if (segment == null || !await AnswersAsync(segment, ct).ConfigureAwait(false)) { ok = false; break; }
                }
                if (ok) return v;
            }
            return null;
        }

        private static async Task<bool> AnswersAsync(string url, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using (var resp = await YtDlp.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    return resp.IsSuccessStatusCode;
            }
        }

        private static async Task<string?> GetTextAsync(string url, CancellationToken ct)
        {
            using (var resp = await YtDlp.Http.GetAsync(url, ct).ConfigureAwait(false))
                return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
        }

        private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

        /// <summary>A one-byte ranged GET until it answers 206 (or 15 s pass).</summary>
        private static async Task WaitUntilLiveAsync(string url, CancellationToken ct)
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            HttpStatusCode last = 0;
            while (true)
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    using (var resp = await YtDlp.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        last = resp.StatusCode;
                        if ((int)resp.StatusCode == 206 || resp.StatusCode == HttpStatusCode.OK) return;
                    }
                }
                if (DateTime.UtcNow > until) throw new InvalidOperationException("YouTube answered " + (int)last + " for the resolved URL");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }

        internal static bool IsGoogleVideo(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
            u.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);

        /// <summary>From the URL's expire= or /expire/ (Unix seconds), 30 min early; at most 5 hours.</summary>
        private static DateTime ValidUntil(string url)
        {
            var max = DateTime.UtcNow.AddHours(5);
            var m = Regex.Match(url, @"(?:[?&]expire=|/expire/)(\d+)");
            if (m.Success && long.TryParse(m.Groups[1].Value, out var exp))
            {
                var t = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime.AddMinutes(-30);
                return t < max ? t : max;
            }
            return DateTime.UtcNow.AddHours(1);
        }

        private static string FirstLine(string s)
        {
            s = (s ?? "").Trim();
            var i = s.IndexOf('\n');
            return i < 0 ? s : s.Substring(0, i).Trim();
        }
    }
}
