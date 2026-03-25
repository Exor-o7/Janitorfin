using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Janitorfin.Plugin.Services;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Tasks;

public class LibraryMaintenanceTask : IScheduledTask
{
    private readonly ILogger _logger;
    private readonly ILocalizationManager _localizationManager;
    private readonly CleanupExecutionService _cleanupExecutionService;

    public LibraryMaintenanceTask(
        ILoggerFactory loggerFactory,
        ILocalizationManager localizationManager,
        CleanupExecutionService cleanupExecutionService)
    {
        _logger = loggerFactory.CreateLogger<LibraryMaintenanceTask>();
        _localizationManager = localizationManager;
        _cleanupExecutionService = cleanupExecutionService;
    }

    public string Name => "Janitorfin Cleanup";

    public string Description => "Evaluates stale Jellyfin media and prepares cleanup actions for Radarr and Sonarr integrations.";

    public string Key => "JanitorfinCleanup";

    public string Category => _localizationManager.GetLocalizedString("TasksMaintenanceCategory");

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(1).Ticks,
        },
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;

        if (configuration is null)
        {
            _logger.LogWarning("Janitorfin configuration was unavailable. Cleanup task did not run.");
            progress.Report(100);
            return;
        }

        var summary = await _cleanupExecutionService.ExecuteAsync(configuration, null, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Janitorfin cleanup finished. DryRun={DryRun}, Scanned={Scanned}, Candidates={Candidates}, Deleted={Deleted}, Failed={Failed}, RadarrUpdated={RadarrUpdated}, SonarrUpdated={SonarrUpdated}",
            summary.DryRun,
            summary.ScannedItemCount,
            summary.CandidateCount,
            summary.DeletedCount,
            summary.FailedCount,
            summary.RadarrUpdatedCount,
            summary.SonarrUpdatedCount);

        progress.Report(100);
    }
}