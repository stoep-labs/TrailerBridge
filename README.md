# TrailerBridge

An Emby Server plugin that makes YouTube trailers play on clients that can't open YouTube links (older TV apps hang on a remote trailer).

For every movie with a YouTube trailer and no local trailer, TrailerBridge writes a tiny `.strm` link into the title's `trailers/` folder. Emby treats it as a local trailer. When it's played, the plugin resolves the YouTube video with [yt-dlp](https://github.com/yt-dlp/yt-dlp) and Emby streams it through its normal pipeline (direct stream or transcode). Nothing is downloaded or stored.

## Install
Download `TrailerBridge.dll` from the [latest release](../../releases/latest), put it in Emby's `plugins` folder and restart Emby. Or install it with a GitHub repo plugin installer.

## Settings
- **Enabled**: turning it off removes all link files.
- **yt-dlp**: found on PATH or downloaded into the plugin folder, updated weekly. yt-dlp needs a JavaScript runtime (deno or node); the plugin finds one or downloads deno.
- **Create links now / Remove all links**, and a scheduled task *TrailerBridge: update trailer links*.
- **Link address**: leave empty. Only set it to the server's LAN address if a client tries to open the trailer link itself instead of letting the server play it.

## Limits
- 360p (YouTube's single-file format).
- The first play of a trailer takes a few seconds while yt-dlp looks it up; later plays are cached.
- Titles in shared or flat folders (several movies in one folder) are skipped.
- The play link only works for Emby itself (and, with a link address set, the local network), and only for titles in the library.

## License
See [LICENSE](LICENSE).
