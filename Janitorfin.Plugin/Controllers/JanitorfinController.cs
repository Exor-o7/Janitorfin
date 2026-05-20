using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Janitorfin.Plugin.Configuration;
using Janitorfin.Plugin.Services;
using Janitorfin.Plugin.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
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
    private readonly IJellystatClient _jellystatClient;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<JanitorfinController> _logger;

    public JanitorfinController(
        CleanupEvaluationService cleanupEvaluationService,
        CleanupExecutionService cleanupExecutionService,
        PendingDeletionQueueService pendingDeletionQueueService,
        IRadarrClient radarrClient,
        ISonarrClient sonarrClient,
        IJellystatClient jellystatClient,
        ITaskManager taskManager,
        ILogger<JanitorfinController> logger)
    {
        _cleanupEvaluationService = cleanupEvaluationService;
        _cleanupExecutionService = cleanupExecutionService;
        _pendingDeletionQueueService = pendingDeletionQueueService;
        _radarrClient = radarrClient;
        _sonarrClient = sonarrClient;
        _jellystatClient = jellystatClient;
        _taskManager = taskManager;
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

    [HttpPost("ScanPending")]
    public async Task<ActionResult<CleanupExecutionSummary>> ScanPendingSavedConfiguration([FromQuery] bool? dryRun, CancellationToken cancellationToken)
    {
        try
        {
            return await _cleanupExecutionService.ScanAndQueuePendingAsync(Plugin.Instance!.Configuration, dryRun, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            return CreateErrorResult(ex, "Scan pending with saved configuration failed.");
        }
    }

    [HttpPost("DeleteDuePending")]
    public async Task<ActionResult<CleanupExecutionSummary>> DeleteDuePendingSavedConfiguration([FromQuery] bool? dryRun, CancellationToken cancellationToken)
    {
        try
        {
            return await _cleanupExecutionService.DeleteDuePendingAsync(Plugin.Instance!.Configuration, dryRun, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            return CreateErrorResult(ex, "Delete due pending with saved configuration failed.");
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

    [HttpPost("Tasks/ScanPending/Run")]
    public ActionResult<CleanupTaskStartResult> RunScanPendingTask()
    {
        return RunTask(
            task => task.ScheduledTask is ScanPendingDeletionTask
                || string.Equals(task.ScheduledTask.Key, "JanitorfinScanPending", StringComparison.OrdinalIgnoreCase)
                || string.Equals(task.Name, "Janitorfin Scan Pending Deletions", StringComparison.OrdinalIgnoreCase),
            "Janitorfin scan pending task is not available.",
            "Run scan pending task failed.",
            "Janitorfin pending scan");
    }

    [HttpPost("Tasks/DeleteDuePending/Run")]
    public ActionResult<CleanupTaskStartResult> RunDeleteDuePendingTask()
    {
        return RunTask(
            task => task.ScheduledTask is DeleteDuePendingTask
                || string.Equals(task.ScheduledTask.Key, "JanitorfinDeleteDuePending", StringComparison.OrdinalIgnoreCase)
                || string.Equals(task.Name, "Janitorfin Delete Due Pending Items", StringComparison.OrdinalIgnoreCase),
            "Janitorfin delete due pending task is not available.",
            "Run delete due pending task failed.",
            "Janitorfin delete due pending");
    }

    [HttpPost("Tasks/Cleanup/Run")]
    public ActionResult<CleanupTaskStartResult> RunCleanupTask()
    {
        return RunDeleteDuePendingTask();
    }

    private ActionResult<CleanupTaskStartResult> RunTask(Func<IScheduledTaskWorker, bool> predicate, string unavailableMessage, string errorContext, string actionName)
    {
        try
        {
            var task = _taskManager.ScheduledTasks.FirstOrDefault(predicate);

            if (task is null)
            {
                return StatusCode(
                    500,
                    new
                    {
                        message = unavailableMessage,
                        context = errorContext,
                    });
            }

            if (task.State is TaskState.Running or TaskState.Cancelling)
            {
                return CreateCleanupTaskStartResult(task, actionName, started: false, alreadyRunning: true);
            }

            _ = _taskManager.Execute(task, new TaskOptions());

            return CreateCleanupTaskStartResult(task, actionName, started: true, alreadyRunning: false);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(ex, errorContext);
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

    [HttpPost("Test/Jellystat")]
    public Task<IntegrationTestResult> TestJellystat(CancellationToken cancellationToken)
    {
        return _jellystatClient.TestConnectionAsync(Plugin.Instance!.Configuration, cancellationToken);
    }

    private static CleanupTaskStartResult CreateCleanupTaskStartResult(IScheduledTaskWorker task, string actionName, bool started, bool alreadyRunning)
    {
        var configuration = Plugin.Instance?.Configuration;
        var dryRun = configuration?.DryRun ?? true;
        var stateText = task.State.ToString();
        var message = started
            ? dryRun
                ? actionName + " dry run was started. Check Dashboard > Scheduled Tasks for progress."
                : actionName + " was started. Check Dashboard > Scheduled Tasks for progress."
            : alreadyRunning
                ? dryRun
                    ? actionName + " dry run is already running. Check Dashboard > Scheduled Tasks for progress."
                    : actionName + " is already running. Check Dashboard > Scheduled Tasks for progress."
                : actionName + " task state is unchanged.";

        return new CleanupTaskStartResult
        {
            Started = started,
            AlreadyRunning = alreadyRunning,
            DryRun = dryRun,
            TaskId = task.Id,
            TaskName = task.Name,
            TaskState = stateText,
            CurrentProgress = task.CurrentProgress,
            Message = message,
        };
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