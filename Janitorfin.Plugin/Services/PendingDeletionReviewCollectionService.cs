using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Janitorfin.Plugin.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Services;

public sealed class PendingDeletionReviewCollectionService
{
    public const string DefaultCollectionName = "Janitorfin Review";

    private readonly ICollectionManager _collectionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly PendingDeletionQueueService _pendingDeletionQueueService;
    private readonly ILogger<PendingDeletionReviewCollectionService> _logger;

    public PendingDeletionReviewCollectionService(
        ICollectionManager collectionManager,
        ILibraryManager libraryManager,
        PendingDeletionQueueService pendingDeletionQueueService,
        ILogger<PendingDeletionReviewCollectionService> logger)
    {
        _collectionManager = collectionManager;
        _libraryManager = libraryManager;
        _pendingDeletionQueueService = pendingDeletionQueueService;
        _logger = logger;
    }

    public async Task SyncAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var reviewCollectionName = DefaultCollectionName;
        var stagedItemIds = configuration.EnablePendingDeletion
            ? _pendingDeletionQueueService.GetEntriesByItemId().Keys.ToHashSet()
            : [];

        var collection = FindCollection(reviewCollectionName);
        if (collection is null)
        {
            if (stagedItemIds.Count == 0)
            {
                return;
            }

            collection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = reviewCollectionName,
                ItemIdList = stagedItemIds.Select(static id => id.ToString()).ToArray(),
            }).ConfigureAwait(false);

            _logger.LogInformation(
                "Janitorfin created review collection {CollectionName} with {ItemCount} staged items.",
                reviewCollectionName,
                stagedItemIds.Count);

            return;
        }

        var existingItemIds = collection.LinkedChildren
            .Where(child => child.ItemId.HasValue)
            .Select(child => child.ItemId!.Value)
            .ToHashSet();

        var itemIdsToAdd = stagedItemIds
            .Except(existingItemIds)
            .Where(id => _libraryManager.GetItemById(id) is not null)
            .ToArray();

        var itemIdsToRemove = existingItemIds
            .Except(stagedItemIds)
            .ToArray();

        if (itemIdsToAdd.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _collectionManager.AddToCollectionAsync(collection.Id, itemIdsToAdd).ConfigureAwait(false);
        }

        if (itemIdsToRemove.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _collectionManager.RemoveFromCollectionAsync(collection.Id, itemIdsToRemove).ConfigureAwait(false);
        }

        if (itemIdsToAdd.Length > 0 || itemIdsToRemove.Length > 0)
        {
            _logger.LogInformation(
                "Janitorfin synced review collection {CollectionName}. Added={Added}, Removed={Removed}, Staged={Staged}.",
                reviewCollectionName,
                itemIdsToAdd.Length,
                itemIdsToRemove.Length,
                stagedItemIds.Count);
        }
    }

    private BoxSet? FindCollection(string collectionName)
    {
        var boxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true,
            CollapseBoxSetItems = false,
        });

        return boxSets
            .OfType<BoxSet>()
            .FirstOrDefault(boxSet => string.Equals(boxSet.Name, collectionName, StringComparison.OrdinalIgnoreCase));
    }
}