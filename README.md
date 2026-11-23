# TrailerBridge

An Emby Server plugin that makes YouTube trailers play on clients that can't open YouTube links (older TV apps hang on a remote trailer).

For every movie and series with a YouTube trailer and no local trailer, TrailerBridge adds a local trailer to the title in Emby's library. It has no file: nothing is written into your media folders. When it's played, the plugin resolves the YouTube video with [yt-dlp](https://github.com/yt-dlp/yt-dlp) and hands Emby's ffmpeg YouTube's HLS stream (one video variant and its audio). Emby always plays it through its own ffmpeg (remux, or transcode when the client needs it), with seeking. Nothing is downloaded or stored.

## Install
Download `TrailerBridge.dll` from the [latest release](../../releases/latest), put it in Emby's `plugins` folder and restart Emby. Or install it with a GitHub repo plugin installer.

## Settings
- **Enabled**: turning it off removes all of TrailerBridge's trailers. Uninstalling the plugin removes them too.
- **yt-dlp**: found on PATH or downloaded into the plugin folder, updated weekly. yt-dlp needs a JavaScript runtime (deno or node); the plugin finds one or downloads deno.
- **Create links now / Remove all links**, and a scheduled task *TrailerBridge: update trailer links* (daily). New titles get their trailer about a minute after Emby adds them and fetches their trailer; on a fresh install the trailers are made once, two minutes after startup. A title that gets a trailer of its own loses TrailerBridge's.
- **Quality**: 1080p (default), 720p or 360p. TrailerBridge takes the best YouTube offers up to that, H.264 first (so older TVs get it without a transcode), VP9 when there is no H.264 at that size. If YouTube has no HLS for a video, it falls back to the 360p single-file mp4.

## Upgrading from 0.1.0.8 or earlier
Earlier versions wrote a `trailers/<title> - TrailerBridge.strm` link file into each title's folder. Two minutes after the first start of this version, in the background, TrailerBridge replaces them: it adds the new trailers, then deletes those link files (only its own: the name and the content must match), their library entries and the `trailers` folders that are left empty, without a library scan.

## Limits
- Some trailers are only on YouTube in 480p or lower; then that is what plays.
- The first play of a trailer takes a few seconds while yt-dlp looks it up; later plays are cached for a few hours.
- The play link only works for Emby itself (from the server, with the plugin's key), and only for trailers in the library.

## License
See [LICENSE](LICENSE).
