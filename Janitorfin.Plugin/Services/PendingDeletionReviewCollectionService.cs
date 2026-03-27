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
    public const string DefaultCollectionName = "Removing Soon";

    private readonly ICollectionManager _collectionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly PendingDeletionReviewItemService _pendingDeletionReviewItemService;
    private readonly ILogger<PendingDeletionReviewCollectionService> _logger;

    public PendingDeletionReviewCollectionService(
        ICollectionManager collectionManager,
        ILibraryManager libraryManager,
        PendingDeletionReviewItemService pendingDeletionReviewItemService,
        ILogger<PendingDeletionReviewCollectionService> logger)
    {
        _collectionManager = collectionManager;
        _libraryManager = libraryManager;
        _pendingDeletionReviewItemService = pendingDeletionReviewItemService;
        _logger = logger;
    }

    public async Task SyncAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var reviewCollectionName = DefaultCollectionName;
        var stagedItemIds = _pendingDeletionReviewItemService.GetReviewItemIds(configuration);
        var stagedItemIdSet = stagedItemIds.ToHashSet();

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

            await EnsureManualCollectionOrderAsync(collection, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Janitorfin created review collection {CollectionName} with {ItemCount} staged items.",
                reviewCollectionName,
                stagedItemIds.Count);

            return;
        }

        await EnsureManualCollectionOrderAsync(collection, cancellationToken).ConfigureAwait(false);

        var existingItemIds = collection.GetLinkedChildren()
            .Select(child => child.Id)
            .ToArray();

        var itemIdsToRemove = existingItemIds
            .Where(id => !stagedItemIdSet.Contains(id))
            .ToArray();

        var itemIdsToAdd = stagedItemIds
            .Where(id => _libraryManager.GetItemById(id) is not null)
            .Where(id => !existingItemIds.Contains(id))
            .ToArray();

        var isOrderDifferent = existingItemIds
            .Where(stagedItemIdSet.Contains)
            .SequenceEqual(stagedItemIds) == false;

        if (isOrderDifferent && existingItemIds.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _collectionManager.RemoveFromCollectionAsync(collection.Id, existingItemIds).ConfigureAwait(false);
            existingItemIds = Array.Empty<Guid>();
            itemIdsToRemove = Array.Empty<Guid>();
            itemIdsToAdd = stagedItemIds
                .Where(id => _libraryManager.GetItemById(id) is not null)
                .ToArray();
        }

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

        if (itemIdsToAdd.Length > 0 || itemIdsToRemove.Length > 0 || isOrderDifferent)
        {
            _logger.LogInformation(
                "Janitorfin synced review collection {CollectionName}. Added={Added}, Removed={Removed}, Reordered={Reordered}, Staged={Staged}.",
                reviewCollectionName,
                itemIdsToAdd.Length,
                itemIdsToRemove.Length,
                isOrderDifferent,
                stagedItemIds.Count);
        }
    }

    private static async Task EnsureManualCollectionOrderAsync(BoxSet collection, CancellationToken cancellationToken)
    {
        if (string.Equals(collection.DisplayOrder, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        collection.DisplayOrder = "Default";
        await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
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