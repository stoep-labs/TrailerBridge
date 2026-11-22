using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TrailerBridge
{
    /// <summary>
    /// YouTube video id -> a direct googlevideo URL of a progressive mp4 (video and audio in one file),
    /// resolved with yt-dlp at play time and kept until shortly before it expires.
    /// </summary>
    internal static class Resolver
    {
        private static readonly Regex VideoIdPattern = new Regex(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
        private static readonly Regex YouTubeUrl = new Regex(
            @"^(?:https?://)?(?:www\.|m\.|music\.)?(?:youtube\.com/(?:watch\?(?:.*&)?v=|embed/|v/|shorts/)|youtu\.be/|youtube-nocookie\.com/embed/)([A-Za-z0-9_-]{11})(?:[?&#/].*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Progressive mp4 first (itag 18 = 360p H.264/AAC), then any single file with both streams.
        private const string Format = "18/best[ext=mp4][acodec!=none][vcodec!=none]/best[acodec!=none][vcodec!=none]";

        private static readonly ConcurrentDictionary<string, Resolved> Cache = new ConcurrentDictionary<string, Resolved>();
        // One yt-dlp per video at a time: Emby's ffprobe and ffmpeg ask within milliseconds of each other.
        private static readonly ConcurrentDictionary<string, Lazy<Task<Resolved>>> Pending = new ConcurrentDictionary<string, Lazy<Task<Resolved>>>();

        internal sealed class Resolved
        {
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

        internal static void Forget(string videoId) => Cache.TryRemove(videoId, out _);

        internal static async Task<string> GetUrlAsync(string videoId, CancellationToken ct)
        {
            if (!IsVideoId(videoId)) throw new ArgumentException("Not a YouTube video id");
            if (Cache.TryGetValue(videoId, out var hit) && hit.ValidUntilUtc > DateTime.UtcNow) return hit.Url;
            var lazy = Pending.GetOrAdd(videoId, id => new Lazy<Task<Resolved>>(() => ResolveAsync(id)));
            try
            {
                var r = await lazy.Value.ConfigureAwait(false);
                Cache[videoId] = r;
                return r.Url;
            }
            finally
            {
                Pending.TryRemove(videoId, out _);
            }
        }

        private static async Task<Resolved> ResolveAsync(string videoId)
        {
            // Not tied to one request: the next caller for the same video waits for this one.
            var ct = CancellationToken.None;
            var started = DateTime.UtcNow;
            var ytdlp = await YtDlp.EnsureAsync(ct).ConfigureAwait(false);
            var js = await YtDlp.JsRuntimeArgsAsync(ct).ConfigureAwait(false);
            var args = js + "--no-warnings --no-playlist --no-config -f " + YtDlp.Quote(Format) +
                       " --print " + YtDlp.Quote("%(available_at|0)s|%(url)s") +
                       " -- " + YtDlp.Quote("https://www.youtube.com/watch?v=" + videoId);
            var run = await YtDlp.RunAsync(ytdlp, args, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            var line = run.Out.Trim();
            var bar = line.IndexOf('|');
            if (run.Exit != 0 || bar < 0 || !line.Substring(bar + 1).StartsWith("http", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("yt-dlp could not resolve " + videoId + ": " + FirstLine(run.Err));
            var url = line.Substring(bar + 1).Trim();
            if (!IsGoogleVideo(url)) throw new InvalidOperationException("yt-dlp gave an unexpected host for " + videoId);

            // YouTube only serves the URL from "available_at" on (a few seconds after it is handed out).
            if (long.TryParse(line.Substring(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out var availableAt) && availableAt > 0)
            {
                var wait = DateTimeOffset.FromUnixTimeSeconds(availableAt) - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero && wait < TimeSpan.FromSeconds(30)) await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            await WaitUntilLiveAsync(url, ct).ConfigureAwait(false);

            Log.Info("Resolved " + videoId + " in " + (int)(DateTime.UtcNow - started).TotalMilliseconds + " ms");
            return new Resolved { Url = url, ValidUntilUtc = ValidUntil(url) };
        }

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

        /// <summary>From the URL's expire= (Unix seconds), 30 min early; at most 5 hours.</summary>
        private static DateTime ValidUntil(string url)
        {
            var max = DateTime.UtcNow.AddHours(5);
            var m = Regex.Match(url, @"[?&]expire=(\d+)");
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
