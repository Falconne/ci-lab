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
    public async Task StreamPushActivity(
        [FromQuery] string? lastPollTime,
        CancellationToken cancellationToken)
    {
        var requestReceivedAt = DateTimeOffset.UtcNow;
        var currentUser = HttpContext.GetGitlabUser();

        if (!TryParseLastPollTime(lastPollTime, out var parsedLastPollTime, out var parseError))
        {
            Response.StatusCode = 400;
            await Response.WriteAsJsonAsync(new ErrorResponse(parseError!), cancellationToken);
            return;
        }

        if (!await EnsureDatabaseHealthyForSse(cancellationToken, "stream activity"))
        {
            return;
        }

        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo == null)
        {
            _logger.LogWarning("Could not resolve GitLab user for stream");
            Response.StatusCode = 401;
            return;
        }

        _logger.LogInformation("Starting SSE activity stream for user {UserId}", userInfo.Id);

        await StreamSse(
            "activity",
            async (streamToken, writeLock) =>
            {
                await WriteSseEvent(
                    new ActivityPollCursor(requestReceivedAt),
                    streamToken,
                    writeLock,
                    "poll-cursor");

                await foreach (var activity in _activityService.StreamBranchActivity(
                                   currentUser,
                                   userInfo.Id,
                                   parsedLastPollTime,
                                   streamToken))
                {
                    await WriteSseEvent(activity, streamToken, writeLock);
                }
            },
            cancellationToken);
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

        if (!await EnsureDatabaseHealthyForSse(cancellationToken, "refresh activity"))
        {
            return;
        }

        _logger.LogInformation("Starting SSE refresh stream for {Count} branches", branches.Count);

        await StreamSse(
            "refresh",
            async (streamToken, writeLock) =>
            {
                await foreach (var item in _activityService.StreamRefreshBranchStatus(
                                   currentUser,
                                   branches,
                                   streamToken))
                {
                    if (item is BranchDeletedNotification deleted)
                    {
                        await WriteSseEvent(deleted, streamToken, writeLock, "deleted");
                    }
                    else
                    {
                        await WriteSseEvent(item, streamToken, writeLock);
                    }
                }
            },
            cancellationToken);
    }

    /// <summary>
    ///     Returns branch activity for events that occurred since the given time.
    ///     Used by the frontend to poll for new activity after the initial SSE stream completes.
    /// </summary>
    [HttpGet("poll")]
    public async Task<IActionResult> PollActivity(
        [FromQuery] string? lastPollTime,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        if (string.IsNullOrWhiteSpace(lastPollTime))
        {
            _logger.LogWarning("Poll request rejected: missing required lastPollTime query value");
            return BadRequest(new ErrorResponse("Missing required 'lastPollTime' query value."));
        }

        if (!TryParseLastPollTime(lastPollTime, out var parsedLastPollTime, out var parseError))
        {
            return BadRequest(new ErrorResponse(parseError!));
        }

        if (!_coreRepository.IsHealthy())
        {
            _logger.LogError("Database is unhealthy, cannot poll for activity");
            return StatusCode(503, new ErrorResponse("Database is unavailable"));
        }

        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo == null)
        {
            return Unauthorized();
        }

        _logger.LogInformation("Polling for activity for user {UserId}", userInfo.Id);

        var result = await _activityService.GetPolledActivitySince(
            currentUser,
            userInfo.Id,
            parsedLastPollTime!.Value,
            cancellationToken);

        _logger.LogInformation("Returning {Count} poll results", result.Activities.Count);
        return Ok(result);
    }

    /// <summary>
    ///     Returns fully resolved details for a single merge group.
    /// </summary>
    [HttpGet("merge-groups/{mergeGroupId:int}")]
    public async Task<IActionResult> GetMergeGroupDetails(
        int mergeGroupId,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        if (!_coreRepository.IsHealthy())
        {
            _logger.LogError(
                "Database is unhealthy, cannot fetch merge group details for merge group {MergeGroupId}",
                mergeGroupId);

            return StatusCode(503, new ErrorResponse("Database is unavailable"));
        }

        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo == null)
        {
            return Unauthorized();
        }

        _logger.LogInformation(
            "Fetching merge group details for user {UserId}, merge group {MergeGroupId}",
            userInfo.Id,
            mergeGroupId);

        var details = await _activityService.GetMergeGroupDetails(
            currentUser,
            userInfo.Id,
            mergeGroupId,
            cancellationToken);

        if (details == null)
        {
            return NotFound(new ErrorResponse("Merge group not found"));
        }

        return Ok(details);
    }

    private async Task<bool> EnsureDatabaseHealthyForSse(
        CancellationToken cancellationToken,
        string operationName)
    {
        if (_coreRepository.IsHealthy())
        {
            return true;
        }

        _logger.LogError("Database is unhealthy, cannot {OperationName}", operationName);
        Response.StatusCode = 503;
        await Response.WriteAsync("{\"error\":\"Database is unavailable\"}", cancellationToken);
        return false;
    }

    private async Task StreamSse(
        string streamName,
        Func<CancellationToken, SemaphoreSlim, Task> streamWriter,
        CancellationToken cancellationToken)
    {
        ConfigureSseHeaders();

        using var writeLock = new SemaphoreSlim(1, 1);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = RunHeartbeat(heartbeatCts.Token, writeLock);

        try
        {
            await streamWriter(cancellationToken, writeLock);
            // ReSharper disable once PossiblyMistakenUseOfCancellationToken
            await WriteSseRaw("event: done\ndata: {}\n\n", cancellationToken, writeLock);

            _logger.LogInformation("SSE {StreamName} stream completed", streamName);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SSE {StreamName} stream cancelled by client", streamName);
        }
        finally
        {
            await heartbeatCts.CancelAsync();
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

    private void ConfigureSseHeaders()
    {
        // Required MIME type so browsers parse response frames as Server-Sent Events.
        Response.Headers.ContentType = "text/event-stream";
        // Prevent proxies and browsers from caching event data.
        Response.Headers.CacheControl = "no-cache";
        // Keep the HTTP connection open for incremental event delivery.
        Response.Headers.Connection = "keep-alive";
        // Disable NGINX buffering so each event is flushed to the client immediately.
        Response.Headers["X-Accel-Buffering"] = "no";
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

    private bool TryParseLastPollTime(
        string? lastPollTime,
        out DateTimeOffset? parsedLastPollTime,
        out string? error)
    {
        parsedLastPollTime = null;
        error = null;

        if (string.IsNullOrWhiteSpace(lastPollTime))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(lastPollTime, out var parsed))
        {
            parsedLastPollTime = parsed.ToUniversalTime();
            return true;
        }

        _logger.LogWarning("Invalid lastPollTime query value '{LastPollTime}'", lastPollTime);
        error = "Invalid 'lastPollTime' query value. Use an ISO-8601 timestamp.";
        return false;
    }
}