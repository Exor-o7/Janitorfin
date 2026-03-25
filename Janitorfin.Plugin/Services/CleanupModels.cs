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
}

public sealed class CleanupEvaluationSummary
{
    public DateTime GeneratedAtUtc { get; init; }

    public int ScannedItemCount { get; init; }

    public int CandidateCount => Candidates.Count;

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

    public IReadOnlyList<CleanupExecutionResult> Results { get; init; } = Array.Empty<CleanupExecutionResult>();
}