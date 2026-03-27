using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Janitorfin.Plugin.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Janitorfin.Plugin.Services;

public sealed class PendingDeletionReviewItemService
{
    private readonly ILibraryManager _libraryManager;
    private readonly PendingDeletionQueueService _pendingDeletionQueueService;

    public PendingDeletionReviewItemService(
        ILibraryManager libraryManager,
        PendingDeletionQueueService pendingDeletionQueueService)
    {
        _libraryManager = libraryManager;
        _pendingDeletionQueueService = pendingDeletionQueueService;
    }

    public IReadOnlyList<Guid> GetReviewItemIds(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.EnablePendingDeletion)
        {
            return Array.Empty<Guid>();
        }

        var entries = _pendingDeletionQueueService.GetSummary(configuration).Entries;
        if (entries.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var reviewItems = new List<BaseItem>(entries.Count);
        var seenIds = new HashSet<Guid>();

        foreach (var entry in entries)
        {
            var item = _libraryManager.GetItemById(entry.ItemId);
            if (item is null)
            {
                continue;
            }

            var reviewItemId = ResolveReviewItemId(item, configuration.TvCleanupScope);
            if (!reviewItemId.HasValue)
            {
                continue;
            }

            if (seenIds.Add(reviewItemId.Value))
            {
                var reviewItem = _libraryManager.GetItemById(reviewItemId.Value);
                if (reviewItem is not null)
                {
                    reviewItems.Add(reviewItem);
                }
            }
        }

        return reviewItems
            .OrderBy(GetPrimarySortKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(GetSecondarySortKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(GetSeasonSortOrder)
            .ThenBy(item => item.SortName ?? item.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Id)
            .ToArray();
    }

    private static string GetPrimarySortKey(BaseItem item)
    {
        return item switch
        {
            Season season => season.FindSeriesSortName() ?? season.FindSeriesName() ?? season.SeriesName ?? season.Name ?? string.Empty,
            Series series => series.SortName ?? series.Name ?? string.Empty,
            _ => item.SortName ?? item.Name ?? string.Empty,
        };
    }

    private static string GetSecondarySortKey(BaseItem item)
    {
        return item switch
        {
            Season season => season.FindSeriesName() ?? season.SeriesName ?? string.Empty,
            Series => string.Empty,
            _ => item.Name ?? string.Empty,
        };
    }

    private static int GetSeasonSortOrder(BaseItem item)
    {
        return item switch
        {
            Season season when season.IndexNumber.HasValue => season.IndexNumber.Value,
            Season => int.MaxValue,
            Series => -1,
            _ => int.MaxValue,
        };
    }

    private Guid? ResolveReviewItemId(BaseItem item, TvCleanupScope tvCleanupScope)
    {
        if (item is not Episode episode)
        {
            return item.Id;
        }

        if (tvCleanupScope == TvCleanupScope.Series)
        {
            var seriesId = episode.SeriesId;
            if (seriesId == Guid.Empty)
            {
                seriesId = episode.FindSeriesId();
            }

            return ResolveContainerItemId(seriesId);
        }

        if (tvCleanupScope == TvCleanupScope.Season)
        {
            var seasonId = episode.SeasonId;
            if (seasonId == Guid.Empty)
            {
                seasonId = episode.FindSeasonId();
            }

            return ResolveContainerItemId(seasonId);
        }

        return null;
    }

    private Guid? ResolveContainerItemId(Guid itemId)
    {
        if (itemId == Guid.Empty)
        {
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        return item?.Id;
    }
}