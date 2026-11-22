using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TrailerBridge
{
    /// <summary>
    /// yt-dlp and the JavaScript runtime it needs for YouTube: the configured path, else one on PATH,
    /// else the official release binary downloaded into the plugin folder (and kept up to date there).
    /// </summary>
    internal static class YtDlp
    {
        internal static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        private static readonly SemaphoreSlim InstallLock = new SemaphoreSlim(1, 1);

        static YtDlp()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("TrailerBridge-Emby-Plugin");
        }

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        private static bool IsArm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;

        private static string OwnYtDlp => Path.Combine(Plugin.Instance.DataPath, IsWindows ? "yt-dlp.exe" : "yt-dlp");
        private static string OwnDeno => Path.Combine(Plugin.Instance.DataPath, IsWindows ? "deno.exe" : "deno");

        // Release asset names: https://github.com/yt-dlp/yt-dlp/releases, https://github.com/denoland/deno/releases
        private static string YtDlpAsset =>
            IsWindows ? (IsArm64 ? "yt-dlp_arm64.exe" : "yt-dlp.exe")
            : IsMac ? "yt-dlp_macos"
            : IsArm64 ? "yt-dlp_linux_aarch64" : "yt-dlp_linux";

        private static string DenoAsset =>
            "deno-" + (IsArm64 ? "aarch64" : "x86_64") + "-" +
            (IsWindows ? "pc-windows-msvc" : IsMac ? "apple-darwin" : "unknown-linux-gnu") + ".zip";

        /// <summary>The yt-dlp in use now, or null when there is none yet.</summary>
        internal static string? Locate()
        {
            var configured = Plugin.Instance.Configuration.YtDlpPath?.Trim();
            if (!string.IsNullOrEmpty(configured)) return File.Exists(configured) ? configured : null;
            return OnPath("yt-dlp") ?? (File.Exists(OwnYtDlp) ? OwnYtDlp : null);
        }

        internal static bool IsOwn(string path) => string.Equals(Path.GetFullPath(path), Path.GetFullPath(OwnYtDlp), StringComparison.OrdinalIgnoreCase);

        /// <summary>The yt-dlp to run, downloading it first when there is none.</summary>
        internal static async Task<string> EnsureAsync(CancellationToken ct)
        {
            var found = Locate();
            if (found != null) return found;
            if (!string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.YtDlpPath))
                throw new FileNotFoundException("yt-dlp not found at " + Plugin.Instance.Configuration.YtDlpPath);
            await InstallLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (File.Exists(OwnYtDlp)) return OwnYtDlp;
                var url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/" + YtDlpAsset;
                Log.Info("Downloading " + url);
                await DownloadAsync(url, OwnYtDlp, ct).ConfigureAwait(false);
                await MakeExecutableAsync(OwnYtDlp).ConfigureAwait(false);
                return OwnYtDlp;
            }
            finally { InstallLock.Release(); }
        }

        /// <summary>Arguments that point yt-dlp at a JavaScript runtime (YouTube needs one for most formats).</summary>
        internal static async Task<string> JsRuntimeArgsAsync(CancellationToken ct)
        {
            var rt = await EnsureJsRuntimeAsync(ct).ConfigureAwait(false);
            return rt == null ? "" : "--js-runtimes " + Quote(rt.Value.Name + ":" + rt.Value.Path) + " ";
        }

        /// <summary>deno or node already there (plugin folder, PATH), else deno downloaded into the plugin folder.</summary>
        internal static (string Name, string Path)? LocateJsRuntime()
        {
            if (File.Exists(OwnDeno)) return ("deno", OwnDeno);
            var deno = OnPath("deno");
            if (deno != null) return ("deno", deno);
            var node = OnPath("node");
            if (node == null && IsWindows)
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var guess = Path.Combine(pf, "nodejs", "node.exe");
                if (File.Exists(guess)) node = guess;
            }
            return node != null ? ("node", node) : ((string, string)?)null;
        }

        private static async Task<(string Name, string Path)?> EnsureJsRuntimeAsync(CancellationToken ct)
        {
            var found = LocateJsRuntime();
            if (found != null) return found;
            await InstallLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (File.Exists(OwnDeno)) return ("deno", OwnDeno);
                var url = "https://github.com/denoland/deno/releases/latest/download/" + DenoAsset;
                Log.Info("Downloading " + url);
                var zip = OwnDeno + ".zip";
                await DownloadAsync(url, zip, ct).ConfigureAwait(false);
                try
                {
                    using (var archive = ZipFile.OpenRead(zip))
                    {
                        var entry = archive.Entries.FirstOrDefault(e => e.Name == Path.GetFileName(OwnDeno))
                            ?? throw new InvalidDataException("No deno binary in " + DenoAsset);
                        var tmp = OwnDeno + ".part";
                        entry.ExtractToFile(tmp, true);
                        if (File.Exists(OwnDeno)) File.Delete(OwnDeno);
                        File.Move(tmp, OwnDeno);
                    }
                }
                finally { TryDelete(zip); }
                await MakeExecutableAsync(OwnDeno).ConfigureAwait(false);
                return ("deno", OwnDeno);
            }
            catch (Exception ex)
            {
                Log.Warn("No JavaScript runtime for yt-dlp: " + ex.Message);
                return null;
            }
            finally { InstallLock.Release(); }
        }

        internal static async Task<string> VersionAsync(CancellationToken ct)
        {
            var path = Locate();
            if (path == null) return "";
            var r = await RunAsync(path, "--version", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return r.Exit == 0 ? r.Out.Trim() : "";
        }

        /// <summary>Updates the plugin's own yt-dlp (one installed elsewhere is left to whoever installed it).</summary>
        internal static async Task<string> UpdateAsync(CancellationToken ct)
        {
            var path = await EnsureAsync(ct).ConfigureAwait(false);
            if (!IsOwn(path)) return "yt-dlp at " + path + " is not the plugin's own: not updated.";
            var before = await VersionAsync(ct).ConfigureAwait(false);
            var r = await RunAsync(path, "-U", TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            if (r.Exit != 0)
            {
                // -U can fail (e.g. a locked file): fetch the release again instead.
                Log.Warn("yt-dlp -U failed (" + r.Exit + "): " + r.Err.Trim() + ". Downloading again.");
                await InstallLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await DownloadAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/" + YtDlpAsset, OwnYtDlp, ct).ConfigureAwait(false);
                    await MakeExecutableAsync(OwnYtDlp).ConfigureAwait(false);
                }
                finally { InstallLock.Release(); }
            }
            var after = await VersionAsync(ct).ConfigureAwait(false);
            return before == after ? "yt-dlp " + after + " is up to date." : "yt-dlp updated from " + before + " to " + after + ".";
        }

        private static async Task DownloadAsync(string url, string target, CancellationToken ct)
        {
            var tmp = target + ".download";
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst, 81920, ct).ConfigureAwait(false);
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
        }

        private static async Task MakeExecutableAsync(string path)
        {
            if (IsWindows) return;
            await RunAsync("chmod", "+x " + Quote(path), TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }

        private static string? OnPath(string name)
        {
            var names = IsWindows ? new[] { name + ".exe", name } : new[] { name };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var n in names)
                {
                    try
                    {
                        var p = Path.Combine(dir.Trim().Trim('"'), n);
                        if (File.Exists(p)) return p;
                    }
                    catch (ArgumentException) { }
                }
            }
            return null;
        }

        internal static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        internal struct RunResult
        {
            public int Exit;
            public string Out;
            public string Err;
        }

        /// <summary>Runs a program without a shell and returns its output; killed after the timeout.</summary>
        internal static async Task<RunResult> RunAsync(string file, string args, TimeSpan timeout, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var done = new TaskCompletionSource<bool>();
                p.Exited += (s, e) => done.TrySetResult(true);
                p.Start();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(timeout);
                    using (cts.Token.Register(() => done.TrySetResult(false)))
                    {
                        if (!await done.Task.ConfigureAwait(false))
                        {
                            try { p.Kill(); } catch { }
                            throw new TimeoutException(Path.GetFileName(file) + " " + args + " did not finish in " + timeout.TotalSeconds + "s");
                        }
                    }
                }
                p.WaitForExit();
                return new RunResult { Exit = p.ExitCode, Out = await outTask.ConfigureAwait(false), Err = await errTask.ConfigureAwait(false) };
            }
        }
    }
}
