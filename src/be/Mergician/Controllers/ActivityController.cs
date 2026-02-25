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

    private readonly UserActivitySyncService _syncService;

    private readonly ILogger<ActivityController> _logger;

    public ActivityController(
        GitlabActivityService activityService,
        GitlabService gitlabService,
        ICoreRepository coreRepository,
        UserActivitySyncService syncService,
        ILogger<ActivityController> logger)
    {
        _activityService = activityService;
        _gitlabService = gitlabService;
        _coreRepository = coreRepository;
        _syncService = syncService;
        _logger = logger;
    }

    /// <summary>
    ///     Returns a diff of the user's dashboard data compared to what the frontend currently shows.
    ///     The frontend sends the branch-project pairs it currently displays, and the backend
    ///     returns branches to add or remove based on the current database state.
    ///     Also ensures the background sync thread is running for this user.
    /// </summary>
    [HttpPost("poll")]
    public async Task<IActionResult> PollDashboard(
        [FromBody] DashboardPollRequest request,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        if (!_coreRepository.IsHealthy())
        {
            _logger.LogError("Database is unhealthy, cannot poll for dashboard data");
            return StatusCode(503, new ErrorResponse("Database is unavailable"));
        }

        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo == null)
        {
            return Unauthorized();
        }

        // Ensure the background sync thread is running (also records poll activity)
        _syncService.EnsureSyncRunning(userInfo.Id, currentUser);

        _logger.LogDebug(
            "Dashboard poll for user {UserId} with {Count} known branches",
            userInfo.Id, request.KnownBranches.Count);

        var result = _activityService.GetDashboardDiff(userInfo.Id, request.KnownBranches);

        _logger.LogDebug(
            "Returning {Added} added, {Removed} removed branches for user {UserId}",
            result.Added.Count, result.Removed.Count, userInfo.Id);

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

        if (!await EnsureDatabaseHealthyForSse(cancellationToken, "refresh activity"))
        {
            return;
        }

        _logger.LogInformation("Starting SSE refresh stream for {Count} branches", branches.Count);

        // Also keep the background sync thread alive during refresh
        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo != null)
        {
            _syncService.EnsureSyncRunning(userInfo.Id, currentUser);
        }

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
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
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
}