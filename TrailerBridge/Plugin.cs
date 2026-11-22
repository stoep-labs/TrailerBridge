using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TrailerBridge
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public override string Name => "TrailerBridge";

        // Stable GUID – never change after first deployment
        public override Guid Id => new Guid("7c1d4b9e-2f63-4a85-b0e7-91d3c5a6f248");

        public override string Description => "Plays YouTube trailers on clients that cannot open YouTube, through the server, without downloading them.";

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            DataPath = Path.Combine(applicationPaths.PluginsPath, "TrailerBridge");
            Directory.CreateDirectory(DataPath);
        }

        private static readonly object KeyLock = new object();

        /// <summary>The secret in the link files, made on first use (Configuration cannot be read in the constructor).</summary>
        internal string Key
        {
            get
            {
                lock (KeyLock)
                {
                    if (string.IsNullOrEmpty(Configuration.Key))
                    {
                        Configuration.Key = Guid.NewGuid().ToString("N");
                        SaveConfiguration();
                    }
                    return Configuration.Key;
                }
            }
        }

        public static Plugin Instance { get; private set; } = null!;

        /// <summary>yt-dlp, the JS runtime and the list of links live here.</summary>
        public string DataPath { get; }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            // The key is in every link file: the page never changes it.
            var before = Configuration;
            var wasEnabled = before.Enabled;
            var oldHost = before.LinkHost ?? "";
            if (configuration is PluginConfiguration incoming) incoming.Key = Key;
            base.UpdateConfiguration(configuration);
            // Turned on or off, or another link address: the link files follow at once.
            if (Configuration.Enabled != wasEnabled || (Configuration.LinkHost ?? "") != oldHost)
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { if (Links.LibraryManager != null) Links.Update(System.Threading.CancellationToken.None); }
                    catch (Exception ex) { Log.Warn("Updating links: " + ex.Message); }
                });
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            var names = GetType().Assembly.GetManifestResourceNames();
            var html = names.FirstOrDefault(r => r.EndsWith("configPage.html"));
            var js = names.FirstOrDefault(r => r.EndsWith("configPage.js"));
            if (html == null || js == null) return new List<PluginPageInfo>();
            return new List<PluginPageInfo>
            {
                new PluginPageInfo
                {
                    Name = "TrailerBridgeConfig",
                    EmbeddedResourcePath = html,
                    EnableInMainMenu = true,
                    DisplayName = "TrailerBridge",
                    MenuIcon = "movie"
                },
                new PluginPageInfo
                {
                    Name = "TrailerBridgeJS",
                    EmbeddedResourcePath = js
                }
            };
        }

        public Stream GetThumbImage()
        {
            var asm = GetType().Assembly;
            var path = asm.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("thumb.png"));
            return (path != null ? asm.GetManifestResourceStream(path) : Stream.Null) ?? Stream.Null;
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
    }
}
