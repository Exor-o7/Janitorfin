using System;
using System.Collections.Generic;

namespace Janitorfin.Plugin.Services;

public enum CleanupReasonKind
{
    WatchedExceeded,
    NeverWatchedExceeded,
}

public sealed class CleanupCandidate
{
    public Guid ItemId { get; init; }

    public string ItemName { get; init; } = string.Empty;

    public string ItemType { get; init; } = string.Empty;

    public string? Path { get; init; }

    public string? LibraryName { get; init; }

    public string? SeriesName { get; init; }

    public string? SeasonName { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public string? SeriesPath { get; init; }

    public CleanupReasonKind ReasonKind { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string AppliedRuleName { get; init; } = string.Empty;

    public int? EffectiveDeleteAfterWatchDays { get; init; }

    public int? EffectiveDeleteNeverWatchedAfterDays { get; init; }

    public DateTime? DateAddedUtc { get; init; }

    public DateTime? LastPlayedDateUtc { get; init; }

    public IReadOnlyList<string> PlayedByUsers { get; init; } = Array.Empty<string>();

    public string? TmdbId { get; init; }

    public string? TvdbId { get; init; }

    public string? ImdbId { get; init; }

    public string? SeriesTmdbId { get; init; }

    public string? SeriesTvdbId { get; init; }

    public string? SeriesImdbId { get; init; }

    public bool IsPendingDeletion { get; init; }

    public DateTime? PendingSinceUtc { get; init; }

    public DateTime? PendingDeleteAfterUtc { get; init; }
}

public sealed class CleanupCountBucket
{
    public string Label { get; init; } = string.Empty;

    public int Count { get; init; }
}

public sealed class CleanupEvaluationSummary
{
    public DateTime GeneratedAtUtc { get; init; }

    public int ScannedItemCount { get; init; }

    public int CandidateCount { get; init; }

    public int CandidateDetailLimit { get; init; }

    public int PendingCandidateCount { get; init; }

    public int DuePendingCandidateCount { get; init; }

    public int DisplayedCandidateCount => Candidates.Count;

    public bool IsCandidateListTruncated => CandidateCount > Candidates.Count;

    public IReadOnlyList<CleanupCountBucket> CandidatesByLibrary { get; init; } = Array.Empty<CleanupCountBucket>();

    public IReadOnlyList<CleanupCountBucket> CandidatesByItemType { get; init; } = Array.Empty<CleanupCountBucket>();

    public IReadOnlyList<CleanupCountBucket> CandidatesByReason { get; init; } = Array.Empty<CleanupCountBucket>();

    public IReadOnlyList<CleanupCandidate> Candidates { get; init; } = Array.Empty<CleanupCandidate>();
}

public sealed class IntegrationTestResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? Version { get; init; }
}

public sealed class ArrActionResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public int? RemoteId { get; init; }
}

public sealed class CleanupExecutionResult
{
    public Guid ItemId { get; init; }

    public string ItemName { get; init; } = string.Empty;

    public string ItemType { get; init; } = string.Empty;

    public bool Deleted { get; init; }

    public bool RadarrUpdated { get; init; }

    public bool SonarrUpdated { get; init; }

    public string Outcome { get; init; } = string.Empty;

    public string? Error { get; init; }

    public DateTime? PendingDeleteAfterUtc { get; init; }
}

public sealed class CleanupExecutionSummary
{
    public bool DryRun { get; init; }

    public DateTime ExecutedAtUtc { get; init; }

    public int ScannedItemCount { get; init; }

    public int CandidateCount { get; init; }

    public int DeletedCount { get; init; }

    public int FailedCount { get; init; }

    public int RadarrUpdatedCount { get; init; }

    public int SonarrUpdatedCount { get; init; }

    public int QueuedCount { get; init; }

    public int PendingCount { get; init; }

    public int ResultCount { get; init; }

    public int ResultDetailLimit { get; init; }

    public int DisplayedResultCount => Results.Count;

    public bool IsResultListTruncated => ResultCount > Results.Count;

    public IReadOnlyList<CleanupExecutionResult> Results { get; init; } = Array.Empty<CleanupExecutionResult>();
}

public sealed class CleanupTaskStartResult
{
    public bool Started { get; init; }

    public bool AlreadyRunning { get; init; }

    public bool DryRun { get; init; }

    public string TaskId { get; init; } = string.Empty;

    public string TaskName { get; init; } = string.Empty;

    public string TaskState { get; init; } = string.Empty;

    public double? CurrentProgress { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed class PendingDeletionEntry
{
    public Guid ItemId { get; set; }

    public string ItemName { get; set; } = string.Empty;

    public string ItemType { get; set; } = string.Empty;

    public string? LibraryName { get; set; }

    public string? SeriesName { get; set; }

    public string? SeasonName { get; set; }

    public string? Path { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string AppliedRuleName { get; set; } = string.Empty;

    public DateTime FirstQualifiedUtc { get; set; }

    public DateTime LastMatchedUtc { get; set; }

    public DateTime DeleteAfterUtc { get; set; }
}

public sealed class PendingDeletionSummary
{
    public DateTime GeneratedAtUtc { get; init; }

    public int GraceDays { get; init; }

    public int EntryCount { get; init; }

    public int DetailLimit { get; init; }

    public int DisplayedEntryCount => Entries.Count;

    public bool IsEntryListTruncated => EntryCount > Entries.Count;

    public IReadOnlyList<PendingDeletionEntry> Entries { get; init; } = Array.Empty<PendingDeletionEntry>();
}