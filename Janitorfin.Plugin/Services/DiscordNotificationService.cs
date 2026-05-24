using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
    private const int PendingColor = 0x3498db;
    private const int WarningColor = 0xf1c40f;
    private const int DeletedColor = 0xe74c3c;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _stateLock = new();
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

        var messages = BuildPendingMessages(pendingEntries, addedEntries, removedEntries, configuration.PendingDeletionGraceDays, nowUtc);
        await SendMessagesAsync(configuration, messages, "Discord pending deletion notification", cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyDeletedItemsAsync(
        PluginConfiguration configuration,
        IReadOnlyList<CleanupCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(candidates);

        var messages = BuildDeletedMessages(candidates);
        await SendMessagesAsync(configuration, messages, "Discord deleted media notification", cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMessagesAsync(
        PluginConfiguration configuration,
        IReadOnlyList<DiscordMessage> messages,
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
            var state = LoadState();
            var desiredKeys = messages.Select(message => message.Key).ToHashSet(StringComparer.Ordinal);
            var scope = GetDashboardScope(messages);
            var dashboardWebhookUri = webhookUri!;

            foreach (var message in messages)
            {
                state.MessageIds.TryGetValue(message.Key, out var existingMessageId);
                var updated = !string.IsNullOrWhiteSpace(existingMessageId)
                    && await TryUpdateMessageAsync(client, dashboardWebhookUri, existingMessageId, message, logContext, cancellationToken).ConfigureAwait(false);

                if (!updated)
                {
                    var createdMessageId = await TryCreateMessageAsync(client, dashboardWebhookUri, message, logContext, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(createdMessageId))
                    {
                        state.MessageIds[message.Key] = createdMessageId;
                    }
                }
            }

            foreach (var stalePair in state.MessageIds
                .Where(pair => pair.Key.StartsWith(scope, StringComparison.Ordinal) && !desiredKeys.Contains(pair.Key))
                .ToArray())
            {
                await TryDeleteMessageAsync(client, dashboardWebhookUri, stalePair.Value, logContext, cancellationToken).ConfigureAwait(false);
                state.MessageIds.Remove(stalePair.Key);
            }

            SaveState(state);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{LogContext} failed.", logContext);
        }
    }

    private static string GetDashboardScope(IReadOnlyList<DiscordMessage> messages)
    {
        var firstKey = messages.FirstOrDefault()?.Key ?? string.Empty;
        var separatorIndex = firstKey.IndexOf(':', StringComparison.Ordinal);
        return separatorIndex <= 0 ? firstKey : firstKey[..(separatorIndex + 1)];
    }

    private async Task<bool> TryUpdateMessageAsync(HttpClient client, Uri webhookUri, string messageId, DiscordMessage message, string logContext, CancellationToken cancellationToken)
    {
        using var response = await client.PatchAsJsonAsync(
            BuildWebhookMessageUri(webhookUri, messageId),
            CreatePayload(message),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        _logger.LogDebug(
            "{LogContext} could not update Discord dashboard message {MessageKey} ({MessageId}); HTTP {StatusCode}. A new message will be created.",
            logContext,
            message.Key,
            messageId,
            (int)response.StatusCode);
        return false;
    }

    private async Task<string?> TryCreateMessageAsync(HttpClient client, Uri webhookUri, DiscordMessage message, string logContext, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            BuildWebhookCreateUri(webhookUri),
            CreatePayload(message),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "{LogContext} failed with HTTP {StatusCode}",
                logContext,
                (int)response.StatusCode);
            return null;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("id", out var idProperty)
            ? idProperty.GetString()
            : null;
    }

    private async Task TryDeleteMessageAsync(HttpClient client, Uri webhookUri, string messageId, string logContext, CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync(BuildWebhookMessageUri(webhookUri, messageId), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug(
                "{LogContext} could not delete stale Discord dashboard message {MessageId}; HTTP {StatusCode}.",
                logContext,
                messageId,
                (int)response.StatusCode);
        }
    }

    private static DiscordWebhookPayload CreatePayload(DiscordMessage message)
    {
        return new DiscordWebhookPayload
        {
            Content = message.Content,
            Embeds = message.Embeds,
            AllowedMentions = new DiscordAllowedMentions(),
        };
    }

    private static Uri BuildWebhookCreateUri(Uri webhookUri)
    {
        var builder = new UriBuilder(webhookUri);
        var query = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrWhiteSpace(query) ? "wait=true" : query + "&wait=true";
        return builder.Uri;
    }

    private static Uri BuildWebhookMessageUri(Uri webhookUri, string messageId)
    {
        var builder = new UriBuilder(webhookUri)
        {
            Path = webhookUri.AbsolutePath.TrimEnd('/') + "/messages/" + Uri.EscapeDataString(messageId),
        };
        return builder.Uri;
    }

    private DiscordDashboardState LoadState()
    {
        lock (_stateLock)
        {
            var filePath = GetStateFilePath();
            try
            {
                if (!File.Exists(filePath))
                {
                    return new DiscordDashboardState();
                }

                var json = File.ReadAllText(filePath);
                return string.IsNullOrWhiteSpace(json)
                    ? new DiscordDashboardState()
                    : JsonSerializer.Deserialize<DiscordDashboardState>(json, JsonOptions) ?? new DiscordDashboardState();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Janitorfin Discord dashboard state.");
                return new DiscordDashboardState();
            }
        }
    }

    private void SaveState(DiscordDashboardState state)
    {
        lock (_stateLock)
        {
            var filePath = GetStateFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, JsonSerializer.Serialize(state, JsonOptions));
        }
    }

    private static string GetStateFilePath()
    {
        var dataFolderPath = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrWhiteSpace(dataFolderPath))
        {
            throw new InvalidOperationException("Janitorfin data folder path is unavailable.");
        }

        return Path.Combine(dataFolderPath, "discord-dashboard.json");
    }

    private static IReadOnlyList<DiscordMessage> BuildPendingMessages(
        IReadOnlyList<PendingDeletionEntry> entries,
        IReadOnlyList<PendingDeletionEntry> addedEntries,
        IReadOnlyList<PendingDeletionEntry> removedEntries,
        int graceDays,
        DateTime nowUtc)
    {
        var graceCount = entries.Count(entry => entry.DeleteAfterUtc > nowUtc);
        var dueCount = entries.Count - graceCount;
        var endingSoonEntries = entries
            .Where(entry => entry.DeleteAfterUtc > nowUtc && entry.DeleteAfterUtc <= nowUtc.AddDays(3))
            .ToArray();
        var endingSoonCount = endingSoonEntries.Length;
        var messages = new List<DiscordMessage>
        {
            DiscordMessage.FromEmbed("pending:status", BuildPendingEmbed(entries.Count, addedEntries.Count, removedEntries.Count, graceCount, dueCount, endingSoonCount, graceDays, nowUtc)),
        };

        AddPendingChangesEmbeds(messages, addedEntries, endingSoonEntries, removedEntries, nowUtc);
        AddPendingMediaEmbeds(messages, entries, endingSoonCount > 0 ? WarningColor : PendingColor, nowUtc);

        return messages;
    }

    private static void AddPendingChangesEmbeds(ICollection<DiscordMessage> messages, IReadOnlyList<PendingDeletionEntry> addedEntries, IReadOnlyList<PendingDeletionEntry> endingSoonEntries, IReadOnlyList<PendingDeletionEntry> removedEntries, DateTime nowUtc)
    {
        AddTwoOrThreeFieldEmbeds(
            messages,
            "pending:changes",
            "Pending Changes",
            PendingColor,
            nowUtc,
            new DashboardSection("Added", BuildDisplayLines(addedEntries).ToArray()),
            new DashboardSection("Within 3 days", BuildDisplayLines(endingSoonEntries).ToArray()),
            new DashboardSection("Removed", BuildDisplayLines(removedEntries).ToArray()));
    }

    private static void AddPendingMediaEmbeds(ICollection<DiscordMessage> messages, IReadOnlyList<PendingDeletionEntry> entries, int color, DateTime nowUtc)
    {
        AddTwoOrThreeFieldEmbeds(
            messages,
            "pending:media",
            "Pending Media",
            color,
            nowUtc,
            new DashboardSection("Movies", entries
                .Where(entry => !string.Equals(entry.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
                .Select(FormatMovieLine)
                .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
                .ToArray()),
            new DashboardSection("TV", entries
                .Where(entry => string.Equals(entry.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
                .GroupBy(entry => new SeriesGroupKey(
                    string.IsNullOrWhiteSpace(entry.SeriesName) ? entry.ItemName : entry.SeriesName!,
                    entry.ProductionYear))
                .Select(group => FormatSeriesLine(group.Key, group))
                .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
                .ToArray()));
    }

    private static void AddTwoOrThreeFieldEmbeds(ICollection<DiscordMessage> messages, string keyPrefix, string title, int color, DateTime timestampUtc, params DashboardSection[] sections)
    {
        var chunks = ChunkSectionsForEmbed(sections).ToArray();
        for (var index = 0; index < chunks.Length; index++)
        {
            messages.Add(DiscordMessage.FromEmbed(keyPrefix + ":" + index.ToString(CultureInfo.InvariantCulture), new DiscordEmbed
            {
                Title = index == 0 ? title : title + " continued",
                Color = color,
                Fields = chunks[index],
                Timestamp = FormatTimestamp(timestampUtc),
            }));
        }
    }

    private static IEnumerable<IReadOnlyList<DiscordEmbedField>> ChunkSectionsForEmbed(IReadOnlyList<DashboardSection> sections)
    {
        var fields = new List<DiscordEmbedField>();
        foreach (var section in sections)
        {
            foreach (var chunk in ChunkLinesForField(section.Lines))
            {
                if (fields.Count == 25)
                {
                    yield return fields;
                    fields = [];
                }

                fields.Add(new DiscordEmbedField
                {
                    Name = section.Title,
                    Value = chunk,
                    Inline = false,
                });
            }
        }

        if (fields.Count > 0)
        {
            yield return fields;
        }
    }

    private static IEnumerable<string> ChunkLinesForField(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            yield return "None";
            yield break;
        }

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (builder.Length > 0 && builder.Length + line.Length + 1 > 1000)
            {
                yield return builder.ToString().Trim();
                builder.Clear();
            }

            builder.AppendLine(line);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString().Trim();
        }
    }

    private static DiscordEmbed BuildPendingEmbed(int pendingCount, int addedCount, int removedCount, int graceCount, int dueCount, int endingSoonCount, int graceDays, DateTime nowUtc)
    {
        var graceLabel = string.Format(
            CultureInfo.InvariantCulture,
            "In grace {0} day{1}",
            Math.Max(0, graceDays),
            Math.Max(0, graceDays) == 1 ? string.Empty : "s");

        return new DiscordEmbed
        {
            Title = "Current Status",
            Description = endingSoonCount > 0
                ? "Scan Completed: Updated Pending List. Some media is within 3 days of deletion. Watch or favorite anything you want to keep."
                : "Scan Completed: Updated Pending List",
            Color = endingSoonCount > 0 ? WarningColor : PendingColor,
            Fields =
            [
                new DiscordEmbedField { Name = "Pending", Value = pendingCount.ToString(CultureInfo.InvariantCulture), Inline = true },
                new DiscordEmbedField { Name = "Added", Value = addedCount.ToString(CultureInfo.InvariantCulture), Inline = true },
                new DiscordEmbedField { Name = "Removed", Value = removedCount.ToString(CultureInfo.InvariantCulture), Inline = true },
                new DiscordEmbedField { Name = graceLabel, Value = graceCount.ToString(CultureInfo.InvariantCulture), Inline = true },
                new DiscordEmbedField { Name = "Within 3 days", Value = endingSoonCount.ToString(CultureInfo.InvariantCulture), Inline = true },
            ],
            Timestamp = FormatTimestamp(nowUtc),
        };
    }

    private static IReadOnlyList<DiscordMessage> BuildDeletedMessages(IReadOnlyList<CleanupCandidate> candidates)
    {
        var messages = new List<DiscordMessage>();
        AddRecentlyDeletedEmbeds(messages, candidates, DeletedColor);
        return messages;
    }

    private static void AddRecentlyDeletedEmbeds(ICollection<DiscordMessage> messages, IReadOnlyList<CleanupCandidate> candidates, int color)
    {
        var chunks = ChunkSectionsForEmbed(
            [
                new DashboardSection("Movies", candidates
                    .Where(candidate => !string.Equals(candidate.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
                    .Select(FormatDeletedMovieLine)
                    .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
                    .ToArray()),
                new DashboardSection("TV", candidates
                    .Where(candidate => string.Equals(candidate.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(candidate => new SeriesGroupKey(
                        string.IsNullOrWhiteSpace(candidate.SeriesName) ? candidate.ItemName : candidate.SeriesName!,
                        candidate.ProductionYear))
                    .Select(group => FormatDeletedSeriesLine(group.Key, group))
                    .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
                    .ToArray()),
            ]).ToArray();
        for (var index = 0; index < chunks.Length; index++)
        {
            var chunk = chunks[index];
            messages.Add(DiscordMessage.FromEmbed("deleted:recent:" + index.ToString(CultureInfo.InvariantCulture), new DiscordEmbed
            {
                Title = index == 0 ? "Recently Deleted" : "Recently Deleted continued",
                Description = index == 0
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "Deleted {0} media item{1}.",
                        candidates.Count,
                        candidates.Count == 1 ? string.Empty : "s")
                    : string.Empty,
                Fields = chunk,
                Color = color,
                Timestamp = FormatTimestamp(DateTime.UtcNow),
            }));
        }
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

        var episodeCount = entries.Count();
        return TrimLine("- " + FormatTitle(key.Title, key.Year) + " - " + seasonText + " (" + episodeCount.ToString(CultureInfo.InvariantCulture) + " episode" + (episodeCount == 1 ? string.Empty : "s") + ")");
    }

    private static string FormatMovieLine(PendingDeletionEntry entry)
    {
        return TrimLine("- " + FormatTitle(entry.ItemName, entry.ProductionYear));
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

    private static string FormatTimestamp(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return utc.ToString("O", CultureInfo.InvariantCulture);
    }

    private readonly record struct SeriesGroupKey(string Title, int? Year);

    private readonly record struct DashboardSection(string Title, IReadOnlyList<string> Lines);

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
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Content { get; init; }

        [JsonPropertyName("embeds")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<DiscordEmbed>? Embeds { get; init; }

        [JsonPropertyName("allowed_mentions")]
        public DiscordAllowedMentions AllowedMentions { get; init; } = new();
    }

    private sealed class DiscordDashboardState
    {
        public Dictionary<string, string> MessageIds { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class DiscordMessage
    {
        public string Key { get; init; } = string.Empty;

        public string? Content { get; init; }

        public IReadOnlyList<DiscordEmbed>? Embeds { get; init; }

        public static DiscordMessage FromContent(string key, string content)
        {
            return new DiscordMessage { Key = key, Content = content };
        }

        public static DiscordMessage FromEmbed(string key, DiscordEmbed embed)
        {
            return new DiscordMessage { Key = key, Embeds = [embed] };
        }
    }

    private sealed class DiscordEmbed
    {
        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; init; } = string.Empty;

        [JsonPropertyName("color")]
        public int Color { get; init; }

        [JsonPropertyName("fields")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<DiscordEmbedField>? Fields { get; init; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; init; } = string.Empty;
    }

    private sealed class DiscordEmbedField
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; init; } = string.Empty;

        [JsonPropertyName("inline")]
        public bool Inline { get; init; }
    }

    private sealed class DiscordAllowedMentions
    {
        [JsonPropertyName("parse")]
        public IReadOnlyList<string> Parse { get; init; } = Array.Empty<string>();
    }
}
