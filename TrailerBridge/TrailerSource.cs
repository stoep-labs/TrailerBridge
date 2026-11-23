using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TrailerBridge
{
    /// <summary>
    /// The one source of a trailer row: this server's play endpoint, which only Emby's own ffmpeg opens. Direct
    /// play and direct streaming are off, so every client gets Emby's stream.
    /// </summary>
    public class TrailerSource : IMediaSourceProvider
    {
        public async Task<List<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            var videoId = Links.RowVideoId(item);
            if (videoId == null || Plugin.Instance == null || !Plugin.Instance.Configuration.Enabled) return new List<MediaSourceInfo>();

            var config = Plugin.Instance.Configuration;
            var r = await Resolver.GetAsync(videoId, config.Quality, cancellationToken).ConfigureAwait(false);
            var v = config.Proxy ? null : r.Variant;
            var source = new MediaSourceInfo
            {
                Id = "trailerbridge_" + item.Id.ToString("N"),
                Path = Links.PlayUrl(item),
                Protocol = MediaProtocol.Http,
                Type = MediaSourceType.Default,
                Container = v != null ? "hls" : "mp4",
                Name = v != null ? v.Height + "p" : "360p",
                IsRemote = false,
                SupportsDirectPlay = false,
                SupportsDirectStream = false,
                SupportsTranscoding = true,
                SupportsProbing = false,
                RequiresOpening = false,
                RequiresClosing = false,
                RunTimeTicks = r.Duration > 0 ? (long)(r.Duration * TimeSpan.TicksPerSecond) : (long?)null,
                Bitrate = v != null && v.Bandwidth > 0 ? (int)Math.Min(v.Bandwidth, int.MaxValue) : 1000000,
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video, Index = 0,
                        Codec = v?.VideoCodec ?? "h264",
                        Width = v?.Width ?? 640, Height = v?.Height ?? 360,
                        BitRate = v != null && v.Bandwidth > 0 ? (int)Math.Min(v.Bandwidth, int.MaxValue) : (int?)null,
                        IsDefault = true
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio, Index = 1,
                        Codec = v?.AudioCodec ?? "aac",
                        Channels = v?.AudioChannels ?? 2,
                        IsDefault = true
                    }
                },
                DefaultAudioStreamIndex = 1
            };
            return new List<MediaSourceInfo> { source };
        }

        // Sources are never opened.
        public Task<ILiveStream> OpenMediaSource(string openToken, string consumerId, List<ILiveStream> currentLiveStreams,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
