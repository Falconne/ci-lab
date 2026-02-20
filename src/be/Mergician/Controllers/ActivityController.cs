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

        _logger.LogInformation("Starting SSE activity stream for user {UserId}", userInfo.Id);

        try
        {
            await foreach (var activity in _activityService.StreamBranchActivity(
                               currentUser,
                               userInfo.Id,
                               cancellationToken))
            {
                var json = JsonSerializer.Serialize(activity, _jsonOptions);
                await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }

            // Signal the end of the stream
            await Response.WriteAsync("event: done\ndata: {}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            _logger.LogInformation("SSE activity stream completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SSE activity stream cancelled by client");
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

        _logger.LogInformation("Starting SSE refresh stream for {Count} branches", branches.Count);

        try
        {
            await foreach (var item in _activityService.StreamRefreshBranchStatus(
                               currentUser,
                               branches,
                               cancellationToken))
            {
                string json;
                if (item is BranchDeletedNotification deleted)
                {
                    json = JsonSerializer.Serialize(deleted, _jsonOptions);
                    await Response.WriteAsync($"event: deleted\ndata: {json}\n\n", cancellationToken);
                }
                else
                {
                    json = JsonSerializer.Serialize(item, _jsonOptions);
                    await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                }

                await Response.Body.FlushAsync(cancellationToken);
            }

            await Response.WriteAsync("event: done\ndata: {}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            _logger.LogInformation("SSE refresh stream completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SSE refresh stream cancelled by client");
        }
    }
}