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
        DateTime nowUtc,
        CancellationToken cancellationToken);
}

internal sealed class DiscordNotificationService : IDiscordNotificationService
{
    private const int MaxDetailItems = 25;
    private const int MaxMessageLength = 1900;

    private readonly ILogger<DiscordNotificationService> _logger;

    public DiscordNotificationService(ILogger<DiscordNotificationService> logger)
    {
        _logger = logger;
    }

    public async Task NotifyGracePeriodItemsAsync(
        PluginConfiguration configuration,
        IReadOnlyList<PendingDeletionEntry> entries,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(entries);

        if (!configuration.EnableDiscordGracePeriodNotifications)
        {
            return;
        }

        if (!TryBuildWebhookUri(configuration.DiscordWebhookUrl, out var webhookUri, out var error))
        {
            _logger.LogWarning("Discord grace-period notification skipped: {Error}", error);
            return;
        }

        var graceEntries = entries
            .Where(entry => entry.DeleteAfterUtc > nowUtc)
            .OrderBy(entry => entry.DeleteAfterUtc)
            .ThenBy(entry => entry.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SeriesName ?? entry.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var message = BuildMessage(graceEntries, nowUtc);

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20),
            };

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
                    "Discord grace-period notification failed with HTTP {StatusCode}",
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Discord grace-period notification failed.");
        }
    }

    private static string BuildMessage(IReadOnlyList<PendingDeletionEntry> entries, DateTime nowUtc)
    {
        if (entries.Count == 0)
        {
            return "**Janitorfin grace-period scan complete**\nNo media is currently in the pending deletion grace period.";
        }

        var header = string.Format(
            CultureInfo.InvariantCulture,
            "**Janitorfin grace-period scan complete**\n{0} media item{1} currently in the pending deletion grace period.",
            entries.Count,
            entries.Count == 1 ? " is" : "s are");
        var builder = new StringBuilder(header);

        var displayLines = BuildDisplayLines(entries).ToArray();
        foreach (var line in displayLines.Take(MaxDetailItems))
        {
            if (builder.Length + line.Length + 1 > MaxMessageLength)
            {
                break;
            }

            builder.AppendLine();
            builder.Append(line);
        }

        var hiddenCount = displayLines.Length - Math.Min(displayLines.Length, MaxDetailItems);
        if (hiddenCount > 0)
        {
            var overflowLine = string.Format(
                CultureInfo.InvariantCulture,
                "\n...and {0} more grouped line{1}. Open Janitorfin pending deletions for the full list.",
                hiddenCount,
                hiddenCount == 1 ? string.Empty : "s");

            if (builder.Length + overflowLine.Length <= MaxMessageLength)
            {
                builder.Append(overflowLine);
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> BuildDisplayLines(IReadOnlyList<PendingDeletionEntry> entries)
    {
        var tvLines = entries
            .Where(entry => string.Equals(entry.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => new SeriesGroupKey(
                string.IsNullOrWhiteSpace(entry.SeriesName) ? entry.ItemName : entry.SeriesName!,
                entry.ProductionYear))
            .Select(group => FormatSeriesLine(group.Key, group))
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase);
        var movieLines = entries
            .Where(entry => !string.Equals(entry.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
            .Select(FormatMovieLine)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase);

        return tvLines.Concat(movieLines);
    }

    private static string FormatSeriesLine(SeriesGroupKey key, IEnumerable<PendingDeletionEntry> entries)
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

        return TrimLine("- " + FormatTitle(key.Title, key.Year) + " - (" + seasonText + ")");
    }

    private static string FormatMovieLine(PendingDeletionEntry entry)
    {
        return TrimLine("- " + FormatTitle(entry.ItemName, entry.ProductionYear));
    }

    private static string FormatTitle(string title, int? year)
    {
        return year.HasValue && year.Value > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", title, year.Value)
            : title;
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
