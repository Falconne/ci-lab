using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Mergician.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ActivityController : ControllerBase
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly GitlabActivityService _activityService;

    private readonly ICoreRepository _coreRepository;

    private readonly GitlabService _gitlabService;

    private readonly ILogger<ActivityController> _logger;

    public ActivityController(
        GitlabActivityService activityService,
        GitlabService gitlabService,
        ICoreRepository coreRepository,
        ILogger<ActivityController> logger)
    {
        _activityService = activityService;
        _gitlabService = gitlabService;
        _coreRepository = coreRepository;
        _logger = logger;
    }

    /// <summary>
    ///     Streams branch activity as Server-Sent Events.
    ///     Each event is a JSON-serialized BranchActivity record.
    ///     Initial records have HasMergeRequest=null (loading state),
    ///     followed by updates with full MR/approval data.
    ///     A final event with type "done" signals the end of the stream.
    /// </summary>
    [HttpGet("stream")]
    public async Task StreamPushActivity(CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        if (!_coreRepository.IsHealthy())
        {
            _logger.LogError("Database is unhealthy, cannot stream activity");
            Response.StatusCode = 503;
            await Response.WriteAsync("{\"error\":\"Database is unavailable\"}", cancellationToken);
            return;
        }

        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo == null)
        {
            _logger.LogWarning("Could not resolve GitLab user for stream");
            Response.StatusCode = 401;
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        _logger.LogInformation("Starting SSE activity stream for user {UserId}", userInfo.Id);

        using var writeLock = new SemaphoreSlim(1, 1);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = RunHeartbeat(heartbeatCts.Token, writeLock);

        try
        {
            await foreach (var activity in _activityService.StreamBranchActivity(
                               currentUser,
                               userInfo.Id,
                               cancellationToken))
            {
                await WriteSseEvent(activity, cancellationToken, writeLock);
            }

            // Signal the end of the stream
            await WriteSseRaw("event: done\ndata: {}\n\n", cancellationToken, writeLock);
            _logger.LogInformation("SSE activity stream completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SSE activity stream cancelled by client");
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation
            }
        }
    }

    /// <summary>
    ///     Returns branch activity for events that occurred since the given time.
    ///     Used by the frontend to poll for new activity after the initial SSE stream completes.
    /// </summary>
    [HttpGet("poll")]
    public async Task<IActionResult> PollActivity(
        [FromQuery] DateTime since,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        if (!_coreRepository.IsHealthy())
        {
            _logger.LogError("Database is unhealthy, cannot poll for activity");
            return StatusCode(503, new { error = "Database is unavailable" });
        }

        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo == null)
        {
            return Unauthorized();
        }

        _logger.LogInformation("Polling for activity for user {UserId} since {Since}", userInfo.Id, since);

        var result = await _activityService.GetActivitySince(
            currentUser,
            userInfo.Id,
            since,
            cancellationToken);

        _logger.LogInformation("Returning {Count} poll results", result.Activities.Count);
        return Ok(result);
    }

    /// <summary>
    ///     Streams refreshed MR and approval status for the specified branch-project pairs
    ///     as Server-Sent Events. Each event is a JSON-serialized BranchActivity or BranchDeletedNotification.
    ///     A final event with type "done" signals the end of the stream.
    /// </summary>
    [HttpPost("refresh")]
    public async Task RefreshActivity(
        [FromBody] List<BranchRefreshRequest> branches,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        if (!_coreRepository.IsHealthy())
        {
            _logger.LogError("Database is unhealthy, cannot refresh activity");
            Response.StatusCode = 503;
            await Response.WriteAsync("{\"error\":\"Database is unavailable\"}", cancellationToken);
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        _logger.LogInformation("Starting SSE refresh stream for {Count} branches", branches.Count);

        using var writeLock = new SemaphoreSlim(1, 1);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = RunHeartbeat(heartbeatCts.Token, writeLock);

        try
        {
            await foreach (var item in _activityService.StreamRefreshBranchStatus(
                               currentUser,
                               branches,
                               cancellationToken))
            {
                if (item is BranchDeletedNotification deleted)
                {
                    await WriteSseEvent(deleted, cancellationToken, writeLock, "deleted");
                }
                else
                {
                    await WriteSseEvent(item, cancellationToken, writeLock);
                }
            }

            await WriteSseRaw("event: done\ndata: {}\n\n", cancellationToken, writeLock);
            _logger.LogInformation("SSE refresh stream completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SSE refresh stream cancelled by client");
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation
            }
        }
    }

    private async Task RunHeartbeat(CancellationToken cancellationToken, SemaphoreSlim writeLock)
    {
        using var timer = new PeriodicTimer(_heartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await WriteSseRaw(": heartbeat\n\n", cancellationToken, writeLock);
        }
    }

    private async Task WriteSseEvent(
        object payload,
        CancellationToken cancellationToken,
        SemaphoreSlim writeLock,
        string? eventName = null)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var frame = eventName == null
            ? $"data: {json}\n\n"
            : $"event: {eventName}\ndata: {json}\n\n";

        await WriteSseRaw(frame, cancellationToken, writeLock);
    }

    private async Task WriteSseRaw(string frame, CancellationToken cancellationToken, SemaphoreSlim writeLock)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await Response.WriteAsync(frame, cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }
}