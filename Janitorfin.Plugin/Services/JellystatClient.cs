using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Janitorfin.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Services;

public interface IJellystatClient
{
    Task<IntegrationTestResult> TestConnectionAsync(PluginConfiguration configuration, CancellationToken cancellationToken);

    Task<JellystatPlaybackSnapshot> GetPlaybackSnapshotAsync(PluginConfiguration configuration, CancellationToken cancellationToken);
}

public sealed class JellystatPlaybackSnapshot
{
    public static JellystatPlaybackSnapshot Empty { get; } = new(new Dictionary<string, List<JellystatPlaybackRecord>>(StringComparer.OrdinalIgnoreCase));

    public JellystatPlaybackSnapshot(IReadOnlyDictionary<string, List<JellystatPlaybackRecord>> recordsByItemId)
    {
        RecordsByItemId = recordsByItemId;
    }

    public IReadOnlyDictionary<string, List<JellystatPlaybackRecord>> RecordsByItemId { get; }

    public bool IsEmpty => RecordsByItemId.Count == 0;
}

public sealed class JellystatPlaybackRecord
{
    public string ItemId { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    public double PlaybackSeconds { get; init; }

    public DateTime? LastPlayedUtc { get; init; }
}

internal sealed class JellystatClient : IJellystatClient
{
    private const int HistoryPageSize = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly ILogger<JellystatClient> _logger;

    public JellystatClient(ILogger<JellystatClient> logger)
    {
        _logger = logger;
    }

    public async Task<IntegrationTestResult> TestConnectionAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!TryBuildBaseUri(configuration.JellystatServerUrl, out var baseUri, out var error))
        {
            return new IntegrationTestResult { Success = false, Message = error };
        }

        if (string.IsNullOrWhiteSpace(configuration.JellystatApiKey))
        {
            return new IntegrationTestResult { Success = false, Message = "Jellystat API key is required." };
        }

        try
        {
            using var client = CreateHttpClient(baseUri!, configuration.JellystatApiKey);
            using var response = await client.GetAsync("api/getHistory?size=1&page=1", cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new IntegrationTestResult
                {
                    Success = false,
                    Message = string.Format(CultureInfo.InvariantCulture, "Jellystat returned HTTP {0}.", (int)response.StatusCode),
                };
            }

            var page = JsonSerializer.Deserialize<JellystatHistoryResponse>(payload, JsonOptions);
            return new IntegrationTestResult
            {
                Success = true,
                Message = string.Format(
                    CultureInfo.InvariantCulture,
                    "Jellystat connection succeeded. Read {0} history row(s).",
                    page?.Results?.Count ?? 0),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellystat connection test failed");
            return new IntegrationTestResult
            {
                Success = false,
                Message = "Jellystat connection test failed: " + ex.Message,
            };
        }
    }

    public async Task<JellystatPlaybackSnapshot> GetPlaybackSnapshotAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!configuration.EnableJellystatIntegration)
        {
            return JellystatPlaybackSnapshot.Empty;
        }

        if (!TryBuildBaseUri(configuration.JellystatServerUrl, out var baseUri, out var error))
        {
            _logger.LogWarning("Jellystat integration skipped: {Error}", error);
            return JellystatPlaybackSnapshot.Empty;
        }

        if (string.IsNullOrWhiteSpace(configuration.JellystatApiKey))
        {
            _logger.LogWarning("Jellystat integration skipped: API key is required.");
            return JellystatPlaybackSnapshot.Empty;
        }

        var maxPages = configuration.JellystatMaxHistoryPages > 0
            ? configuration.JellystatMaxHistoryPages
            : 100;
        var recordsByItemId = new Dictionary<string, List<JellystatPlaybackRecord>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var client = CreateHttpClient(baseUri!, configuration.JellystatApiKey);
            var totalPages = 1;

            for (var pageNumber = 1; pageNumber <= totalPages && pageNumber <= maxPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "api/getHistory?size={0}&page={1}",
                    HistoryPageSize,
                    pageNumber);
                using var response = await client.GetAsync(relativePath, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Jellystat history request failed with HTTP {StatusCode}", (int)response.StatusCode);
                    break;
                }

                var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var history = JsonSerializer.Deserialize<JellystatHistoryResponse>(payload, JsonOptions);
                if (history is null)
                {
                    break;
                }

                totalPages = Math.Max(history.Pages, 1);
                foreach (var row in history.Results)
                {
                    var itemId = GetHistoryItemId(row);
                    if (string.IsNullOrWhiteSpace(itemId))
                    {
                        continue;
                    }

                    var record = new JellystatPlaybackRecord
                    {
                        ItemId = NormalizeJellyfinItemId(itemId),
                        UserName = string.IsNullOrWhiteSpace(row.UserName) ? "Jellystat user" : row.UserName!,
                        PlaybackSeconds = row.TotalDuration ?? row.PlaybackDuration ?? 0,
                        LastPlayedUtc = NormalizeUtc(row.ActivityDateInserted),
                    };

                    if (!recordsByItemId.TryGetValue(record.ItemId, out var records))
                    {
                        records = [];
                        recordsByItemId.Add(record.ItemId, records);
                    }

                    records.Add(record);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Jellystat history sync failed; continuing with Jellyfin user data only.");
            return JellystatPlaybackSnapshot.Empty;
        }

        return new JellystatPlaybackSnapshot(recordsByItemId);
    }

    private static bool TryBuildBaseUri(string serverUrl, out Uri? baseUri, out string error)
    {
        error = string.Empty;
        baseUri = null;

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            error = "Jellystat server URL is required.";
            return false;
        }

        if (!Uri.TryCreate(serverUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out baseUri))
        {
            error = "Jellystat server URL is invalid.";
            return false;
        }

        if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Jellystat server URL must use http or https.";
            return false;
        }

        return true;
    }

    private static HttpClient CreateHttpClient(Uri baseUri, string apiKey)
    {
        var client = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.Add("x-api-token", apiKey.Trim());
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        return client;
    }

    private static string? GetHistoryItemId(JellystatHistoryRow row)
    {
        return !string.IsNullOrWhiteSpace(row.EpisodeId) && !string.Equals(row.EpisodeId, "1", StringComparison.Ordinal)
            ? row.EpisodeId
            : row.NowPlayingItemId;
    }

    internal static string NormalizeJellyfinItemId(string itemId)
    {
        return itemId.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
    }

    private static DateTime? NormalizeUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return null;
        }

        var utcDateTime = parsed.UtcDateTime;
        return utcDateTime.Kind switch
        {
            DateTimeKind.Utc => utcDateTime,
            DateTimeKind.Local => utcDateTime.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc),
        };
    }

    private sealed class JellystatHistoryResponse
    {
        [JsonPropertyName("pages")]
        public int Pages { get; init; } = 1;

        [JsonPropertyName("results")]
        public List<JellystatHistoryRow> Results { get; init; } = [];
    }

    private sealed class JellystatHistoryRow
    {
        public string? NowPlayingItemId { get; init; }

        public string? EpisodeId { get; init; }

        public string? UserName { get; init; }

        public double? PlaybackDuration { get; init; }

        public double? TotalDuration { get; init; }

        public string? ActivityDateInserted { get; init; }
    }
}
