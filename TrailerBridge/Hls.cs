using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TrailerBridge
{
    /// <summary>
    /// YouTube's HLS master playlist cut down to one video variant and its audio, so Emby's ffmpeg sees one
    /// video and one audio stream. The URIs stay YouTube's own (manifest.googlevideo.com): ffmpeg fetches
    /// the playlists and segments itself, nothing goes through the plugin.
    /// </summary>
    internal static class Hls
    {
        private static readonly Regex Attribute = new Regex(@"([A-Z0-9-]+)=(""[^""]*""|[^,]*)", RegexOptions.Compiled);

        internal sealed class Variant
        {
            public string Info = "";
            public string Uri = "";
            public int Width;
            public int Height;
            public long Bandwidth;
            public string Codec = "";
            public string? AudioGroup;
            public List<string> Audio = new List<string>();
            public List<string> AudioUris = new List<string>();

            /// <summary>1080, 720, ... from the frame size, so a 1920x800 scope picture counts as 1080p.</summary>
            public int Lines => Math.Max(Math.Min(Width, Height), (int)Math.Round(Math.Max(Width, Height) * 9 / 16.0));

            public string Describe => Width + "x" + Height + " " + Codec;
        }

        /// <summary>
        /// The variants at or below the quality, best first: most lines, then H.264 (old TVs play it as is),
        /// then VP9, then the rest; empty when the master is protected or not YouTube's.
        /// </summary>
        internal static List<Variant> Candidates(string master, int quality)
        {
            var none = new List<Variant>();
            var lines = master.Replace("\r", "").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "#EXTM3U") return none;
            if (lines.Any(l => l.StartsWith("#EXT-X-SESSION-KEY", StringComparison.Ordinal) || l.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))) return none;

            var audio = new Dictionary<string, List<(string Line, string Uri)>>(StringComparer.Ordinal);
            var variants = new List<Variant>();
            for (var i = 0; i < lines.Length; i++)
            {
                var l = lines[i].Trim();
                if (l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
                {
                    var a = Attributes(l);
                    if (Get(a, "TYPE") != "AUDIO") continue;
                    var group = Get(a, "GROUP-ID");
                    var uri = Get(a, "URI");
                    if (group.Length == 0 || !IsManifestUrl(uri)) continue;
                    if (!audio.TryGetValue(group, out var list)) audio[group] = list = new List<(string, string)>();
                    list.Add((l, uri));
                }
                else if (l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    var uri = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
                    if (!IsManifestUrl(uri)) continue;
                    var a = Attributes(l);
                    var res = Get(a, "RESOLUTION").Split('x');
                    if (res.Length != 2 || !int.TryParse(res[0], out var w) || !int.TryParse(res[1], out var h)) continue;
                    var codec = Get(a, "CODECS").Split(',').Select(c => c.Trim()).FirstOrDefault(IsVideoCodec);
                    if (codec == null) continue;
                    long.TryParse(Get(a, "BANDWIDTH"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bw);
                    var g = Get(a, "AUDIO");
                    variants.Add(new Variant { Info = l, Uri = uri, Width = w, Height = h, Bandwidth = bw, Codec = codec, AudioGroup = g.Length == 0 ? null : g });
                }
            }

            foreach (var v in variants)
            {
                if (v.AudioGroup == null) continue; // video and audio in one stream
                if (!audio.TryGetValue(v.AudioGroup, out var list)) { v.Uri = ""; continue; }
                v.Audio.AddRange(list.Select(x => x.Line));
                v.AudioUris.AddRange(list.Select(x => x.Uri));
            }

            return variants
                .Where(v => v.Uri.Length > 0 && v.Lines <= quality)
                .OrderByDescending(v => v.Lines)
                .ThenBy(v => CodecRank(v.Codec))
                .ThenByDescending(v => v.Bandwidth)
                .ToList();
        }

        /// <summary>
        /// A master playlist with just this variant and its audio. The variant comes first so ffmpeg numbers
        /// the video 0 and the audio 1, as in the mp4: Emby keeps the first probe and maps streams by number.
        /// </summary>
        internal static string Build(Variant v)
        {
            var sb = new StringBuilder();
            sb.Append("#EXTM3U\n#EXT-X-INDEPENDENT-SEGMENTS\n");
            sb.Append(v.Info).Append('\n').Append(v.Uri).Append('\n');
            foreach (var a in v.Audio) sb.Append(a).Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// The first segment of a media playlist ffmpeg can play (segments on googlevideo, not encrypted),
        /// or null.
        /// </summary>
        internal static string? FirstSegment(string playlist)
        {
            if (!playlist.StartsWith("#EXTM3U", StringComparison.Ordinal) || playlist.Contains("#EXT-X-KEY")) return null;
            var segments = playlist.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)).ToList();
            return segments.Count > 0 && segments.All(Resolver.IsGoogleVideo) ? segments[0] : null;
        }

        internal static bool IsManifestUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
            string.Equals(u.Host, "manifest.googlevideo.com", StringComparison.OrdinalIgnoreCase);

        private static bool IsVideoCodec(string c) =>
            c.StartsWith("avc", StringComparison.OrdinalIgnoreCase) || c.StartsWith("vp09", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("vp9", StringComparison.OrdinalIgnoreCase) || c.StartsWith("av01", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase) || c.StartsWith("hev1", StringComparison.OrdinalIgnoreCase);

        private static int CodecRank(string c) =>
            c.StartsWith("avc", StringComparison.OrdinalIgnoreCase) ? 0
            : c.StartsWith("vp", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

        private static Dictionary<string, string> Attributes(string line)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            var colon = line.IndexOf(':');
            foreach (Match m in Attribute.Matches(colon < 0 ? "" : line.Substring(colon + 1)))
                d[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
            return d;
        }

        private static string Get(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v : "";
    }
}
