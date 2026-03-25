using System.Threading;
using System.Threading.Tasks;
using Janitorfin.Plugin.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Janitorfin.Plugin.Controllers;

[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("Janitorfin")]
public class JanitorfinController : ControllerBase
{
    private readonly CleanupEvaluationService _cleanupEvaluationService;
    private readonly CleanupExecutionService _cleanupExecutionService;
    private readonly IRadarrClient _radarrClient;
    private readonly ISonarrClient _sonarrClient;

    public JanitorfinController(
        CleanupEvaluationService cleanupEvaluationService,
        CleanupExecutionService cleanupExecutionService,
        IRadarrClient radarrClient,
        ISonarrClient sonarrClient)
    {
        _cleanupEvaluationService = cleanupEvaluationService;
        _cleanupExecutionService = cleanupExecutionService;
        _radarrClient = radarrClient;
        _sonarrClient = sonarrClient;
    }

    [HttpGet("Preview")]
    public Task<CleanupEvaluationSummary> Preview(CancellationToken cancellationToken)
    {
        return _cleanupEvaluationService.EvaluateAsync(Plugin.Instance!.Configuration, cancellationToken);
    }

    [HttpPost("Execute")]
    public Task<CleanupExecutionSummary> Execute([FromQuery] bool? dryRun, CancellationToken cancellationToken)
    {
        return _cleanupExecutionService.ExecuteAsync(Plugin.Instance!.Configuration, dryRun, cancellationToken);
    }

    [HttpPost("Test/Radarr")]
    public Task<IntegrationTestResult> TestRadarr(CancellationToken cancellationToken)
    {
        return _radarrClient.TestConnectionAsync(Plugin.Instance!.Configuration, cancellationToken);
    }

    [HttpPost("Test/Sonarr")]
    public Task<IntegrationTestResult> TestSonarr(CancellationToken cancellationToken)
    {
        return _sonarrClient.TestConnectionAsync(Plugin.Instance!.Configuration, cancellationToken);
    }
}