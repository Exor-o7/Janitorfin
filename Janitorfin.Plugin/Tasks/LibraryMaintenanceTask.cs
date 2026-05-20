using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Janitorfin.Plugin.Services;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Tasks;

public class ScanPendingDeletionTask : IScheduledTask
{
    private readonly ILogger _logger;
    private readonly ILocalizationManager _localizationManager;
    private readonly CleanupExecutionService _cleanupExecutionService;

    public ScanPendingDeletionTask(
        ILoggerFactory loggerFactory,
        ILocalizationManager localizationManager,
        CleanupExecutionService cleanupExecutionService)
    {
        _logger = loggerFactory.CreateLogger<ScanPendingDeletionTask>();
        _localizationManager = localizationManager;
        _cleanupExecutionService = cleanupExecutionService;
    }

    public string Name => "Janitorfin Update Pending Media";

    public string Description => "Scans Jellyfin media and updates Janitorfin's pending deletion list.";

    public string Key => "JanitorfinScanPending";

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

        var summary = await _cleanupExecutionService.ScanAndQueuePendingAsync(configuration, null, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Janitorfin pending deletion list update finished. DryRun={DryRun}, Scanned={Scanned}, Candidates={Candidates}, Queued={Queued}, Pending={Pending}",
            summary.DryRun,
            summary.ScannedItemCount,
            summary.CandidateCount,
            summary.QueuedCount,
            summary.PendingCount);

        progress.Report(100);
    }
}

public class DeleteDuePendingTask : IScheduledTask
{
    private readonly ILogger _logger;
    private readonly ILocalizationManager _localizationManager;
    private readonly CleanupExecutionService _cleanupExecutionService;

    public DeleteDuePendingTask(
        ILoggerFactory loggerFactory,
        ILocalizationManager localizationManager,
        CleanupExecutionService cleanupExecutionService)
    {
        _logger = loggerFactory.CreateLogger<DeleteDuePendingTask>();
        _localizationManager = localizationManager;
        _cleanupExecutionService = cleanupExecutionService;
    }

    public string Name => "Janitorfin Delete Overdue Media";

    public string Description => "Deletes overdue media from Janitorfin's pending deletion list after its grace period has elapsed.";

    public string Key => "JanitorfinDeleteDuePending";

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
            _logger.LogWarning("Janitorfin configuration was unavailable. Delete due pending task did not run.");
            progress.Report(100);
            return;
        }

        var summary = await _cleanupExecutionService.DeleteDuePendingAsync(configuration, null, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Janitorfin delete overdue pending media finished. DryRun={DryRun}, PendingChecked={PendingChecked}, Due={Due}, Deleted={Deleted}, Failed={Failed}, RadarrUpdated={RadarrUpdated}, SonarrUpdated={SonarrUpdated}",
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