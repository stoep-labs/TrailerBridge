# TrailerBridge

An Emby Server plugin that makes YouTube trailers play on clients that can't open YouTube links (older TV apps hang on a remote trailer).

For every movie with a YouTube trailer and no local trailer, TrailerBridge writes a tiny `.strm` link into the title's `trailers/` folder. Emby treats it as a local trailer. When it's played, the plugin resolves the YouTube video with [yt-dlp](https://github.com/yt-dlp/yt-dlp) and hands Emby's ffmpeg YouTube's HLS stream (one video variant and its audio). Emby streams it through its normal pipeline (direct stream, remux or transcode), with seeking. Nothing is downloaded or stored.

## Install
Download `TrailerBridge.dll` from the [latest release](../../releases/latest), put it in Emby's `plugins` folder and restart Emby. Or install it with a GitHub repo plugin installer.

## Settings
- **Enabled**: turning it off removes all link files.
- **yt-dlp**: found on PATH or downloaded into the plugin folder, updated weekly. yt-dlp needs a JavaScript runtime (deno or node); the plugin finds one or downloads deno.
- **Create links now / Remove all links**, and a scheduled task *TrailerBridge: update trailer links*.
- **Quality**: 1080p (default), 720p or 360p. TrailerBridge takes the best YouTube offers up to that, H.264 first (so older TVs get it without a transcode), VP9 when there is no H.264 at that size. If YouTube has no HLS for a video, it falls back to the 360p single-file mp4.
- **Link address**: leave empty. Only set it to the server's LAN address if a client tries to open the trailer link itself instead of letting the server play it.

## Limits
- Above 360p only when Emby's own ffmpeg plays the link (the normal case). A client that opens the link itself (with a link address set) gets the 360p mp4 through the server.
- Some trailers are only on YouTube in 480p or lower; then that is what plays.
- Emby keeps what it learned about a trailer the first time it played it. A trailer it first played as the 360p mp4 (links from version 0.1, Quality set to 360p at the time, or YouTube's HLS failing on that first play) stays 360p. To give those trailers a fresh start: **Remove all links**, run a library scan, then **Create links now**.
- The first play of a trailer takes a few seconds while yt-dlp looks it up; later plays are cached.
- Titles in shared or flat folders (several movies in one folder) are skipped.
- The play link only works for Emby itself (and, with a link address set, the local network), and only for titles in the library.

## License
See [LICENSE](LICENSE).
