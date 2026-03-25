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
    private readonly CleanupEvaluationService _cleanupEvaluationService;
    private readonly ILibraryManager _libraryManager;
    private readonly IRadarrClient _radarrClient;
    private readonly ISonarrClient _sonarrClient;
    private readonly ILogger<CleanupExecutionService> _logger;

    public CleanupExecutionService(
        CleanupEvaluationService cleanupEvaluationService,
        ILibraryManager libraryManager,
        IRadarrClient radarrClient,
        ISonarrClient sonarrClient,
        ILogger<CleanupExecutionService> logger)
    {
        _cleanupEvaluationService = cleanupEvaluationService;
        _libraryManager = libraryManager;
        _radarrClient = radarrClient;
        _sonarrClient = sonarrClient;
        _logger = logger;
    }

    public async Task<CleanupExecutionSummary> ExecuteAsync(PluginConfiguration configuration, bool? dryRunOverride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var evaluation = await _cleanupEvaluationService.EvaluateAsync(configuration, cancellationToken).ConfigureAwait(false);
        var dryRun = dryRunOverride ?? configuration.DryRun;
        var results = new List<CleanupExecutionResult>();
        var deletedCount = 0;
        var failedCount = 0;
        var radarrUpdatedCount = 0;
        var sonarrUpdatedCount = 0;

        foreach (var candidate in evaluation.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (dryRun)
            {
                results.Add(new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidate.ItemName,
                    ItemType = candidate.ItemType,
                    Outcome = "Dry run candidate",
                });
                continue;
            }

            var item = _libraryManager.GetItemById(candidate.ItemId);
            if (item is null)
            {
                failedCount++;
                results.Add(new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidate.ItemName,
                    ItemType = candidate.ItemType,
                    Outcome = "Skipped",
                    Error = "Item no longer exists in Jellyfin.",
                });
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
                        results.Add(new CleanupExecutionResult
                        {
                            ItemId = candidate.ItemId,
                            ItemName = candidate.ItemName,
                            ItemType = candidate.ItemType,
                            Outcome = "Skipped",
                            Error = radarrResult.Message,
                        });
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
                        results.Add(new CleanupExecutionResult
                        {
                            ItemId = candidate.ItemId,
                            ItemName = candidate.ItemName,
                            ItemType = candidate.ItemType,
                            Outcome = "Skipped",
                            Error = sonarrResult.Message,
                        });
                        continue;
                    }

                    sonarrUpdated = true;
                    sonarrUpdatedCount++;
                }

                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
                deletedCount++;
                results.Add(new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidate.ItemName,
                    ItemType = candidate.ItemType,
                    Deleted = true,
                    RadarrUpdated = radarrUpdated,
                    SonarrUpdated = sonarrUpdated,
                    Outcome = "Deleted",
                });
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(ex, "Error processing cleanup candidate {ItemName} ({ItemId})", candidate.ItemName, candidate.ItemId);
                results.Add(new CleanupExecutionResult
                {
                    ItemId = candidate.ItemId,
                    ItemName = candidate.ItemName,
                    ItemType = candidate.ItemType,
                    RadarrUpdated = radarrUpdated,
                    SonarrUpdated = sonarrUpdated,
                    Outcome = "Failed",
                    Error = ex.Message,
                });
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
            RadarrUpdatedCount = radarrUpdatedCount,
            SonarrUpdatedCount = sonarrUpdatedCount,
            Results = results,
        };
    }
}