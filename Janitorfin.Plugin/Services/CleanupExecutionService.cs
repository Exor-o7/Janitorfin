using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Janitorfin.Plugin.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Services;

public sealed class CleanupExecutionService
{
    public const int DefaultExecutionResultDetailLimit = 100;

    private readonly CleanupEvaluationService _cleanupEvaluationService;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ITaskManager _taskManager;
    private readonly PendingDeletionQueueService _pendingDeletionQueueService;
    private readonly PendingDeletionReviewCollectionService _pendingDeletionReviewCollectionService;
    private readonly IDiscordNotificationService _discordNotificationService;
    private readonly IJanitorfinActivityLogService _activityLogService;
    private readonly IRadarrClient _radarrClient;
    private readonly ISonarrClient _sonarrClient;
    private readonly ILogger<CleanupExecutionService> _logger;

    public CleanupExecutionService(
        CleanupEvaluationService cleanupEvaluationService,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ITaskManager taskManager,
        PendingDeletionQueueService pendingDeletionQueueService,
        PendingDeletionReviewCollectionService pendingDeletionReviewCollectionService,
        IDiscordNotificationService discordNotificationService,
        IJanitorfinActivityLogService activityLogService,
        IRadarrClient radarrClient,
        ISonarrClient sonarrClient,
        ILogger<CleanupExecutionService> logger)
    {
        _cleanupEvaluationService = cleanupEvaluationService;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _taskManager = taskManager;
        _pendingDeletionQueueService = pendingDeletionQueueService;
        _pendingDeletionReviewCollectionService = pendingDeletionReviewCollectionService;
        _discordNotificationService = discordNotificationService;
        _activityLogService = activityLogService;
        _radarrClient = radarrClient;
        _sonarrClient = sonarrClient;
        _logger = logger;
    }

    public async Task<CleanupExecutionSummary> ExecuteAsync(PluginConfiguration configuration, bool? dryRunOverride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        HomeScreenSectionsIntegrationBootstrap.Refresh(configuration);

        await WaitForLibraryTasksAsync(cancellationToken).ConfigureAwait(false);

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
        var deletedCandidates = new List<CleanupCandidate>();
        var successfulSonarrActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedSonarrActions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var existingPendingIds = configuration.EnablePendingDeletion
            ? _pendingDeletionQueueService.GetEntriesByItemId().Keys.ToHashSet()
            : [];

        if (!dryRun && configuration.EnablePendingDeletion)
        {
            _pendingDeletionQueueService.ReconcileAndQueueEligibleCandidates(configuration, evaluation.Candidates, now);
        }

        var pendingEntriesById = configuration.EnablePendingDeletion
            ? _pendingDeletionQueueService.GetEntriesByItemId()
            : new Dictionary<Guid, PendingDeletionEntry>();

        if (!dryRun && configuration.EnablePendingDeletion)
        {
            var queuedCandidates = evaluation.Candidates
                .Where(candidate =>
                    !existingPendingIds.Contains(candidate.ItemId)
                    && pendingEntriesById.TryGetValue(candidate.ItemId, out var pendingEntry)
                    && pendingEntry.DeleteAfterUtc > now)
                .ToArray();

            await _activityLogService.LogPendingDeletionQueuedAsync(configuration, queuedCandidates).ConfigureAwait(false);
        }

        foreach (var candidate in evaluation.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidateDisplayName = GetCandidateDisplayName(candidate);
            _logger.LogDebug("Processing cleanup candidate {ItemName} ({ItemType}, {ItemId}).", candidateDisplayName, candidate.ItemType, candidate.ItemId);
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
                    ItemName = candidateDisplayName,
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
                        ItemName = candidateDisplayName,
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
                        ItemName = candidateDisplayName,
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
                    ItemName = candidateDisplayName,
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
                    _logger.LogDebug("Applying Radarr unmonitor before deleting {ItemName} ({ItemId}).", candidateDisplayName, candidate.ItemId);
                    var radarrResult = await _radarrClient.UnmonitorMovieAsync(candidate, configuration, cancellationToken).ConfigureAwait(false);
                    if (!radarrResult.Success)
                    {
                        _logger.LogDebug("Radarr unmonitor failed for {ItemName} ({ItemId}): {Message}", candidateDisplayName, candidate.ItemId, radarrResult.Message);
                        failedCount++;
                        AddResult(results, new CleanupExecutionResult
                        {
                            ItemId = candidate.ItemId,
                            ItemName = candidateDisplayName,
                            ItemType = candidate.ItemType,
                            Outcome = "Skipped",
                            Error = radarrResult.Message,
                        });
                        resultCount++;
                        continue;
                    }

                    radarrUpdated = true;
                    radarrUpdatedCount++;
                    _logger.LogDebug("Radarr unmonitor succeeded for {ItemName} ({ItemId}).", candidateDisplayName, candidate.ItemId);
                }

                if (configuration.EnableSonarrIntegration
                    && configuration.UnmonitorSonarrOnDelete
                    && string.Equals(candidate.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
                {
                    var sonarrActionKey = GetSonarrActionKey(candidate, configuration.SonarrUnmonitorScope);
                    if (successfulSonarrActions.Contains(sonarrActionKey))
                    {
                        sonarrUpdated = true;
                        _logger.LogDebug(
                            "Reusing successful Sonarr action {ActionKey} for {ItemName} ({ItemId}).",
                            sonarrActionKey,
                            candidateDisplayName,
                            candidate.ItemId);
                    }
                    else if (failedSonarrActions.TryGetValue(sonarrActionKey, out var previousSonarrError))
                    {
                        _logger.LogDebug(
                            "Skipping {ItemName} ({ItemId}) because Sonarr action {ActionKey} already failed: {Message}",
                            candidateDisplayName,
                            candidate.ItemId,
                            sonarrActionKey,
                            previousSonarrError);
                        failedCount++;
                        AddResult(results, new CleanupExecutionResult
                        {
                            ItemId = candidate.ItemId,
                            ItemName = candidateDisplayName,
                            ItemType = candidate.ItemType,
                            Outcome = "Skipped",
                            Error = previousSonarrError,
                        });
                        resultCount++;
                        continue;
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Applying Sonarr unmonitor action {ActionKey} before deleting {ItemName} ({ItemId}).",
                            sonarrActionKey,
                            candidateDisplayName,
                            candidate.ItemId);
                        var sonarrResult = await _sonarrClient.ApplyMonitoringAsync(candidate, configuration, cancellationToken).ConfigureAwait(false);
                        if (!sonarrResult.Success)
                        {
                            _logger.LogDebug(
                                "Sonarr unmonitor action {ActionKey} failed for {ItemName} ({ItemId}): {Message}",
                                sonarrActionKey,
                                candidateDisplayName,
                                candidate.ItemId,
                                sonarrResult.Message);
                            failedSonarrActions[sonarrActionKey] = sonarrResult.Message;
                            failedCount++;
                            AddResult(results, new CleanupExecutionResult
                            {
                                ItemId = candidate.ItemId,
                                ItemName = candidateDisplayName,
                                ItemType = candidate.ItemType,
                                Outcome = "Skipped",
                                Error = sonarrResult.Message,
                            });
                            resultCount++;
                            continue;
                        }

                        successfulSonarrActions.Add(sonarrActionKey);
                        sonarrUpdated = true;
                        sonarrUpdatedCount++;
                        _logger.LogDebug(
                            "Sonarr unmonitor action {ActionKey} succeeded for {ItemName} ({ItemId}).",
                            sonarrActionKey,
                            candidateDisplayName,
                            candidate.ItemId);
                    }
                }

                _logger.LogDebug("Deleting {ItemName} ({ItemType}, {ItemId}) through Jellyfin library manager.", candidateDisplayName, candidate.ItemType, candidate.ItemId);
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
                _pendingDeletionQueueService.RemoveEntry(candidate.ItemId);
                deletedCount++;
                deletedCandidates.Add(candidate);
                _logger.LogDebug("Deleted {ItemName} ({ItemType}, {ItemId}).", candidateDisplayName, candidate.ItemType, candidate.ItemId);
                AddResult(results, new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidateDisplayName,
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
                _logger.LogError(
                    ex,
                    "Error processing cleanup candidate {ItemName} ({ItemType}, {ItemId})",
                    candidateDisplayName,
                    candidate.ItemType,
                    candidate.ItemId);
                AddResult(results, new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidateDisplayName,
                    ItemType = candidate.ItemType,
                    RadarrUpdated = radarrUpdated,
                    SonarrUpdated = sonarrUpdated,
                    Outcome = "Failed",
                    Error = ex.Message,
                });
                resultCount++;
            }
        }

        if (!dryRun)
        {
            await _activityLogService.LogDeletedAsync(configuration, deletedCandidates).ConfigureAwait(false);

            try
            {
                await _pendingDeletionReviewCollectionService.SyncAsync(configuration, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Janitorfin review collection sync failed.");
            }
        }

        if (!dryRun && configuration.EnablePendingDeletion)
        {
            await NotifyDiscordGracePeriodItemsAsync(configuration, now, cancellationToken).ConfigureAwait(false);
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

    public async Task<CleanupExecutionSummary> ScanAndQueuePendingAsync(PluginConfiguration configuration, bool? dryRunOverride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        HomeScreenSectionsIntegrationBootstrap.Refresh(configuration);

        await WaitForLibraryTasksAsync(cancellationToken).ConfigureAwait(false);

        var evaluation = await _cleanupEvaluationService.EvaluateAsync(configuration, cancellationToken).ConfigureAwait(false);
        var dryRun = dryRunOverride ?? configuration.DryRun;
        var now = DateTime.UtcNow;
        var results = new List<CleanupExecutionResult>();
        var resultCount = 0;
        var queuedCount = 0;
        var pendingCount = 0;
        var existingPendingIds = configuration.EnablePendingDeletion
            ? _pendingDeletionQueueService.GetEntriesByItemId().Keys.ToHashSet()
            : [];

        if (!dryRun && configuration.EnablePendingDeletion)
        {
            _pendingDeletionQueueService.ReconcileAndQueueEligibleCandidates(configuration, evaluation.Candidates, now);
        }

        var pendingEntriesById = configuration.EnablePendingDeletion
            ? _pendingDeletionQueueService.GetEntriesByItemId()
            : new Dictionary<Guid, PendingDeletionEntry>();

        if (!dryRun && configuration.EnablePendingDeletion)
        {
            var queuedCandidates = evaluation.Candidates
                .Where(candidate =>
                    !existingPendingIds.Contains(candidate.ItemId)
                    && pendingEntriesById.TryGetValue(candidate.ItemId, out var pendingEntry)
                    && pendingEntry.DeleteAfterUtc > now)
                .ToArray();

            await _activityLogService.LogPendingDeletionQueuedAsync(configuration, queuedCandidates).ConfigureAwait(false);
        }

        foreach (var candidate in evaluation.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            pendingEntriesById.TryGetValue(candidate.ItemId, out var pendingEntry);
            var outcome = configuration.EnablePendingDeletion
                ? pendingEntry is null
                    ? dryRun ? "Would be queued for staged deletion" : "Queued for staged deletion"
                    : pendingEntry.DeleteAfterUtc <= now
                        ? "Pending grace period elapsed; ready for deletion"
                        : "Pending grace period"
                : "Pending deletion is disabled";

            if (configuration.EnablePendingDeletion)
            {
                if (pendingEntry is null)
                {
                    queuedCount++;
                }
                else
                {
                    pendingCount++;
                }
            }

            AddResult(results, new CleanupExecutionResult
            {
                ItemId = candidate.ItemId,
                ItemName = GetCandidateDisplayName(candidate),
                ItemType = candidate.ItemType,
                Outcome = outcome,
                PendingDeleteAfterUtc = pendingEntry?.DeleteAfterUtc,
            });
            resultCount++;
        }

        if (!dryRun)
        {
            try
            {
                await _pendingDeletionReviewCollectionService.SyncAsync(configuration, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Janitorfin review collection sync failed.");
            }
        }

        if (!dryRun && configuration.EnablePendingDeletion)
        {
            await NotifyDiscordGracePeriodItemsAsync(configuration, now, cancellationToken).ConfigureAwait(false);
        }

        return new CleanupExecutionSummary
        {
            DryRun = dryRun,
            ExecutedAtUtc = DateTime.UtcNow,
            ScannedItemCount = evaluation.ScannedItemCount,
            CandidateCount = evaluation.CandidateCount,
            QueuedCount = queuedCount,
            PendingCount = pendingCount,
            ResultCount = resultCount,
            ResultDetailLimit = DefaultExecutionResultDetailLimit,
            Results = results,
        };
    }

    public async Task<CleanupExecutionSummary> DeleteDuePendingAsync(PluginConfiguration configuration, bool? dryRunOverride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        HomeScreenSectionsIntegrationBootstrap.Refresh(configuration);

        await WaitForLibraryTasksAsync(cancellationToken).ConfigureAwait(false);

        var dryRun = dryRunOverride ?? configuration.DryRun;
        var now = DateTime.UtcNow;
        var pendingEntries = _pendingDeletionQueueService.GetEntriesByItemId().Values
            .OrderBy(entry => entry.DeleteAfterUtc)
            .ThenBy(entry => entry.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SeriesName ?? entry.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SeasonName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dueEntries = pendingEntries
            .Where(entry => entry.DeleteAfterUtc <= now)
            .ToArray();
        var results = new List<CleanupExecutionResult>();
        var resultCount = 0;
        var deletedCount = 0;
        var failedCount = 0;
        var pendingCount = pendingEntries.Length - dueEntries.Length;
        var radarrUpdatedCount = 0;
        var sonarrUpdatedCount = 0;
        var deletedCandidates = new List<CleanupCandidate>();
        var successfulSonarrActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedSonarrActions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in dueEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = _libraryManager.GetItemById(entry.ItemId);
            var candidate = CreateCandidateFromPendingEntry(entry, item);
            var candidateDisplayName = GetCandidateDisplayName(candidate);

            if (item is null)
            {
                if (!dryRun)
                {
                    _pendingDeletionQueueService.RemoveEntry(entry.ItemId);
                }

                AddResult(results, new CleanupExecutionResult
                {
                    ItemId = entry.ItemId,
                    ItemName = candidateDisplayName,
                    ItemType = entry.ItemType,
                    Outcome = dryRun ? "Would remove from pending" : "Removed from pending",
                    Error = "Item no longer exists in Jellyfin.",
                    PendingDeleteAfterUtc = entry.DeleteAfterUtc,
                });
                resultCount++;
                continue;
            }

            if (TryGetDeletionSafetyBlock(item, configuration, out var safetyReason))
            {
                if (!dryRun)
                {
                    _pendingDeletionQueueService.RemoveEntry(entry.ItemId);
                }

                AddResult(results, new CleanupExecutionResult
                {
                    ItemId = entry.ItemId,
                    ItemName = candidateDisplayName,
                    ItemType = entry.ItemType,
                    Outcome = dryRun ? "Would remove from pending" : "Removed from pending",
                    Error = safetyReason,
                    PendingDeleteAfterUtc = entry.DeleteAfterUtc,
                });
                resultCount++;
                continue;
            }

            if (dryRun)
            {
                AddResult(results, new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidateDisplayName,
                    ItemType = candidate.ItemType,
                    Outcome = "Due for deletion",
                    PendingDeleteAfterUtc = entry.DeleteAfterUtc,
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
                        _logger.LogWarning(
                            "Skipping deletion of due pending item {ItemName} ({ItemId}) because Radarr unmonitor failed: {Message}",
                            candidateDisplayName,
                            candidate.ItemId,
                            radarrResult.Message);
                        failedCount++;
                        AddResult(results, new CleanupExecutionResult
                        {
                            ItemId = candidate.ItemId,
                            ItemName = candidateDisplayName,
                            ItemType = candidate.ItemType,
                            Outcome = "Skipped",
                            Error = radarrResult.Message,
                            PendingDeleteAfterUtc = entry.DeleteAfterUtc,
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
                    var sonarrActionKey = GetSonarrActionKey(candidate, configuration.SonarrUnmonitorScope);
                    if (successfulSonarrActions.Contains(sonarrActionKey))
                    {
                        sonarrUpdated = true;
                    }
                    else if (failedSonarrActions.TryGetValue(sonarrActionKey, out var previousSonarrError))
                    {
                        failedCount++;
                        AddResult(results, new CleanupExecutionResult
                        {
                            ItemId = candidate.ItemId,
                            ItemName = candidateDisplayName,
                            ItemType = candidate.ItemType,
                            Outcome = "Skipped",
                            Error = previousSonarrError,
                            PendingDeleteAfterUtc = entry.DeleteAfterUtc,
                        });
                        resultCount++;
                        continue;
                    }
                    else
                    {
                        var sonarrResult = await _sonarrClient.ApplyMonitoringAsync(candidate, configuration, cancellationToken).ConfigureAwait(false);
                        if (!sonarrResult.Success)
                        {
                            _logger.LogWarning(
                                "Skipping deletion of due pending item {ItemName} ({ItemId}) because Sonarr action {ActionKey} failed: {Message}",
                                candidateDisplayName,
                                candidate.ItemId,
                                sonarrActionKey,
                                sonarrResult.Message);
                            failedSonarrActions[sonarrActionKey] = sonarrResult.Message;
                            failedCount++;
                            AddResult(results, new CleanupExecutionResult
                            {
                                ItemId = candidate.ItemId,
                                ItemName = candidateDisplayName,
                                ItemType = candidate.ItemType,
                                Outcome = "Skipped",
                                Error = sonarrResult.Message,
                                PendingDeleteAfterUtc = entry.DeleteAfterUtc,
                            });
                            resultCount++;
                            continue;
                        }

                        successfulSonarrActions.Add(sonarrActionKey);
                        sonarrUpdated = true;
                        sonarrUpdatedCount++;
                    }
                }

                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
                _pendingDeletionQueueService.RemoveEntry(candidate.ItemId);
                deletedCount++;
                deletedCandidates.Add(candidate);
                AddResult(results, new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidateDisplayName,
                    ItemType = candidate.ItemType,
                    Deleted = true,
                    RadarrUpdated = radarrUpdated,
                    SonarrUpdated = sonarrUpdated,
                    Outcome = "Deleted",
                    PendingDeleteAfterUtc = entry.DeleteAfterUtc,
                });
                resultCount++;
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(
                    ex,
                    "Error deleting due pending item {ItemName} ({ItemType}, {ItemId})",
                    candidateDisplayName,
                    candidate.ItemType,
                    candidate.ItemId);
                AddResult(results, new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidateDisplayName,
                    ItemType = candidate.ItemType,
                    RadarrUpdated = radarrUpdated,
                    SonarrUpdated = sonarrUpdated,
                    Outcome = "Failed",
                    Error = ex.Message,
                    PendingDeleteAfterUtc = entry.DeleteAfterUtc,
                });
                resultCount++;
            }
        }

        if (!dryRun)
        {
            await _activityLogService.LogDeletedAsync(configuration, deletedCandidates).ConfigureAwait(false);

            try
            {
                await _pendingDeletionReviewCollectionService.SyncAsync(configuration, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Janitorfin review collection sync failed.");
            }
        }

        return new CleanupExecutionSummary
        {
            DryRun = dryRun,
            ExecutedAtUtc = DateTime.UtcNow,
            ScannedItemCount = pendingEntries.Length,
            CandidateCount = dueEntries.Length,
            DeletedCount = deletedCount,
            FailedCount = failedCount,
            PendingCount = pendingCount,
            RadarrUpdatedCount = radarrUpdatedCount,
            SonarrUpdatedCount = sonarrUpdatedCount,
            ResultCount = resultCount,
            ResultDetailLimit = DefaultExecutionResultDetailLimit,
            Results = results,
        };
    }

    private async Task WaitForLibraryTasksAsync(CancellationToken cancellationToken)
    {
        string? lastLoggedTaskName = null;

        while (TryGetRunningLibraryTaskName(out var runningLibraryTaskName))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(lastLoggedTaskName, runningLibraryTaskName, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Janitorfin cleanup is waiting for Jellyfin library task to finish: {TaskName}",
                    runningLibraryTaskName);
                lastLoggedTaskName = runningLibraryTaskName;
            }

            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(lastLoggedTaskName))
        {
            _logger.LogInformation("Jellyfin library tasks are idle. Janitorfin cleanup is starting.");
        }
    }

    private bool TryGetRunningLibraryTaskName(out string taskName)
    {
        var runningTask = _taskManager.ScheduledTasks.FirstOrDefault(task =>
            task.State is TaskState.Running or TaskState.Cancelling
            && !IsJanitorfinTask(task)
            && IsLibraryTask(task));

        taskName = runningTask?.Name ?? string.Empty;
        return runningTask is not null;
    }

    private static bool IsLibraryTask(IScheduledTaskWorker task)
    {
        return ContainsLibraryTaskKeyword(task.Name)
            || ContainsLibraryTaskKeyword(task.ScheduledTask.Name)
            || ContainsLibraryTaskKeyword(task.ScheduledTask.Description)
            || ContainsLibraryTaskKeyword(task.ScheduledTask.Key)
            || ContainsLibraryTaskKeyword(task.ScheduledTask.Category);
    }

    private static bool ContainsLibraryTaskKeyword(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (value.Contains("library", StringComparison.OrdinalIgnoreCase)
                || value.Contains("scan", StringComparison.OrdinalIgnoreCase)
                || value.Contains("refresh", StringComparison.OrdinalIgnoreCase)
                || value.Contains("metadata", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsJanitorfinTask(IScheduledTaskWorker task)
    {
        return IsJanitorfinTaskValue(task.Name)
            || IsJanitorfinTaskValue(task.ScheduledTask.Name)
            || IsJanitorfinTaskValue(task.ScheduledTask.Key);
    }

    private static bool IsJanitorfinTaskValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains("Janitorfin", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSonarrActionKey(CleanupCandidate candidate, SonarrUnmonitorScope scope)
    {
        var seriesKey = candidate.SeriesTvdbId
            ?? candidate.SeriesTmdbId
            ?? candidate.SeriesImdbId
            ?? candidate.SeriesPath
            ?? candidate.SeriesName
            ?? candidate.ItemName;

        return scope switch
        {
            SonarrUnmonitorScope.Series => "series:" + seriesKey,
            _ => "season:" + seriesKey + ":" + (candidate.SeasonNumber?.ToString() ?? "unknown"),
        };
    }

    private CleanupCandidate CreateCandidateFromPendingEntry(PendingDeletionEntry entry, BaseItem? item)
    {
        return new CleanupCandidate
        {
            ItemId = entry.ItemId,
            ItemName = item?.Name ?? entry.ItemName,
            ItemType = item is null ? entry.ItemType : GetItemType(item),
            Path = item?.Path ?? entry.Path,
            LibraryName = entry.LibraryName,
            SeriesName = item is Episode episode ? episode.Series?.Name : entry.SeriesName,
            SeasonName = item is Episode episodeForSeason ? episodeForSeason.Season?.Name : entry.SeasonName,
            ProductionYear = item is Episode episodeForYear ? episodeForYear.Series?.ProductionYear : item?.ProductionYear ?? entry.ProductionYear,
            SeasonNumber = item is Episode episodeForNumbers ? episodeForNumbers.ParentIndexNumber : entry.SeasonNumber,
            EpisodeNumber = item is Episode episodeForEpisodeNumber ? episodeForEpisodeNumber.IndexNumber : entry.EpisodeNumber,
            SeriesPath = item is Episode episodeForPath ? episodeForPath.Series?.Path : null,
            Reason = entry.Reason,
            AppliedRuleName = entry.AppliedRuleName,
            DateAddedUtc = entry.DateAddedUtc,
            TmdbId = item?.GetProviderId(MetadataProvider.Tmdb),
            TvdbId = item?.GetProviderId(MetadataProvider.Tvdb),
            ImdbId = item?.GetProviderId(MetadataProvider.Imdb),
            SeriesTmdbId = item is Episode episodeForTmdb ? episodeForTmdb.Series?.GetProviderId(MetadataProvider.Tmdb) : null,
            SeriesTvdbId = item is Episode episodeForTvdb ? episodeForTvdb.Series?.GetProviderId(MetadataProvider.Tvdb) : null,
            SeriesImdbId = item is Episode episodeForImdb ? episodeForImdb.Series?.GetProviderId(MetadataProvider.Imdb) : null,
            IsPendingDeletion = true,
            PendingSinceUtc = entry.FirstQualifiedUtc,
            PendingDeleteAfterUtc = entry.DeleteAfterUtc,
        };
    }

    private bool TryGetDeletionSafetyBlock(BaseItem item, PluginConfiguration configuration, out string reason)
    {
        if (!string.IsNullOrWhiteSpace(configuration.ProtectedTag)
            && item.Tags is not null
            && item.Tags.Any(tag => string.Equals(tag, configuration.ProtectedTag, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Item now has the protected tag, so it was removed from the pending list.";
            return true;
        }

        if (configuration.KeepFavorites)
        {
            foreach (var user in _userManager.Users)
            {
                var userData = _userDataManager.GetUserData(user, item);
                if (userData?.IsFavorite == true)
                {
                    reason = "Item is now favorited, so it was removed from the pending list.";
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }

    private static string GetItemType(BaseItem item)
    {
        return item switch
        {
            Movie => "Movie",
            Episode => "Episode",
            _ => "Video",
        };
    }

    private static string GetCandidateDisplayName(CleanupCandidate candidate)
    {
        if (!string.Equals(candidate.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
        {
            return candidate.ItemName;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(candidate.SeriesName))
        {
            parts.Add(candidate.SeriesName!);
        }

        if (candidate.SeasonNumber.HasValue)
        {
            parts.Add("Season " + candidate.SeasonNumber.Value);
        }
        else if (!string.IsNullOrWhiteSpace(candidate.SeasonName))
        {
            parts.Add(candidate.SeasonName!);
        }

        parts.Add(candidate.ItemName);
        return string.Join(" - ", parts);
    }

    private async Task NotifyDiscordGracePeriodItemsAsync(PluginConfiguration configuration, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var pendingEntries = _pendingDeletionQueueService.GetEntriesByItemId().Values.ToArray();

            await _discordNotificationService.NotifyGracePeriodItemsAsync(
                configuration,
                pendingEntries,
                now,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Janitorfin Discord grace-period notification failed.");
        }
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