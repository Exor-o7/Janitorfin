using System;
using System.Collections.Generic;
using System.Linq;
using Janitorfin.Plugin.Configuration;
using MediaBrowser.Controller.Entities;
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

            if (seenIds.Add(item.Id))
            {
                reviewItems.Add(item);
            }
        }

        return reviewItems
            .OrderBy(GetPrimarySortKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(GetSecondarySortKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SortName ?? item.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Id)
            .ToArray();
    }

    private static string GetPrimarySortKey(BaseItem item)
    {
        return item.SortName ?? item.Name ?? string.Empty;
    }

    private static string GetSecondarySortKey(BaseItem item)
    {
        return item.Name ?? string.Empty;
    }
}