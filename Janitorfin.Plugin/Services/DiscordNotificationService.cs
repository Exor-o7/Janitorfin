using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Janitorfin.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Services;

public interface IDiscordNotificationService
{
    Task NotifyGracePeriodItemsAsync(
        PluginConfiguration configuration,
        IReadOnlyList<PendingDeletionEntry> entries,
        IReadOnlyList<PendingDeletionEntry> addedEntries,
        IReadOnlyList<PendingDeletionEntry> removedEntries,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    Task NotifyDeletedItemsAsync(
        PluginConfiguration configuration,
        IReadOnlyList<CleanupCandidate> candidates,
        CancellationToken cancellationToken);
}

internal sealed class DiscordNotificationService : IDiscordNotificationService
{
    private const int MaxMessageLength = 1900;

    private readonly ILogger<DiscordNotificationService> _logger;

    public DiscordNotificationService(ILogger<DiscordNotificationService> logger)
    {
        _logger = logger;
    }

    public async Task NotifyGracePeriodItemsAsync(
        PluginConfiguration configuration,
        IReadOnlyList<PendingDeletionEntry> entries,
        IReadOnlyList<PendingDeletionEntry> addedEntries,
        IReadOnlyList<PendingDeletionEntry> removedEntries,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(addedEntries);
        ArgumentNullException.ThrowIfNull(removedEntries);

        var pendingEntries = entries
            .OrderBy(entry => entry.DeleteAfterUtc)
            .ThenBy(entry => entry.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SeriesName ?? entry.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var messages = BuildPendingMessages(pendingEntries, addedEntries, removedEntries, nowUtc);
        await SendMessagesAsync(configuration, messages, "Discord pending deletion notification", cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyDeletedItemsAsync(
        PluginConfiguration configuration,
        IReadOnlyList<CleanupCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return;
        }

        var messages = BuildDeletedMessages(candidates);
        await SendMessagesAsync(configuration, messages, "Discord deleted media notification", cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMessagesAsync(
        PluginConfiguration configuration,
        IReadOnlyList<string> messages,
        string logContext,
        CancellationToken cancellationToken)
    {
        if (!configuration.EnableDiscordGracePeriodNotifications || messages.Count == 0)
        {
            return;
        }

        if (!TryBuildWebhookUri(configuration.DiscordWebhookUrl, out var webhookUri, out var error))
        {
            _logger.LogWarning("{LogContext} skipped: {Error}", logContext, error);
            return;
        }

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20),
            };

            foreach (var message in messages)
            {
                using var response = await client.PostAsJsonAsync(
                    webhookUri,
                    new DiscordWebhookPayload
                    {
                        Content = message,
                        AllowedMentions = new DiscordAllowedMentions(),
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "{LogContext} failed with HTTP {StatusCode}",
                        logContext,
                        (int)response.StatusCode);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{LogContext} failed.", logContext);
        }
    }

    private static IReadOnlyList<string> BuildPendingMessages(
        IReadOnlyList<PendingDeletionEntry> entries,
        IReadOnlyList<PendingDeletionEntry> addedEntries,
        IReadOnlyList<PendingDeletionEntry> removedEntries,
        DateTime nowUtc)
    {
        if (entries.Count == 0)
        {
            if (removedEntries.Count == 0)
            {
                return ["**Janitorfin pending deletion scan complete**\nNo media is currently in the pending deletion list."];
            }
        }

        var graceCount = entries.Count(entry => entry.DeleteAfterUtc > nowUtc);
        var dueCount = entries.Count - graceCount;
        var endingSoonEntries = entries
            .Where(entry => entry.DeleteAfterUtc > nowUtc && entry.DeleteAfterUtc <= nowUtc.AddDays(3))
            .ToArray();
        var endingSoonCount = endingSoonEntries.Length;
        var header = string.Format(
            CultureInfo.InvariantCulture,
            "**Janitorfin pending deletion scan complete**\n{0} media item{1} in the pending deletion list. {2} added, {3} removed, {4} in grace period, {5} due or overdue.",
            entries.Count,
            entries.Count == 1 ? " is" : "s are",
            addedEntries.Count,
            removedEntries.Count,
            graceCount,
            dueCount);
        if (endingSoonCount > 0)
        {
            header += string.Format(
                CultureInfo.InvariantCulture,
                "\n{0} media item{1} within 3 days of deletion. Watch or favorite anything you want to keep.",
                endingSoonCount,
                endingSoonCount == 1 ? " is" : "s are");
        }

        var messages = new List<string>();
        var builder = new StringBuilder(header);

        AppendSection("Added to pending", BuildDisplayLines(addedEntries, "Added"));
        AppendSection("Full pending list", BuildDisplayLines(entries));
        AppendSection("Within 3 days of deletion", BuildDisplayLines(endingSoonEntries, "3 days or less"));
        AppendSection("Removed from pending", BuildDisplayLines(removedEntries, "Removed"));

        if (messages.Count > 0)
        {
            var footer = string.Format(
                CultureInfo.InvariantCulture,
                "\nEnd of pending deletion scan. {0} grouped pending line{1} shown.",
                BuildDisplayLines(entries).Count(),
                BuildDisplayLines(entries).Count() == 1 ? string.Empty : "s");
            if (builder.Length + footer.Length <= MaxMessageLength)
            {
                builder.Append(footer);
            }
        }

        messages.Add(builder.ToString());
        return messages;

        void AppendSection(string title, IEnumerable<string> lines)
        {
            var lineArray = lines.ToArray();
            if (lineArray.Length == 0)
            {
                return;
            }

            AppendLine(string.Empty);
            AppendLine("**" + title + "**");
            foreach (var line in lineArray)
            {
                AppendLine(line);
            }
        }

        void AppendLine(string line)
        {
            if (builder.Length + line.Length + 1 > MaxMessageLength)
            {
                messages.Add(builder.ToString());
                builder.Clear();
                builder.Append("**Janitorfin pending deletion scan continued**");
            }

            builder.AppendLine();
            builder.Append(line);
        }
    }

    private static IReadOnlyList<string> BuildDeletedMessages(IReadOnlyList<CleanupCandidate> candidates)
    {
        var header = string.Format(
            CultureInfo.InvariantCulture,
            "**Janitorfin deleted media**\n{0} media item{1} deleted.",
            candidates.Count,
            candidates.Count == 1 ? " was" : "s were");
        var messages = new List<string>();
        var displayLines = BuildDeletedDisplayLines(candidates).ToArray();
        var builder = new StringBuilder(header);

        foreach (var line in displayLines)
        {
            if (builder.Length + line.Length + 1 > MaxMessageLength)
            {
                messages.Add(builder.ToString());
                builder.Clear();
                builder.Append("**Janitorfin deleted media continued**");
            }

            builder.AppendLine();
            builder.Append(line);
        }

        if (messages.Count > 0)
        {
            var footer = string.Format(
                CultureInfo.InvariantCulture,
                "\nEnd of deleted media list. {0} grouped line{1} shown.",
                displayLines.Length,
                displayLines.Length == 1 ? string.Empty : "s");
            if (builder.Length + footer.Length <= MaxMessageLength)
            {
                builder.Append(footer);
            }
        }

        messages.Add(builder.ToString());
        return messages;
    }

    private static IEnumerable<string> BuildDisplayLines(IReadOnlyList<PendingDeletionEntry> entries, string? suffix = null)
    {
        var tvLines = entries
            .Where(entry => string.Equals(entry.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => new SeriesGroupKey(
                string.IsNullOrWhiteSpace(entry.SeriesName) ? entry.ItemName : entry.SeriesName!,
                entry.ProductionYear))
            .Select(group => FormatSeriesLine(group.Key, group, suffix))
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase);
        var movieLines = entries
            .Where(entry => !string.Equals(entry.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
            .Select(entry => FormatMovieLine(entry, suffix))
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase);

        return tvLines.Concat(movieLines);
    }

    private static IEnumerable<string> BuildDeletedDisplayLines(IReadOnlyList<CleanupCandidate> candidates)
    {
        var tvLines = candidates
            .Where(candidate => string.Equals(candidate.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
            .GroupBy(candidate => new SeriesGroupKey(
                string.IsNullOrWhiteSpace(candidate.SeriesName) ? candidate.ItemName : candidate.SeriesName!,
                candidate.ProductionYear))
            .Select(group => FormatDeletedSeriesLine(group.Key, group))
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase);
        var movieLines = candidates
            .Where(candidate => !string.Equals(candidate.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
            .Select(FormatDeletedMovieLine)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase);

        return tvLines.Concat(movieLines);
    }

    private static string FormatSeriesLine(SeriesGroupKey key, IEnumerable<PendingDeletionEntry> entries, string? suffix)
    {
        var seasons = entries
            .Select(entry => entry.SeasonNumber)
            .Where(seasonNumber => seasonNumber.HasValue)
            .Select(seasonNumber => seasonNumber!.Value)
            .Distinct()
            .Order()
            .ToArray();
        var seasonText = seasons.Length == 0
            ? "Unknown season"
            : "Season " + string.Join(",", seasons.Select(season => season.ToString(CultureInfo.InvariantCulture)));

        return TrimLine("- TV: " + FormatTitle(key.Title, key.Year) + " - " + seasonText + FormatSuffix(suffix));
    }

    private static string FormatMovieLine(PendingDeletionEntry entry, string? suffix)
    {
        return TrimLine("- " + FormatTitle(entry.ItemName, entry.ProductionYear) + FormatSuffix(suffix));
    }

    private static string FormatDeletedSeriesLine(SeriesGroupKey key, IEnumerable<CleanupCandidate> candidates)
    {
        var seasons = candidates
            .Select(candidate => candidate.SeasonNumber)
            .Where(seasonNumber => seasonNumber.HasValue)
            .Select(seasonNumber => seasonNumber!.Value)
            .Distinct()
            .Order()
            .ToArray();
        var seasonText = seasons.Length == 0
            ? "Unknown season"
            : "Season " + string.Join(",", seasons.Select(season => season.ToString(CultureInfo.InvariantCulture)));

        return TrimLine("- " + FormatTitle(key.Title, key.Year) + " - (" + seasonText + ")");
    }

    private static string FormatDeletedMovieLine(CleanupCandidate candidate)
    {
        return TrimLine("- " + FormatTitle(candidate.ItemName, candidate.ProductionYear));
    }

    private static string FormatSuffix(string? suffix)
    {
        return string.IsNullOrWhiteSpace(suffix) ? string.Empty : " - " + suffix;
    }

    private static string FormatTitle(string title, int? year)
    {
        var normalizedTitle = title.Trim();
        if (!year.HasValue || year.Value <= 0)
        {
            return normalizedTitle;
        }

        var yearSuffix = string.Format(CultureInfo.InvariantCulture, "({0})", year.Value);
        return normalizedTitle.EndsWith(yearSuffix, StringComparison.OrdinalIgnoreCase)
            ? normalizedTitle
            : string.Format(CultureInfo.InvariantCulture, "{0} ({1})", normalizedTitle, year.Value);
    }

    private static string TrimLine(string line)
    {
        return line.Length <= 450 ? line : line[..447] + "...";
    }

    private readonly record struct SeriesGroupKey(string Title, int? Year);

    private static bool TryBuildWebhookUri(string webhookUrl, out Uri? webhookUri, out string error)
    {
        webhookUri = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            error = "Discord webhook URL is required.";
            return false;
        }

        if (!Uri.TryCreate(webhookUrl.Trim(), UriKind.Absolute, out webhookUri))
        {
            error = "Discord webhook URL is invalid.";
            return false;
        }

        if (webhookUri.Scheme != Uri.UriSchemeHttp && webhookUri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Discord webhook URL must use http or https.";
            return false;
        }

        return true;
    }

    private sealed class DiscordWebhookPayload
    {
        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;

        [JsonPropertyName("allowed_mentions")]
        public DiscordAllowedMentions AllowedMentions { get; init; } = new();
    }

    private sealed class DiscordAllowedMentions
    {
        [JsonPropertyName("parse")]
        public IReadOnlyList<string> Parse { get; init; } = Array.Empty<string>();
    }
}
