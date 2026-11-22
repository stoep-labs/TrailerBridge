using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TrailerBridge
{
    // ---- Requests ----

    // Called by Emby's own ffprobe / ffmpeg with the URL in a link file: no Emby token, so it checks
    // the plugin key, a loopback caller, and only plays the YouTube trailer of an item in the library.
    [Route("/TrailerBridge/Play/{Id}", "GET")]
    [Route("/TrailerBridge/Play/{Id}", "HEAD")]
    [Unauthenticated]
    public class PlayRequest
    {
        public string Id { get; set; } = "";
        public string K { get; set; } = "";
    }

    [Route("/TrailerBridge/Status", "GET")]
    [Authenticated(Roles = "Admin")]
    public class StatusRequest : IReturn<StatusResponse> { }

    public class StatusResponse
    {
        public bool Enabled { get; set; }
        public string YtDlpPath { get; set; } = "";
        public string YtDlpVersion { get; set; } = "";
        public bool YtDlpOwn { get; set; }
        public string JsRuntime { get; set; } = "";
        public int Linked { get; set; }
        public string LastMessage { get; set; } = "";
        public DateTime? LastRun { get; set; }
    }

    [Route("/TrailerBridge/Links/Create", "POST")]
    [Authenticated(Roles = "Admin")]
    public class CreateLinksRequest : IReturn<Links.Outcome> { }

    [Route("/TrailerBridge/Links/Remove", "POST")]
    [Authenticated(Roles = "Admin")]
    public class RemoveLinksRequest : IReturn<Links.Outcome> { }

    [Route("/TrailerBridge/YtDlp/Update", "POST")]
    [Authenticated(Roles = "Admin")]
    public class UpdateYtDlpRequest : IReturn<MessageResponse> { }

    public class MessageResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    public class TrailerBridgeService : IService, IRequiresRequest
    {
        private readonly IHttpResultFactory _results;

        public TrailerBridgeService(ILogManager logManager, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor,
            IServerApplicationHost host, IJsonSerializer json, IHttpResultFactory results)
        {
            Log.Initialize(logManager);
            Links.Initialize(libraryManager, libraryMonitor, host, json);
            _results = results;
        }

        public IRequest Request { get; set; } = null!;

        public async Task<object> Get(PlayRequest request)
        {
            var config = Plugin.Instance.Configuration;
            var ip = Request.RemoteIp;
            var ua = Request.Headers["User-Agent"] ?? "";
            var range = Request.Headers["Range"] ?? "";
            Log.Info("Play " + request.Id + " from " + ip + " (" + ua + ")" + (range.Length > 0 ? " Range: " + range : ""));

            if (!config.Enabled || !long.TryParse(request.Id, out var itemId)) throw new ResourceNotFoundException();
            if (!KeyMatches(request.K, Plugin.Instance.Key)) throw new SecurityException("Wrong key");
            var forwarded = !string.IsNullOrEmpty(Request.Headers["X-Forwarded-For"]);
            var loopback = ip != null && IPAddress.IsLoopback(ip) && !forwarded;
            // With a LAN link address, a client on the local network that plays the link itself is let in too.
            var lan = !loopback && !forwarded && ip != null && !string.IsNullOrWhiteSpace(config.LinkHost) && IsPrivate(ip);
            if (!loopback && !lan) throw new SecurityException("Trailer links only play on the server or the local network");

            var item = Links.LibraryManager.GetItemById(itemId);
            var videoId = item == null ? null : Links.YouTubeId(item);
            if (videoId == null) throw new ResourceNotFoundException();

            var head = string.Equals(Request.Verb, "HEAD", StringComparison.OrdinalIgnoreCase);
            // Emby's ffmpeg on this machine gets the HLS playlist (it fetches the segments from YouTube itself)
            // or a redirect to the mp4. A client gets the mp4 bytes through the server: the URLs are tied to
            // the server's address, and old TVs may not manage YouTube's TLS.
            if (loopback && !config.Proxy)
            {
                var source = await Resolver.GetAsync(videoId, config.Quality, CancellationToken.None).ConfigureAwait(false);
                if (source.Playlist == null) return _results.GetRedirectResult(source.Url);
                // Only ffmpeg gets the playlist. Emby's direct play passes the link's response on to the client
                // as is, and a client can't load YouTube's segments (bound to this server's address); an mp4
                // instead would play there at 360p. Refused, the client falls back to Emby's own stream
                // (ffmpeg, video copied). The User-Agent only picks the answer; the checks above decide access.
                if (!ua.StartsWith("Lavf/", StringComparison.Ordinal)) throw new ResourceNotFoundException();
                return new PlaylistWriter(source.Playlist, head);
            }
            var url = await Resolver.GetUrlAsync(videoId, CancellationToken.None).ConfigureAwait(false);
            return new ProxyWriter(videoId, url, range, head);
        }

        public Task<object> Head(PlayRequest request) => Get(request);

        public async Task<object> Get(StatusRequest request)
        {
            var path = YtDlp.Locate();
            var state = Links.Load();
            var js = YtDlp.LocateJsRuntime();
            return new StatusResponse
            {
                Enabled = Plugin.Instance.Configuration.Enabled,
                YtDlpPath = path ?? "",
                YtDlpOwn = path != null && YtDlp.IsOwn(path),
                YtDlpVersion = path == null ? "" : await YtDlp.VersionAsync(CancellationToken.None).ConfigureAwait(false),
                JsRuntime = js == null ? "" : js.Value.Name + " (" + js.Value.Path + ")",
                Linked = state.Files.Count,
                LastMessage = state.Last?.Message ?? "",
                LastRun = state.Last?.When
            };
        }

        public object Post(CreateLinksRequest request) => Links.Update(CancellationToken.None);

        public object Post(RemoveLinksRequest request) => Links.RemoveAll();

        public async Task<object> Post(UpdateYtDlpRequest request)
        {
            try
            {
                var msg = await YtDlp.UpdateAsync(CancellationToken.None).ConfigureAwait(false);
                return new MessageResponse { Success = true, Message = msg };
            }
            catch (Exception ex)
            {
                return new MessageResponse { Message = ex.Message };
            }
        }

        private static bool IsPrivate(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        private static bool KeyMatches(string? given, string expected)
        {
            given ??= "";
            if (expected.Length == 0 || given.Length != expected.Length) return false;
            var diff = 0;
            for (var i = 0; i < given.Length; i++) diff |= given[i] ^ expected[i];
            return diff == 0;
        }
    }

    /// <summary>The cut-down HLS master playlist (its URIs are all on manifest.googlevideo.com).</summary>
    internal sealed class PlaylistWriter : IAsyncStreamWriter
    {
        private readonly byte[] _body;
        private readonly bool _head;

        internal PlaylistWriter(string playlist, bool head)
        {
            _body = System.Text.Encoding.UTF8.GetBytes(playlist);
            _head = head;
        }

        public async Task WriteToAsync(IResponse response, CancellationToken ct)
        {
            response.StatusCode = 200;
            response.ContentType = "application/vnd.apple.mpegurl";
            // No Content-Length on purpose: with one, ffprobe takes the playlist's few bytes over the trailer's
            // duration as its bitrate (a few hundred bit/s), and Emby then transcodes to that.
            if (_head) return;
            await response.OutputWriter.WriteAsync(new ReadOnlyMemory<byte>(_body), ct).ConfigureAwait(false);
        }
    }

    /// <summary>The bytes of the googlevideo URL through the plugin, with the caller's Range passed on.</summary>
    internal sealed class ProxyWriter : IAsyncStreamWriter
    {
        private readonly string _videoId;
        private readonly string _url;
        private readonly string _range;
        private readonly bool _head;

        internal ProxyWriter(string videoId, string url, string range, bool head)
        {
            _videoId = videoId;
            _url = url;
            _range = range;
            _head = head;
        }

        public async Task WriteToAsync(IResponse response, CancellationToken ct)
        {
            var url = _url;
            HttpResponseMessage upstream = await SendAsync(url, ct).ConfigureAwait(false);
            if (upstream.StatusCode == HttpStatusCode.Forbidden || upstream.StatusCode == HttpStatusCode.Gone)
            {
                // Expired or no longer valid: resolve once more.
                upstream.Dispose();
                Resolver.Forget(_videoId);
                url = await Resolver.GetUrlAsync(_videoId, ct).ConfigureAwait(false);
                upstream = await SendAsync(url, ct).ConfigureAwait(false);
            }
            using (upstream)
            {
                response.StatusCode = (int)upstream.StatusCode;
                var c = upstream.Content.Headers;
                response.ContentType = c.ContentType?.ToString() ?? "video/mp4";
                response.AddHeader("Accept-Ranges", "bytes");
                if (c.ContentRange != null) response.AddHeader("Content-Range", c.ContentRange.ToString());
                if (c.ContentLength.HasValue) response.SetContentLength(c.ContentLength.Value);
                if (_head || !upstream.IsSuccessStatusCode) return;

                using (var src = await upstream.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        var flush = await response.OutputWriter.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, n), ct).ConfigureAwait(false);
                        if (flush.IsCanceled || flush.IsCompleted) break;
                    }
                }
            }
        }

        private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
        {
            var req = new HttpRequestMessage(_head ? HttpMethod.Head : HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_range)) req.Headers.TryAddWithoutValidation("Range", _range);
            return await YtDlp.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
    }
}
