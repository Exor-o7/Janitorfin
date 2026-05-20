using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Janitorfin.Plugin.Configuration;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Services;

public interface IJanitorfinActivityLogService
{
    Task LogPendingDeletionQueuedAsync(PluginConfiguration configuration, IReadOnlyList<CleanupCandidate> candidates);

    Task LogDeletedAsync(PluginConfiguration configuration, IReadOnlyList<CleanupCandidate> candidates);
}

internal sealed class JanitorfinActivityLogService : IJanitorfinActivityLogService
{
    private const string PendingType = "JanitorfinPendingDeletionQueued";
    private const string DeletedType = "JanitorfinDeleted";

    private readonly IActivityManager _activityManager;
    private readonly ILogger<JanitorfinActivityLogService> _logger;

    public JanitorfinActivityLogService(IActivityManager activityManager, ILogger<JanitorfinActivityLogService> logger)
    {
        _activityManager = activityManager;
        _logger = logger;
    }

    public Task LogPendingDeletionQueuedAsync(PluginConfiguration configuration, IReadOnlyList<CleanupCandidate> candidates)
    {
        return LogGroupedCandidatesAsync(
            configuration,
            candidates,
            PendingType,
            "Janitorfin queued for deletion",
            "Added to Janitorfin pending deletion list");
    }

    public Task LogDeletedAsync(PluginConfiguration configuration, IReadOnlyList<CleanupCandidate> candidates)
    {
        return LogGroupedCandidatesAsync(
            configuration,
            candidates,
            DeletedType,
            "Janitorfin deleted media",
            "Deleted by Janitorfin");
    }

    private async Task LogGroupedCandidatesAsync(
        PluginConfiguration configuration,
        IReadOnlyList<CleanupCandidate> candidates,
        string type,
        string namePrefix,
        string overviewPrefix)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        foreach (var group in BuildActivityGroups(configuration, candidates))
        {
            try
            {
                await _activityManager.CreateAsync(new ActivityLog(namePrefix + ": " + group.DisplayName, type, Guid.Empty)
                {
                    DateCreated = DateTime.UtcNow,
                    ItemId = group.ItemId,
                    Overview = overviewPrefix + ": " + group.DisplayName,
                    ShortOverview = group.DisplayName,
                    LogSeverity = LogLevel.Information,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create Janitorfin activity log entry for {DisplayName}", group.DisplayName);
            }
        }
    }

    private static IEnumerable<ActivityGroup> BuildActivityGroups(PluginConfiguration configuration, IReadOnlyList<CleanupCandidate> candidates)
    {
        var movieGroups = candidates
            .Where(candidate => !string.Equals(candidate.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
            .Select(candidate => new ActivityGroup(
                FormatTitle(candidate.ItemName, candidate.ProductionYear),
                candidate.ItemId.ToString("N", CultureInfo.InvariantCulture)));
        var episodeCandidates = candidates
            .Where(candidate => string.Equals(candidate.ItemType, "Episode", StringComparison.OrdinalIgnoreCase));
        var tvGroups = configuration.TvCleanupScope == TvCleanupScope.Series
            ? BuildSeriesGroups(episodeCandidates)
            : BuildSeasonGroups(episodeCandidates);

        return tvGroups
            .Concat(movieGroups)
            .GroupBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<ActivityGroup> BuildSeriesGroups(IEnumerable<CleanupCandidate> candidates)
    {
        return candidates
            .GroupBy(candidate => new SeriesGroupKey(
                string.IsNullOrWhiteSpace(candidate.SeriesName) ? candidate.ItemName : candidate.SeriesName!,
                candidate.ProductionYear))
            .Select(group => new ActivityGroup(
                FormatTitle(group.Key.Title, group.Key.Year),
                group.First().ItemId.ToString("N", CultureInfo.InvariantCulture)));
    }

    private static IEnumerable<ActivityGroup> BuildSeasonGroups(IEnumerable<CleanupCandidate> candidates)
    {
        return candidates
            .GroupBy(candidate => new SeasonGroupKey(
                string.IsNullOrWhiteSpace(candidate.SeriesName) ? candidate.ItemName : candidate.SeriesName!,
                candidate.ProductionYear,
                candidate.SeasonNumber,
                candidate.SeasonName))
            .Select(group => new ActivityGroup(
                FormatSeasonTitle(group.Key),
                group.First().ItemId.ToString("N", CultureInfo.InvariantCulture)));
    }

    private static string FormatSeasonTitle(SeasonGroupKey key)
    {
        var seasonName = key.SeasonNumber.HasValue
            ? "Season " + key.SeasonNumber.Value.ToString(CultureInfo.InvariantCulture)
            : string.IsNullOrWhiteSpace(key.SeasonName)
                ? "Unknown season"
                : key.SeasonName!;

        return FormatTitle(key.Title, key.Year) + " - " + seasonName;
    }

    private static string FormatTitle(string title, int? year)
    {
        return year.HasValue && year.Value > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", title, year.Value)
            : title;
    }

    private readonly record struct ActivityGroup(string DisplayName, string ItemId);

    private readonly record struct SeriesGroupKey(string Title, int? Year);

    private readonly record struct SeasonGroupKey(string Title, int? Year, int? SeasonNumber, string? SeasonName);
}
