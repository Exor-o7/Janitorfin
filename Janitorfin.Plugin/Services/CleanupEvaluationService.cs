using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Janitorfin.Plugin.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Services;

public sealed class CleanupEvaluationService
{
    public const int DefaultPreviewCandidateDetailLimit = 200;

    private enum CandidateMediaKind
    {
        Movie,
        Episode,
        Video,
    }

    private sealed class ResolvedCleanupRules
    {
        public int? DeleteAfterWatchDays { get; init; }

        public int? DeleteNeverWatchedAfterDays { get; init; }

        public string WatchedRuleName { get; init; } = string.Empty;

        public string NeverWatchedRuleName { get; init; } = string.Empty;
    }

    private sealed class ItemEvaluationOutcome
    {
        public BaseItem Item { get; init; } = null!;

        public CleanupCandidate? Candidate { get; init; }
    }

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly PendingDeletionQueueService _pendingDeletionQueueService;
    private readonly IJellystatClient _jellystatClient;
    private readonly ILogger<CleanupEvaluationService> _logger;

    public CleanupEvaluationService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        PendingDeletionQueueService pendingDeletionQueueService,
        IJellystatClient jellystatClient,
        ILogger<CleanupEvaluationService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _pendingDeletionQueueService = pendingDeletionQueueService;
        _jellystatClient = jellystatClient;
        _logger = logger;
    }

    public async Task<CleanupEvaluationSummary> EvaluateAsync(PluginConfiguration configuration, CancellationToken cancellationToken, int? candidateDetailLimit = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes =
            [
                BaseItemKind.Movie,
                BaseItemKind.Episode,
                BaseItemKind.Video,
            ],
        };

        if (!string.IsNullOrWhiteSpace(configuration.ProtectedTag))
        {
            query.ExcludeTags = [configuration.ProtectedTag.Trim()];
        }

        var items = _libraryManager.GetItemList(query);
        var users = GetUsers();
        var now = DateTime.UtcNow;
        var pendingEntriesById = _pendingDeletionQueueService.GetEntriesByItemId();
        var jellystatPlayback = await _jellystatClient.GetPlaybackSnapshotAsync(configuration, cancellationToken).ConfigureAwait(false);

        var candidates = new List<CleanupCandidate>();
        var episodeOutcomesByGroup = new Dictionary<string, List<ItemEvaluationOutcome>>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = EvaluateItem(item, configuration, users, now, pendingEntriesById, jellystatPlayback);
            if (outcome is null)
            {
                continue;
            }

            if (item is Episode episode)
            {
                var groupKey = GetEpisodeCleanupGroupKey(episode, configuration.TvCleanupScope);
                if (!episodeOutcomesByGroup.TryGetValue(groupKey, out var groupOutcomes))
                {
                    groupOutcomes = new List<ItemEvaluationOutcome>();
                    episodeOutcomesByGroup.Add(groupKey, groupOutcomes);
                }

                groupOutcomes.Add(outcome);
                continue;
            }

            if (outcome.Candidate is not null)
            {
                candidates.Add(outcome.Candidate);
            }
        }

        foreach (var groupOutcomes in episodeOutcomesByGroup.Values)
        {
            if (groupOutcomes.All(outcome => outcome.Candidate is not null))
            {
                _logger.LogDebug(
                    "TV cleanup group qualified. Scope={Scope}, Episodes={EpisodeCount}",
                    configuration.TvCleanupScope,
                    groupOutcomes.Count);
                candidates.AddRange(groupOutcomes.Select(outcome => outcome.Candidate!));
            }
            else
            {
                _logger.LogDebug(
                    "TV cleanup group skipped because not every episode qualified. Scope={Scope}, Qualified={QualifiedCount}, Total={TotalCount}",
                    configuration.TvCleanupScope,
                    groupOutcomes.Count(outcome => outcome.Candidate is not null),
                    groupOutcomes.Count);
            }
        }

        _logger.LogInformation(
            "Janitorfin evaluation completed. Scanned={Scanned}, Candidates={Candidates}",
            items.Count,
            candidates.Count);

        var orderedCandidates = candidates
            .OrderBy(candidate => candidate.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SeriesName ?? candidate.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.SeasonName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var normalizedDetailLimit = candidateDetailLimit.GetValueOrDefault(orderedCandidates.Length);
        if (normalizedDetailLimit < 0)
        {
            normalizedDetailLimit = 0;
        }

        return new CleanupEvaluationSummary
        {
            GeneratedAtUtc = now,
            ScannedItemCount = items.Count,
            CandidateCount = orderedCandidates.Length,
            CandidateDetailLimit = normalizedDetailLimit,
            PendingCandidateCount = orderedCandidates.Count(candidate => candidate.IsPendingDeletion),
            DuePendingCandidateCount = orderedCandidates.Count(candidate => candidate.PendingDeleteAfterUtc.HasValue && candidate.PendingDeleteAfterUtc.Value <= now),
            CandidatesByLibrary = BuildBuckets(orderedCandidates, candidate => candidate.LibraryName, 12),
            CandidatesByItemType = BuildBuckets(orderedCandidates, candidate => candidate.ItemType),
            CandidatesByReason = BuildBuckets(orderedCandidates, candidate => candidate.Reason),
            Candidates = orderedCandidates.Take(normalizedDetailLimit).ToArray(),
        };
    }

    private static CleanupCountBucket[] BuildBuckets(IEnumerable<CleanupCandidate> candidates, Func<CleanupCandidate, string?> selector, int? limit = null)
    {
        IEnumerable<CleanupCountBucket> buckets = candidates
            .GroupBy(candidate => selector(candidate) ?? "Unknown")
            .Select(group => new CleanupCountBucket
            {
                Label = group.Key,
                Count = group.Count(),
            })
            .OrderByDescending(bucket => bucket.Count)
            .ThenBy(bucket => bucket.Label, StringComparer.OrdinalIgnoreCase);

        if (limit.HasValue)
        {
            buckets = buckets.Take(limit.Value);
        }

        return buckets.ToArray();
    }

    private static bool ShouldSkipItem(BaseItem item, PluginConfiguration configuration)
    {
        if (item.IsFolder || string.IsNullOrWhiteSpace(item.Path))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configuration.ProtectedTag)
            && item.Tags is not null
            && item.Tags.Any(tag => string.Equals(tag, configuration.ProtectedTag, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private User[] GetUsers()
    {
        var userIdsProperty = _userManager.GetType().GetProperty("UsersIds")
            ?? _userManager.GetType().GetProperty("UserIds");
        if (userIdsProperty?.GetValue(_userManager) is IEnumerable<Guid> userIds)
        {
            return userIds
                .Select(_userManager.GetUserById)
                .Where(static user => user is not null)
                .Select(static user => user!)
                .ToArray();
        }

        var usersProperty = _userManager.GetType().GetProperty("Users");
        return usersProperty?.GetValue(_userManager) is IEnumerable<User> users
            ? users.ToArray()
            : [];
    }

    private static string GetEpisodeCleanupGroupKey(Episode episode, TvCleanupScope scope)
    {
        if (scope == TvCleanupScope.Series && episode.Series is not null)
        {
            return "series:" + episode.Series.Id.ToString("N", CultureInfo.InvariantCulture);
        }

        if (scope == TvCleanupScope.Season)
        {
            if (episode.Season is not null)
            {
                return "season:" + episode.Season.Id.ToString("N", CultureInfo.InvariantCulture);
            }

            if (episode.Series is not null && episode.ParentIndexNumber.HasValue)
            {
                return "series-season:" + episode.Series.Id.ToString("N", CultureInfo.InvariantCulture) + ":" + episode.ParentIndexNumber.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (episode.Series is not null)
        {
            return "series:" + episode.Series.Id.ToString("N", CultureInfo.InvariantCulture);
        }

        return "episode:" + episode.Id.ToString("N", CultureInfo.InvariantCulture);
    }

    private ItemEvaluationOutcome? EvaluateItem(
        BaseItem item,
        PluginConfiguration configuration,
        IReadOnlyList<User> users,
        DateTime now,
        IReadOnlyDictionary<Guid, PendingDeletionEntry> pendingEntriesById,
        JellystatPlaybackSnapshot jellystatPlayback)
    {
        if (ShouldSkipItem(item, configuration))
        {
            _logger.LogDebug("Skipping {ItemName} ({ItemId}) because it is a folder, missing a path, or has the protected tag.", item.Name, item.Id);
            return null;
        }

        var userNames = new List<string>();
        DateTime? latestPlayed = null;

        foreach (var user in users)
        {
            if (user is null)
            {
                continue;
            }

            var userData = _userDataManager.GetUserData(user, item);
            if (userData is null)
            {
                continue;
            }

            if (configuration.KeepFavorites && userData.IsFavorite)
            {
                _logger.LogDebug(
                    "Skipping {ItemName} ({ItemId}) because user {UserName} marked it as favorite.",
                    item.Name,
                    item.Id,
                    user.Username ?? "Unknown user");
                return new ItemEvaluationOutcome
                {
                    Item = item,
                };
            }

            if (userData.Played || userData.PlayCount > 0 || userData.LastPlayedDate.HasValue)
            {
                AddUserName(userNames, user.Username ?? "Unknown user");
            }

            if (userData.LastPlayedDate.HasValue
                && (!latestPlayed.HasValue || userData.LastPlayedDate.Value > latestPlayed.Value))
            {
                latestPlayed = userData.LastPlayedDate.Value;
            }
        }

        ApplyJellystatPlayback(item, configuration, jellystatPlayback, userNames, ref latestPlayed);

        var addedUtc = NormalizeUtc(item.DateCreated);
        var libraryName = _libraryManager.GetCollectionFolders(item).FirstOrDefault()?.Name;
        var resolvedRules = ResolveRules(configuration, item, libraryName);
        var watchedCutoff = resolvedRules.DeleteAfterWatchDays.HasValue
            ? now.AddDays(-resolvedRules.DeleteAfterWatchDays.Value)
            : (DateTime?)null;
        var neverWatchedCutoff = resolvedRules.DeleteNeverWatchedAfterDays.HasValue
            ? now.AddDays(-resolvedRules.DeleteNeverWatchedAfterDays.Value)
            : (DateTime?)null;

        if (watchedCutoff.HasValue && latestPlayed.HasValue && latestPlayed.Value <= watchedCutoff.Value)
        {
            _logger.LogDebug(
                "{ItemName} ({ItemId}) qualifies by watched retention. LastPlayed={LastPlayed:u}, Cutoff={Cutoff:u}, Rule={RuleName}",
                item.Name,
                item.Id,
                latestPlayed.Value,
                watchedCutoff.Value,
                resolvedRules.WatchedRuleName);
            return new ItemEvaluationOutcome
            {
                Item = item,
                Candidate = BuildCandidate(item, libraryName, resolvedRules, CleanupReasonKind.WatchedExceeded, latestPlayed, addedUtc, userNames, pendingEntriesById.GetValueOrDefault(item.Id)),
            };
        }

        if (neverWatchedCutoff.HasValue && !latestPlayed.HasValue && addedUtc.HasValue && addedUtc.Value <= neverWatchedCutoff.Value)
        {
            _logger.LogDebug(
                "{ItemName} ({ItemId}) qualifies by never-watched retention. Added={Added:u}, Cutoff={Cutoff:u}, Rule={RuleName}",
                item.Name,
                item.Id,
                addedUtc.Value,
                neverWatchedCutoff.Value,
                resolvedRules.NeverWatchedRuleName);
            return new ItemEvaluationOutcome
            {
                Item = item,
                Candidate = BuildCandidate(item, libraryName, resolvedRules, CleanupReasonKind.NeverWatchedExceeded, latestPlayed, addedUtc, userNames, pendingEntriesById.GetValueOrDefault(item.Id)),
            };
        }

        _logger.LogDebug(
            "{ItemName} ({ItemId}) does not qualify. Added={Added:u}, LastPlayed={LastPlayed:u}, WatchedCutoff={WatchedCutoff:u}, NeverWatchedCutoff={NeverWatchedCutoff:u}",
            item.Name,
            item.Id,
            addedUtc,
            latestPlayed,
            watchedCutoff,
            neverWatchedCutoff);

        return new ItemEvaluationOutcome
        {
            Item = item,
        };
    }

    private void ApplyJellystatPlayback(
        BaseItem item,
        PluginConfiguration configuration,
        JellystatPlaybackSnapshot jellystatPlayback,
        List<string> userNames,
        ref DateTime? latestPlayed)
    {
        if (jellystatPlayback.IsEmpty)
        {
            _logger.LogDebug("Jellystat playback unavailable for {ItemName} ({ItemId}); using Jellyfin user data only.", item.Name, item.Id);
            return;
        }

        var runtimeSeconds = GetRuntimeSeconds(item);
        if (!runtimeSeconds.HasValue)
        {
            _logger.LogDebug("Jellystat playback skipped for {ItemName} ({ItemId}) because runtime is unavailable.", item.Name, item.Id);
            return;
        }

        var itemId = GetJellystatItemId(item);
        if (itemId is null || !jellystatPlayback.RecordsByItemId.TryGetValue(itemId, out var records))
        {
            _logger.LogDebug("No Jellystat playback records found for {ItemName} ({ItemId}).", item.Name, item.Id);
            return;
        }

        var thresholdPercent = configuration.JellystatWatchedThresholdPercent is > 0 and <= 100
            ? configuration.JellystatWatchedThresholdPercent
            : 90;
        var minimumPlaybackSeconds = runtimeSeconds.Value * (thresholdPercent / 100.0);

        foreach (var record in records.Where(record => record.PlaybackSeconds >= minimumPlaybackSeconds))
        {
            _logger.LogDebug(
                "Jellystat marked {ItemName} ({ItemId}) watched for user {UserName}. PlaybackSeconds={PlaybackSeconds}, RuntimeSeconds={RuntimeSeconds}, ThresholdPercent={ThresholdPercent}",
                item.Name,
                item.Id,
                record.UserName,
                record.PlaybackSeconds,
                runtimeSeconds.Value,
                thresholdPercent);
            AddUserName(userNames, record.UserName);

            if (record.LastPlayedUtc.HasValue
                && (!latestPlayed.HasValue || record.LastPlayedUtc.Value > latestPlayed.Value))
            {
                latestPlayed = record.LastPlayedUtc.Value;
            }
        }

        if (!records.Any(record => record.PlaybackSeconds >= minimumPlaybackSeconds))
        {
            _logger.LogDebug(
                "Jellystat records for {ItemName} ({ItemId}) did not meet watched threshold. MaxPlaybackSeconds={MaxPlaybackSeconds}, RuntimeSeconds={RuntimeSeconds}, ThresholdPercent={ThresholdPercent}",
                item.Name,
                item.Id,
                records.Count == 0 ? 0 : records.Max(record => record.PlaybackSeconds),
                runtimeSeconds.Value,
                thresholdPercent);
        }
    }

    private static string? GetJellystatItemId(BaseItem item)
    {
        return JellystatClient.NormalizeJellyfinItemId(item.Id.ToString("N", CultureInfo.InvariantCulture));
    }

    private static double? GetRuntimeSeconds(BaseItem item)
    {
        if (item.RunTimeTicks <= 0)
        {
            return null;
        }

        return item.RunTimeTicks / 10000000.0;
    }

    private static void AddUserName(List<string> userNames, string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return;
        }

        if (!userNames.Contains(userName, StringComparer.OrdinalIgnoreCase))
        {
            userNames.Add(userName);
        }
    }

    private static ResolvedCleanupRules ResolveRules(PluginConfiguration configuration, BaseItem item, string? libraryName)
    {
        var mediaKind = GetMediaKind(item);
        var libraryRule = FindMatchingLibraryRule(configuration.LibraryRules, libraryName, item.Path, item is Episode episode ? episode.Series?.Path : null);

        var typeRules = GetGlobalTypeRules(configuration, mediaKind);
        var watchedDays = NormalizeThreshold(typeRules?.DeleteAfterWatchDays ?? CleanupRuleConfiguration.Inherit)
            ?? NormalizeThreshold(configuration.DeleteAfterWatchDays);
        var watchedRuleName = GetMediaKindLabel(mediaKind) + " rules";
        var neverWatchedDays = NormalizeThreshold(typeRules?.DeleteNeverWatchedAfterDays ?? CleanupRuleConfiguration.Inherit)
            ?? NormalizeThreshold(configuration.DeleteNeverWatchedAfterDays);
        var neverWatchedRuleName = GetMediaKindLabel(mediaKind) + " rules";

        if (libraryRule is not null)
        {
            var libraryLabel = "Library rule: " + GetLibraryRuleName(libraryRule);
            ApplyRuleOverrides(libraryRule.DefaultRules, libraryLabel, ref watchedDays, ref watchedRuleName, ref neverWatchedDays, ref neverWatchedRuleName);
            ApplyRuleOverrides(GetLibraryTypeRules(libraryRule, mediaKind), libraryLabel + " > " + GetMediaKindLabel(mediaKind), ref watchedDays, ref watchedRuleName, ref neverWatchedDays, ref neverWatchedRuleName);
        }

        return new ResolvedCleanupRules
        {
            DeleteAfterWatchDays = watchedDays,
            DeleteNeverWatchedAfterDays = neverWatchedDays,
            WatchedRuleName = watchedRuleName,
            NeverWatchedRuleName = neverWatchedRuleName,
        };
    }

    private static void ApplyRuleOverrides(
        CleanupRuleConfiguration? rule,
        string ruleName,
        ref int? watchedDays,
        ref string watchedRuleName,
        ref int? neverWatchedDays,
        ref string neverWatchedRuleName)
    {
        if (rule is null)
        {
            return;
        }

        if (TryResolveThreshold(rule.DeleteAfterWatchDays, out var watchedValue))
        {
            watchedDays = watchedValue;
            watchedRuleName = ruleName;
        }

        if (TryResolveThreshold(rule.DeleteNeverWatchedAfterDays, out var neverWatchedValue))
        {
            neverWatchedDays = neverWatchedValue;
            neverWatchedRuleName = ruleName;
        }
    }

    private static bool TryResolveThreshold(int configuredValue, out int? threshold)
    {
        threshold = null;

        if (configuredValue == CleanupRuleConfiguration.Inherit)
        {
            return false;
        }

        threshold = NormalizeThreshold(configuredValue);
        return true;
    }

    private static int? NormalizeThreshold(int configuredValue)
    {
        return configuredValue switch
        {
            >= 0 => configuredValue,
            -1 => null,
            _ => null,
        };
    }

    private static CleanupRuleConfiguration? GetGlobalTypeRules(PluginConfiguration configuration, CandidateMediaKind mediaKind)
    {
        return mediaKind switch
        {
            CandidateMediaKind.Movie => configuration.MovieRules,
            CandidateMediaKind.Episode => configuration.EpisodeRules,
            _ => configuration.VideoRules,
        };
    }

    private static CleanupRuleConfiguration? GetLibraryTypeRules(LibraryRuleConfiguration libraryRule, CandidateMediaKind mediaKind)
    {
        return mediaKind switch
        {
            CandidateMediaKind.Movie => libraryRule.MovieRules,
            CandidateMediaKind.Episode => libraryRule.EpisodeRules,
            _ => libraryRule.VideoRules,
        };
    }

    private static CandidateMediaKind GetMediaKind(BaseItem item)
    {
        return item switch
        {
            Movie => CandidateMediaKind.Movie,
            Episode => CandidateMediaKind.Episode,
            _ => CandidateMediaKind.Video,
        };
    }

    private static string GetMediaKindLabel(CandidateMediaKind mediaKind)
    {
        return mediaKind switch
        {
            CandidateMediaKind.Movie => "Movies",
            CandidateMediaKind.Episode => "Episodes",
            _ => "Videos",
        };
    }

    private static string GetMediaKindItemType(BaseItem item)
    {
        return GetMediaKind(item) switch
        {
            CandidateMediaKind.Movie => "Movie",
            CandidateMediaKind.Episode => "Episode",
            _ => "Video",
        };
    }

    private static LibraryRuleConfiguration? FindMatchingLibraryRule(
        IEnumerable<LibraryRuleConfiguration>? libraryRules,
        string? libraryName,
        string? itemPath,
        string? seriesPath)
    {
        if (libraryRules is null)
        {
            return null;
        }

        LibraryRuleConfiguration? bestRule = null;
        var bestScore = -1;

        foreach (var libraryRule in libraryRules)
        {
            if (libraryRule is null)
            {
                continue;
            }

            var score = GetLibraryRuleScore(libraryRule, libraryName, itemPath, seriesPath);
            if (score > bestScore)
            {
                bestScore = score;
                bestRule = libraryRule;
            }
        }

        return bestScore >= 0 ? bestRule : null;
    }

    private static int GetLibraryRuleScore(LibraryRuleConfiguration libraryRule, string? libraryName, string? itemPath, string? seriesPath)
    {
        var score = -1;

        if (!string.IsNullOrWhiteSpace(libraryRule.LibraryName)
            && !string.IsNullOrWhiteSpace(libraryName)
            && string.Equals(libraryRule.LibraryName.Trim(), libraryName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score = 1000 + libraryRule.LibraryName.Trim().Length;
        }

        if (!string.IsNullOrWhiteSpace(libraryRule.LibraryPathPrefix))
        {
            var normalizedPrefix = NormalizePath(libraryRule.LibraryPathPrefix);
            if (PathStartsWith(itemPath, normalizedPrefix) || PathStartsWith(seriesPath, normalizedPrefix))
            {
                score = Math.Max(score, 500 + normalizedPrefix.Length);
            }
        }

        return score;
    }

    private static bool PathStartsWith(string? path, string normalizedPrefix)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return NormalizePath(path).StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/');
    }

    private static string GetLibraryRuleName(LibraryRuleConfiguration libraryRule)
    {
        if (!string.IsNullOrWhiteSpace(libraryRule.LibraryName))
        {
            return libraryRule.LibraryName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(libraryRule.LibraryPathPrefix))
        {
            return libraryRule.LibraryPathPrefix.Trim();
        }

        return "Unnamed library rule";
    }

    private static CleanupCandidate BuildCandidate(
        BaseItem item,
        string? libraryName,
        ResolvedCleanupRules resolvedRules,
        CleanupReasonKind reasonKind,
        DateTime? latestPlayed,
        DateTime? addedUtc,
        IReadOnlyList<string> userNames,
        PendingDeletionEntry? pendingEntry)
    {
        var appliedRuleName = reasonKind == CleanupReasonKind.WatchedExceeded
            ? resolvedRules.WatchedRuleName
            : resolvedRules.NeverWatchedRuleName;
        var reason = reasonKind switch
        {
            CleanupReasonKind.WatchedExceeded => latestPlayed.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "Last played on {0:u}; watched retention {1} days ({2})", latestPlayed.Value, resolvedRules.DeleteAfterWatchDays ?? -1, appliedRuleName)
                : string.Format(CultureInfo.InvariantCulture, "Played threshold exceeded by watched retention {0} days ({1})", resolvedRules.DeleteAfterWatchDays ?? -1, appliedRuleName),
            CleanupReasonKind.NeverWatchedExceeded => addedUtc.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "Never played and added on {0:u}; never-watched retention {1} days ({2})", addedUtc.Value, resolvedRules.DeleteNeverWatchedAfterDays ?? -1, appliedRuleName)
                : string.Format(CultureInfo.InvariantCulture, "Never played threshold exceeded by never-watched retention {0} days ({1})", resolvedRules.DeleteNeverWatchedAfterDays ?? -1, appliedRuleName),
            _ => "Cleanup candidate",
        };

        return new CleanupCandidate
        {
            ItemId = item.Id,
            ItemName = item.Name ?? string.Empty,
            ItemType = GetMediaKindItemType(item),
            Path = item.Path,
            LibraryName = libraryName,
            SeriesName = item is Episode episode ? episode.Series?.Name : null,
            SeasonName = item is Episode episodeForSeason ? episodeForSeason.Season?.Name : null,
            ProductionYear = item is Episode episodeForYear ? episodeForYear.Series?.ProductionYear : item.ProductionYear,
            SeasonNumber = item is Episode episodeForNumbers ? episodeForNumbers.ParentIndexNumber : null,
            EpisodeNumber = item is Episode episodeForEpisodeNumber ? episodeForEpisodeNumber.IndexNumber : null,
            SeriesPath = item is Episode episodeForPath ? episodeForPath.Series?.Path : null,
            ReasonKind = reasonKind,
            Reason = reason,
            AppliedRuleName = appliedRuleName,
            EffectiveDeleteAfterWatchDays = resolvedRules.DeleteAfterWatchDays,
            EffectiveDeleteNeverWatchedAfterDays = resolvedRules.DeleteNeverWatchedAfterDays,
            DateAddedUtc = addedUtc,
            LastPlayedDateUtc = latestPlayed,
            PlayedByUsers = userNames.ToArray(),
            TmdbId = item.GetProviderId(MetadataProvider.Tmdb),
            TvdbId = item.GetProviderId(MetadataProvider.Tvdb),
            ImdbId = item.GetProviderId(MetadataProvider.Imdb),
            SeriesTmdbId = item is Episode episodeForTmdb ? episodeForTmdb.Series?.GetProviderId(MetadataProvider.Tmdb) : null,
            SeriesTvdbId = item is Episode episodeForTvdb ? episodeForTvdb.Series?.GetProviderId(MetadataProvider.Tvdb) : null,
            SeriesImdbId = item is Episode episodeForImdb ? episodeForImdb.Series?.GetProviderId(MetadataProvider.Imdb) : null,
            IsPendingDeletion = pendingEntry is not null,
            PendingSinceUtc = pendingEntry?.FirstQualifiedUtc,
            PendingDeleteAfterUtc = pendingEntry?.DeleteAfterUtc,
        };
    }

    private static DateTime? NormalizeUtc(DateTime value)
    {
        if (value == default)
        {
            return null;
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }
}
