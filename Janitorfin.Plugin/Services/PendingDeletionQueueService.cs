using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Janitorfin.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Services;

public sealed class PendingDeletionQueueService
{
    public const int DefaultPendingDetailLimit = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _syncLock = new();
    private readonly ILogger<PendingDeletionQueueService> _logger;

    public PendingDeletionQueueService(ILogger<PendingDeletionQueueService> logger)
    {
        _logger = logger;
    }

    public Dictionary<Guid, PendingDeletionEntry> GetEntriesByItemId()
    {
        lock (_syncLock)
        {
            return LoadStateLocked()
                .Entries
                .GroupBy(entry => entry.ItemId)
                .Select(group => group.OrderByDescending(entry => entry.DeleteAfterUtc).First())
                .ToDictionary(entry => entry.ItemId, entry => entry);
        }
    }

    public PendingDeletionSummary GetSummary(PluginConfiguration configuration, int? detailLimit = null)
    {
        lock (_syncLock)
        {
            var entries = LoadStateLocked()
                .Entries
                .OrderBy(entry => entry.DeleteAfterUtc)
                .ThenBy(entry => entry.LibraryName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.SeriesName ?? entry.ItemName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ItemName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var normalizedLimit = detailLimit.GetValueOrDefault(entries.Length);
            if (normalizedLimit < 0)
            {
                normalizedLimit = 0;
            }

            return new PendingDeletionSummary
            {
                GeneratedAtUtc = DateTime.UtcNow,
                GraceDays = Math.Max(0, configuration.PendingDeletionGraceDays),
                EntryCount = entries.Length,
                DetailLimit = normalizedLimit,
                Entries = entries.Take(normalizedLimit).ToArray(),
            };
        }
    }

    public void ReconcileAndQueueEligibleCandidates(PluginConfiguration configuration, IReadOnlyList<CleanupCandidate> candidates, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(candidates);

        lock (_syncLock)
        {
            var state = LoadStateLocked();
            var candidateMap = candidates.ToDictionary(candidate => candidate.ItemId, candidate => candidate);

            state.Entries.RemoveAll(entry => !candidateMap.ContainsKey(entry.ItemId));

            if (!configuration.EnablePendingDeletion)
            {
                SaveStateLocked(state);
                return;
            }

            var graceDays = Math.Max(0, configuration.PendingDeletionGraceDays);
            foreach (var candidate in candidates)
            {
                var existingEntry = state.Entries.FirstOrDefault(entry => entry.ItemId == candidate.ItemId);
                if (existingEntry is null)
                {
                    state.Entries.Add(CreateEntry(candidate, nowUtc, graceDays));
                    continue;
                }

                existingEntry.ItemName = candidate.ItemName;
                existingEntry.ItemType = candidate.ItemType;
                existingEntry.LibraryName = candidate.LibraryName;
                existingEntry.SeriesName = candidate.SeriesName;
                existingEntry.SeasonName = candidate.SeasonName;
                existingEntry.Path = candidate.Path;
                existingEntry.Reason = candidate.Reason;
                existingEntry.AppliedRuleName = candidate.AppliedRuleName;
                existingEntry.LastMatchedUtc = nowUtc;
            }

            SaveStateLocked(state);
        }
    }

    public void RemoveEntry(Guid itemId)
    {
        lock (_syncLock)
        {
            var state = LoadStateLocked();
            if (state.Entries.RemoveAll(entry => entry.ItemId == itemId) > 0)
            {
                SaveStateLocked(state);
            }
        }
    }

    private PendingDeletionEntry CreateEntry(CleanupCandidate candidate, DateTime nowUtc, int graceDays)
    {
        return new PendingDeletionEntry
        {
            ItemId = candidate.ItemId,
            ItemName = candidate.ItemName,
            ItemType = candidate.ItemType,
            LibraryName = candidate.LibraryName,
            SeriesName = candidate.SeriesName,
            SeasonName = candidate.SeasonName,
            Path = candidate.Path,
            Reason = candidate.Reason,
            AppliedRuleName = candidate.AppliedRuleName,
            FirstQualifiedUtc = nowUtc,
            LastMatchedUtc = nowUtc,
            DeleteAfterUtc = nowUtc.AddDays(graceDays),
        };
    }

    private PendingDeletionQueueState LoadStateLocked()
    {
        var filePath = GetStateFilePath();

        try
        {
            if (!File.Exists(filePath))
            {
                return new PendingDeletionQueueState();
            }

            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new PendingDeletionQueueState();
            }

            return JsonSerializer.Deserialize<PendingDeletionQueueState>(json, JsonOptions) ?? new PendingDeletionQueueState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Janitorfin pending deletion queue from {Path}", filePath);
            return new PendingDeletionQueueState();
        }
    }

    private void SaveStateLocked(PendingDeletionQueueState state)
    {
        var filePath = GetStateFilePath();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Janitorfin pending deletion queue to {Path}", filePath);
            throw;
        }
    }

    private static string GetStateFilePath()
    {
        var dataFolderPath = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrWhiteSpace(dataFolderPath))
        {
            throw new InvalidOperationException("Janitorfin data folder path is unavailable.");
        }

        return Path.Combine(dataFolderPath, "pending-deletions.json");
    }

    private sealed class PendingDeletionQueueState
    {
        public List<PendingDeletionEntry> Entries { get; set; } = [];
    }
}