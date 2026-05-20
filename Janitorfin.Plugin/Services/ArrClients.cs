using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Janitorfin.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Services;

public interface IRadarrClient
{
    Task<IntegrationTestResult> TestConnectionAsync(PluginConfiguration configuration, CancellationToken cancellationToken);

    Task<ArrActionResult> UnmonitorMovieAsync(CleanupCandidate candidate, PluginConfiguration configuration, CancellationToken cancellationToken);
}

public interface ISonarrClient
{
    Task<IntegrationTestResult> TestConnectionAsync(PluginConfiguration configuration, CancellationToken cancellationToken);

    Task<ArrActionResult> ApplyMonitoringAsync(CleanupCandidate candidate, PluginConfiguration configuration, CancellationToken cancellationToken);
}

internal abstract class ArrClientBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected ILogger Logger { get; }

    protected ArrClientBase(ILogger logger)
    {
        Logger = logger;
    }

    protected async Task<IntegrationTestResult> TestConnectionInternalAsync(string productName, string serverUrl, string apiKey, CancellationToken cancellationToken)
    {
        if (!TryBuildBaseUri(serverUrl, out var baseUri, out var error))
        {
            return new IntegrationTestResult { Success = false, Message = error };
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new IntegrationTestResult { Success = false, Message = productName + " API key is required." };
        }

        try
        {
            using var client = CreateHttpClient(baseUri!, apiKey);
            using var response = await client.GetAsync("api/v3/system/status", cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new IntegrationTestResult
                {
                    Success = false,
                    Message = string.Format(CultureInfo.InvariantCulture, "{0} returned HTTP {1}.", productName, (int)response.StatusCode),
                };
            }

            string? version = null;
            using (var document = JsonDocument.Parse(payload))
            {
                if (document.RootElement.TryGetProperty("version", out var versionProperty))
                {
                    version = versionProperty.GetString();
                }
            }

            return new IntegrationTestResult
            {
                Success = true,
                Version = version,
                Message = version is null
                    ? productName + " connection succeeded."
                    : string.Format(CultureInfo.InvariantCulture, "{0} connection succeeded. Version {1}.", productName, version),
            };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{ProductName} connection test failed", productName);
            return new IntegrationTestResult
            {
                Success = false,
                Message = productName + " connection test failed: " + ex.Message,
            };
        }
    }

    protected static bool TryBuildBaseUri(string serverUrl, out Uri? baseUri, out string error)
    {
        error = string.Empty;
        baseUri = null;

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            error = "Server URL is required.";
            return false;
        }

        if (!Uri.TryCreate(serverUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out baseUri))
        {
            error = "Server URL is invalid.";
            return false;
        }

        if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Server URL must use http or https.";
            return false;
        }

        return true;
    }

    protected static HttpClient CreateHttpClient(Uri baseUri, string apiKey)
    {
        var client = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey.Trim());
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        return client;
    }

    protected static async Task<List<JsonElement>> GetArrayAsync(HttpClient client, string relativePath, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(relativePath, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
    }

    protected static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    protected static int? GetInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.TryGetInt32(out var value)
                ? value
                : null;
    }

    protected static bool PathEquals(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    protected static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/');
    }

    protected static async Task<HttpResponseMessage> PutJsonAsync(HttpClient client, string relativePath, object body, CancellationToken cancellationToken)
    {
        return await client.PutAsJsonAsync(relativePath, body, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class RadarrClient : ArrClientBase, IRadarrClient
{
    public RadarrClient(ILogger<RadarrClient> logger)
        : base(logger)
    {
    }

    public Task<IntegrationTestResult> TestConnectionAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        return TestConnectionInternalAsync("Radarr", configuration.RadarrServerUrl, configuration.RadarrApiKey, cancellationToken);
    }

    public async Task<ArrActionResult> UnmonitorMovieAsync(CleanupCandidate candidate, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!TryBuildBaseUri(configuration.RadarrServerUrl, out var baseUri, out var error))
        {
            return new ArrActionResult { Success = false, Message = error };
        }

        if (string.IsNullOrWhiteSpace(configuration.RadarrApiKey))
        {
            return new ArrActionResult { Success = false, Message = "Radarr API key is required." };
        }

        using var client = CreateHttpClient(baseUri!, configuration.RadarrApiKey);
        var movies = await GetArrayAsync(client, "api/v3/movie", cancellationToken).ConfigureAwait(false);
        Logger.LogDebug("Fetched {MovieCount} Radarr movies while matching {ItemName} ({ItemId}).", movies.Count, candidate.ItemName, candidate.ItemId);
        var match = movies.FirstOrDefault(movie => MatchesRadarrMovie(movie, candidate));

        if (match.ValueKind == JsonValueKind.Undefined)
        {
            return new ArrActionResult
            {
                Success = false,
                Message = "No matching Radarr movie was found.",
            };
        }

        var radarrId = GetInt32(match, "id");
        if (!radarrId.HasValue)
        {
            return new ArrActionResult { Success = false, Message = "Matching Radarr movie did not expose an id." };
        }

        Logger.LogDebug("Matched {ItemName} ({ItemId}) to Radarr movie id {RadarrId}.", candidate.ItemName, candidate.ItemId, radarrId.Value);
        using var response = await PutJsonAsync(client, "api/v3/movie/editor", new
        {
            movieIds = new[] { radarrId.Value },
            monitored = false,
        }, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new ArrActionResult
            {
                Success = false,
                RemoteId = radarrId,
                Message = string.Format(CultureInfo.InvariantCulture, "Radarr unmonitor failed with HTTP {0}.", (int)response.StatusCode),
            };
        }

        return new ArrActionResult
        {
            Success = true,
            RemoteId = radarrId,
            Message = string.Format(CultureInfo.InvariantCulture, "Radarr movie {0} set to unmonitored.", radarrId.Value),
        };
    }

    private static bool MatchesRadarrMovie(JsonElement movie, CleanupCandidate candidate)
    {
        if (candidate.TmdbId is not null && GetInt32(movie, "tmdbId")?.ToString(CultureInfo.InvariantCulture) == candidate.TmdbId)
        {
            return true;
        }

        if (candidate.ImdbId is not null && string.Equals(GetString(movie, "imdbId"), candidate.ImdbId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (movie.TryGetProperty("movieFile", out var movieFile) && PathEquals(GetString(movieFile, "path"), candidate.Path))
        {
            return true;
        }

        return PathEquals(GetString(movie, "path"), candidate.Path)
            || string.Equals(GetString(movie, "title"), candidate.ItemName, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class SonarrClient : ArrClientBase, ISonarrClient
{
    public SonarrClient(ILogger<SonarrClient> logger)
        : base(logger)
    {
    }

    public Task<IntegrationTestResult> TestConnectionAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        return TestConnectionInternalAsync("Sonarr", configuration.SonarrServerUrl, configuration.SonarrApiKey, cancellationToken);
    }

    public async Task<ArrActionResult> ApplyMonitoringAsync(CleanupCandidate candidate, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!TryBuildBaseUri(configuration.SonarrServerUrl, out var baseUri, out var error))
        {
            return new ArrActionResult { Success = false, Message = error };
        }

        if (string.IsNullOrWhiteSpace(configuration.SonarrApiKey))
        {
            return new ArrActionResult { Success = false, Message = "Sonarr API key is required." };
        }

        using var client = CreateHttpClient(baseUri!, configuration.SonarrApiKey);
        var series = await GetArrayAsync(client, "api/v3/series", cancellationToken).ConfigureAwait(false);
        Logger.LogDebug("Fetched {SeriesCount} Sonarr series while matching {ItemName} ({ItemId}).", series.Count, candidate.ItemName, candidate.ItemId);
        var match = series.FirstOrDefault(show => MatchesSonarrSeries(show, candidate));

        if (match.ValueKind == JsonValueKind.Undefined)
        {
            return new ArrActionResult
            {
                Success = false,
                Message = "No matching Sonarr series was found.",
            };
        }

        var sonarrId = GetInt32(match, "id");
        if (!sonarrId.HasValue)
        {
            return new ArrActionResult { Success = false, Message = "Matching Sonarr series did not expose an id." };
        }

        Logger.LogDebug("Matched {ItemName} ({ItemId}) to Sonarr series id {SonarrId}.", candidate.ItemName, candidate.ItemId, sonarrId.Value);
        return configuration.SonarrUnmonitorScope switch
        {
            SonarrUnmonitorScope.Series => await UnmonitorSeriesAsync(client, sonarrId.Value, cancellationToken).ConfigureAwait(false),
            _ => await UnmonitorSeasonAsync(client, sonarrId.Value, candidate, cancellationToken).ConfigureAwait(false),
        };
    }

    private static async Task<ArrActionResult> UnmonitorSeriesAsync(HttpClient client, int sonarrId, CancellationToken cancellationToken)
    {
        using var response = await PutJsonAsync(client, "api/v3/series/editor", new
        {
            seriesIds = new[] { sonarrId },
            monitored = false,
        }, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new ArrActionResult
            {
                Success = false,
                RemoteId = sonarrId,
                Message = string.Format(CultureInfo.InvariantCulture, "Sonarr unmonitor failed with HTTP {0}.", (int)response.StatusCode),
            };
        }

        return new ArrActionResult
        {
            Success = true,
            RemoteId = sonarrId,
            Message = string.Format(CultureInfo.InvariantCulture, "Sonarr series {0} set to unmonitored.", sonarrId),
        };
    }

    private static async Task<ArrActionResult> UnmonitorSeasonAsync(HttpClient client, int sonarrId, CleanupCandidate candidate, CancellationToken cancellationToken)
    {
        if (!candidate.SeasonNumber.HasValue)
        {
            return new ArrActionResult
            {
                Success = false,
                RemoteId = sonarrId,
                Message = "The cleanup candidate does not include a season number, so Sonarr season monitoring could not be updated.",
            };
        }

        var seriesPath = string.Format(CultureInfo.InvariantCulture, "api/v3/series/{0}", sonarrId);
        using var seriesResponse = await client.GetAsync(seriesPath, cancellationToken).ConfigureAwait(false);
        if (!seriesResponse.IsSuccessStatusCode)
        {
            return new ArrActionResult
            {
                Success = false,
                RemoteId = sonarrId,
                Message = string.Format(CultureInfo.InvariantCulture, "Sonarr series lookup failed with HTTP {0}.", (int)seriesResponse.StatusCode),
            };
        }

        var payload = await seriesResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var seriesObject = JsonNode.Parse(payload)?.AsObject();
        var seasons = seriesObject?["seasons"]?.AsArray();
        var season = seasons?.FirstOrDefault(node =>
            node?["seasonNumber"]?.GetValue<int>() == candidate.SeasonNumber.Value);

        if (seriesObject is null || season is null)
        {
            return new ArrActionResult
            {
                Success = false,
                RemoteId = sonarrId,
                Message = string.Format(CultureInfo.InvariantCulture, "Sonarr series {0} did not expose season {1}.", sonarrId, candidate.SeasonNumber.Value),
            };
        }

        season["monitored"] = false;
        seriesObject["monitored"] = false;

        using var response = await PutJsonAsync(client, seriesPath, seriesObject, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new ArrActionResult
            {
                Success = false,
                RemoteId = sonarrId,
                Message = string.Format(CultureInfo.InvariantCulture, "Sonarr season unmonitor failed with HTTP {0}.", (int)response.StatusCode),
            };
        }

        return new ArrActionResult
        {
            Success = true,
            RemoteId = sonarrId,
            Message = string.Format(CultureInfo.InvariantCulture, "Sonarr series {0} and season {1} set to unmonitored.", sonarrId, candidate.SeasonNumber.Value),
        };
    }

    private static bool MatchesSonarrSeries(JsonElement series, CleanupCandidate candidate)
    {
        if (candidate.SeriesTvdbId is not null && GetInt32(series, "tvdbId")?.ToString(CultureInfo.InvariantCulture) == candidate.SeriesTvdbId)
        {
            return true;
        }

        if (candidate.SeriesTmdbId is not null && GetInt32(series, "tmdbId")?.ToString(CultureInfo.InvariantCulture) == candidate.SeriesTmdbId)
        {
            return true;
        }

        if (candidate.SeriesImdbId is not null && string.Equals(GetString(series, "imdbId"), candidate.SeriesImdbId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return PathEquals(GetString(series, "path"), candidate.SeriesPath)
            || string.Equals(GetString(series, "title"), candidate.SeriesName, StringComparison.OrdinalIgnoreCase);
    }

}