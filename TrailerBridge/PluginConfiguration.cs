using MediaBrowser.Model.Plugins;

namespace TrailerBridge
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // Off: the trailers are removed and the play endpoint answers 404.
        public bool Enabled { get; set; } = true;

        // Empty = yt-dlp on PATH, else the release binary the plugin downloads into its folder.
        public string YtDlpPath { get; set; } = "";

        // Secret in every trailer's source URL, so only the server (which reads it) can call the play endpoint.
        public string Key { get; set; } = "";

        // 1080, 720 or 360: the highest the server's ffmpeg gets (HLS, H.264 first); 360 is the single-file mp4.
        // The proxy always gets the 360p mp4.
        public int Quality { get; set; } = 1080;

        // false = 302 to the YouTube video URL (the server's ffmpeg follows it); true = the bytes go through the plugin.
        public bool Proxy { get; set; } = false;
    }
}
