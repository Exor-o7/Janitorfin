using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Janitorfin.Plugin.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Services;

public sealed class CleanupExecutionService
{
    public const int DefaultExecutionResultDetailLimit = 100;

    private readonly CleanupEvaluationService _cleanupEvaluationService;
    private readonly ILibraryManager _libraryManager;
    private readonly PendingDeletionQueueService _pendingDeletionQueueService;
    private readonly IRadarrClient _radarrClient;
    private readonly ISonarrClient _sonarrClient;
    private readonly ILogger<CleanupExecutionService> _logger;

    public CleanupExecutionService(
        CleanupEvaluationService cleanupEvaluationService,
        ILibraryManager libraryManager,
        PendingDeletionQueueService pendingDeletionQueueService,
        IRadarrClient radarrClient,
        ISonarrClient sonarrClient,
        ILogger<CleanupExecutionService> logger)
    {
        _cleanupEvaluationService = cleanupEvaluationService;
        _libraryManager = libraryManager;
        _pendingDeletionQueueService = pendingDeletionQueueService;
        _radarrClient = radarrClient;
        _sonarrClient = sonarrClient;
        _logger = logger;
    }

    public async Task<CleanupExecutionSummary> ExecuteAsync(PluginConfiguration configuration, bool? dryRunOverride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var evaluation = await _cleanupEvaluationService.EvaluateAsync(configuration, cancellationToken).ConfigureAwait(false);
        var dryRun = dryRunOverride ?? configuration.DryRun;
        var now = DateTime.UtcNow;
        var results = new List<CleanupExecutionResult>();
        var resultCount = 0;
        var deletedCount = 0;
        var failedCount = 0;
        var queuedCount = 0;
        var pendingCount = 0;
        var radarrUpdatedCount = 0;
        var sonarrUpdatedCount = 0;

        if (!dryRun && configuration.EnablePendingDeletion)
        {
            _pendingDeletionQueueService.ReconcileAndQueueEligibleCandidates(configuration, evaluation.Candidates, now);
        }

        var pendingEntriesById = configuration.EnablePendingDeletion
            ? _pendingDeletionQueueService.GetEntriesByItemId()
            : new Dictionary<Guid, PendingDeletionEntry>();

        foreach (var candidate in evaluation.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            pendingEntriesById.TryGetValue(candidate.ItemId, out var pendingEntry);

            if (dryRun)
            {
                var dryRunOutcome = configuration.EnablePendingDeletion
                    ? pendingEntry is null
                        ? "Would be queued for staged deletion"
                        : pendingEntry.DeleteAfterUtc <= now
                            ? "Pending grace period elapsed; ready for deletion"
                            : "Pending grace period"
                    : "Dry run candidate";

                AddResult(results, new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidate.ItemName,
                    ItemType = candidate.ItemType,
                    Outcome = dryRunOutcome,
                    PendingDeleteAfterUtc = pendingEntry?.DeleteAfterUtc,
                });
                resultCount++;
                continue;
            }

            if (configuration.EnablePendingDeletion)
            {
                if (pendingEntry is null)
                {
                    queuedCount++;
                    AddResult(results, new CleanupExecutionResult
                    {
                        ItemId = candidate.ItemId,
                        ItemName = candidate.ItemName,
                        ItemType = candidate.ItemType,
                        Outcome = "Queued for staged deletion",
                    });
                    resultCount++;
                    continue;
                }

                if (pendingEntry.DeleteAfterUtc > now)
                {
                    pendingCount++;
                    AddResult(results, new CleanupExecutionResult
                    {
                        ItemId = candidate.ItemId,
                        ItemName = candidate.ItemName,
                        ItemType = candidate.ItemType,
                        Outcome = "Pending grace period",
                        PendingDeleteAfterUtc = pendingEntry.DeleteAfterUtc,
                    });
                    resultCount++;
                    continue;
                }
            }

            var item = _libraryManager.GetItemById(candidate.ItemId);
            if (item is null)
            {
                failedCount++;
                _pendingDeletionQueueService.RemoveEntry(candidate.ItemId);
                AddResult(results, new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidate.ItemName,
                    ItemType = candidate.ItemType,
                    Outcome = "Skipped",
                    Error = "Item no longer exists in Jellyfin.",
                });
                resultCount++;
                continue;
            }

            var radarrUpdated = false;
            var sonarrUpdated = false;

            try
            {
                if (configuration.EnableRadarrIntegration
                    && configuration.UnmonitorRadarrOnDelete
                    && string.Equals(candidate.ItemType, "Movie", StringComparison.OrdinalIgnoreCase))
                {
                    var radarrResult = await _radarrClient.UnmonitorMovieAsync(candidate, configuration, cancellationToken).ConfigureAwait(false);
                    if (!radarrResult.Success)
                    {
                        failedCount++;
                        AddResult(results, new CleanupExecutionResult
                        {
                            ItemId = candidate.ItemId,
                            ItemName = candidate.ItemName,
                            ItemType = candidate.ItemType,
                            Outcome = "Skipped",
                            Error = radarrResult.Message,
                        });
                        resultCount++;
                        continue;
                    }

                    radarrUpdated = true;
                    radarrUpdatedCount++;
                }

                if (configuration.EnableSonarrIntegration
                    && configuration.UnmonitorSonarrOnDelete
                    && string.Equals(candidate.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
                {
                    var sonarrResult = await _sonarrClient.ApplyMonitoringAsync(candidate, configuration, cancellationToken).ConfigureAwait(false);
                    if (!sonarrResult.Success)
                    {
                        failedCount++;
                        AddResult(results, new CleanupExecutionResult
                        {
                            ItemId = candidate.ItemId,
                            ItemName = candidate.ItemName,
                            ItemType = candidate.ItemType,
                            Outcome = "Skipped",
                            Error = sonarrResult.Message,
                        });
                        resultCount++;
                        continue;
                    }

                    sonarrUpdated = true;
                    sonarrUpdatedCount++;
                }

                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
                _pendingDeletionQueueService.RemoveEntry(candidate.ItemId);
                deletedCount++;
                AddResult(results, new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidate.ItemName,
                    ItemType = candidate.ItemType,
                    Deleted = true,
                    RadarrUpdated = radarrUpdated,
                    SonarrUpdated = sonarrUpdated,
                    Outcome = "Deleted",
                });
                resultCount++;
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(ex, "Error processing cleanup candidate {ItemName} ({ItemId})", candidate.ItemName, candidate.ItemId);
                AddResult(results, new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidate.ItemName,
                    ItemType = candidate.ItemType,
                    RadarrUpdated = radarrUpdated,
                    SonarrUpdated = sonarrUpdated,
                    Outcome = "Failed",
                    Error = ex.Message,
                });
                resultCount++;
            }
        }

        return new CleanupExecutionSummary
        {
            DryRun = dryRun,
            ExecutedAtUtc = DateTime.UtcNow,
            ScannedItemCount = evaluation.ScannedItemCount,
            CandidateCount = evaluation.CandidateCount,
            DeletedCount = deletedCount,
            FailedCount = failedCount,
            QueuedCount = queuedCount,
            PendingCount = pendingCount,
            RadarrUpdatedCount = radarrUpdatedCount,
            SonarrUpdatedCount = sonarrUpdatedCount,
            ResultCount = resultCount,
            ResultDetailLimit = DefaultExecutionResultDetailLimit,
            Results = results,
        };
    }

    private static void AddResult(ICollection<CleanupExecutionResult> results, CleanupExecutionResult result)
    {
        if (results.Count >= DefaultExecutionResultDetailLimit)
        {
            return;
        }

        results.Add(result);
    }
}