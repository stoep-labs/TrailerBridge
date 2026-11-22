using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TrailerBridge
{
    /// <summary>Creates and removes the link files (daily and at startup unless changed under Scheduled Tasks).</summary>
    public class UpdateLinksTask : IScheduledTask
    {
        public UpdateLinksTask(ILogManager logManager, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor,
            IServerApplicationHost host, IJsonSerializer json)
        {
            Log.Initialize(logManager);
            Links.Initialize(libraryManager, libraryMonitor, host, json);
        }

        public string Key => "TrailerBridgeUpdateLinks";
        public string Name => "TrailerBridge: update trailer links";
        public string Description => "Adds a trailer link for every movie and series with a YouTube trailer and no trailer of its own, and removes the ones no longer needed.";
        public string Category => "TrailerBridge";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(24).Ticks },
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup }
            };
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress.Report(0);
            Links.Update(cancellationToken);
            progress.Report(100);
            return Task.CompletedTask;
        }
    }

    /// <summary>Keeps the plugin's own yt-dlp current (YouTube changes often break older versions).</summary>
    public class UpdateYtDlpTask : IScheduledTask
    {
        public UpdateYtDlpTask(ILogManager logManager)
        {
            Log.Initialize(logManager);
        }

        public string Key => "TrailerBridgeUpdateYtDlp";
        public string Name => "TrailerBridge: update yt-dlp";
        public string Description => "Downloads yt-dlp when missing and updates the plugin's own copy.";
        public string Category => "TrailerBridge";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[] { new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromDays(7).Ticks } };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress.Report(0);
            Log.Info(await YtDlp.UpdateAsync(cancellationToken).ConfigureAwait(false));
            await YtDlp.JsRuntimeArgsAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
        }
    }
}
