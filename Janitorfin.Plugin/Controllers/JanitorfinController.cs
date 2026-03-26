using System.Threading;
using System.Threading.Tasks;
using Janitorfin.Plugin.Configuration;
using Janitorfin.Plugin.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Janitorfin.Plugin.Controllers;

[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("Janitorfin")]
public class JanitorfinController : ControllerBase
{
    private readonly CleanupEvaluationService _cleanupEvaluationService;
    private readonly CleanupExecutionService _cleanupExecutionService;
    private readonly PendingDeletionQueueService _pendingDeletionQueueService;
    private readonly IRadarrClient _radarrClient;
    private readonly ISonarrClient _sonarrClient;
    private readonly ILogger<JanitorfinController> _logger;

    public JanitorfinController(
        CleanupEvaluationService cleanupEvaluationService,
        CleanupExecutionService cleanupExecutionService,
        PendingDeletionQueueService pendingDeletionQueueService,
        IRadarrClient radarrClient,
        ISonarrClient sonarrClient,
        ILogger<JanitorfinController> logger)
    {
        _cleanupEvaluationService = cleanupEvaluationService;
        _cleanupExecutionService = cleanupExecutionService;
        _pendingDeletionQueueService = pendingDeletionQueueService;
        _radarrClient = radarrClient;
        _sonarrClient = sonarrClient;
        _logger = logger;
    }

    [HttpGet("Preview")]
    public async Task<ActionResult<CleanupEvaluationSummary>> Preview(CancellationToken cancellationToken)
    {
        try
        {
            return await _cleanupEvaluationService.EvaluateAsync(
                Plugin.Instance!.Configuration,
                cancellationToken,
                CleanupEvaluationService.DefaultPreviewCandidateDetailLimit).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            return CreateErrorResult(ex, "Preview with saved configuration failed.");
        }
    }

    [HttpPost("Preview/WithConfiguration")]
    public async Task<ActionResult<CleanupEvaluationSummary>> PreviewWithConfiguration([FromBody] PluginConfiguration? configuration, CancellationToken cancellationToken)
    {
        try
        {
            return await _cleanupEvaluationService.EvaluateAsync(
                configuration ?? Plugin.Instance!.Configuration,
                cancellationToken,
                CleanupEvaluationService.DefaultPreviewCandidateDetailLimit).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            return CreateErrorResult(ex, "Preview with posted configuration failed.");
        }
    }

    [HttpPost("Execute")]
    public async Task<ActionResult<CleanupExecutionSummary>> ExecuteSavedConfiguration([FromQuery] bool? dryRun, CancellationToken cancellationToken)
    {
        try
        {
            return await _cleanupExecutionService.ExecuteAsync(Plugin.Instance!.Configuration, dryRun, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            return CreateErrorResult(ex, "Execution with saved configuration failed.");
        }
    }

    [HttpPost("Execute/WithConfiguration")]
    public async Task<ActionResult<CleanupExecutionSummary>> ExecuteWithConfiguration([FromQuery] bool? dryRun, [FromBody] PluginConfiguration? configuration, CancellationToken cancellationToken)
    {
        try
        {
            return await _cleanupExecutionService.ExecuteAsync(configuration ?? Plugin.Instance!.Configuration, dryRun, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            return CreateErrorResult(ex, "Execution with posted configuration failed.");
        }
    }

    [HttpGet("Pending")]
    public ActionResult<PendingDeletionSummary> Pending()
    {
        return _pendingDeletionQueueService.GetSummary(Plugin.Instance!.Configuration, PendingDeletionQueueService.DefaultPendingDetailLimit);
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

    private ActionResult CreateErrorResult(System.Exception ex, string context)
    {
        _logger.LogError(ex, "{Context}", context);

        return StatusCode(
            500,
            new
            {
                message = ex.Message,
                detail = ex.ToString(),
                context,
            });
    }
}