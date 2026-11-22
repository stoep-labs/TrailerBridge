using MediaBrowser.Model.Plugins;

namespace TrailerBridge
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // Off: the scheduled task removes the links and the play endpoint answers 404.
        public bool Enabled { get; set; } = true;

        // Empty = yt-dlp on PATH, else the release binary the plugin downloads into its folder.
        public string YtDlpPath { get; set; } = "";

        // Secret in every link file, so only the server (which reads them) can call the play endpoint.
        public string Key { get; set; } = "";

        // Host in the link files. Empty = 127.0.0.1 (only the server plays them); a LAN address lets clients
        // that play the link themselves (older apps) reach it from the local network.
        public string LinkHost { get; set; } = "";

        // 1080, 720 or 360: the highest the server's ffmpeg gets (HLS, H.264 first); 360 is the single-file mp4.
        // Clients that play the link themselves and the proxy always get the 360p mp4.
        public int Quality { get; set; } = 1080;

        // false = 302 to the YouTube video URL (the server's ffmpeg follows it); true = the bytes go through the plugin.
        public bool Proxy { get; set; } = false;
    }
}
