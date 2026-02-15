using Mergician.Entities;
using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ActivityController : ControllerBase
{
    private readonly GitlabCurrentUser _currentUser;
    private readonly GitlabActivityService _activityService;
    private readonly ILogger<ActivityController> _logger;

    public ActivityController(
        GitlabCurrentUser currentUser,
        GitlabActivityService activityService,
        ILogger<ActivityController> logger)
    {
        _currentUser = currentUser;
        _activityService = activityService;
        _logger = logger;
    }

    /// <summary>
    /// Streams branch activity as Server-Sent Events.
    /// Each event is a JSON-serialized BranchActivity record.
    /// Initial records have HasMergeRequest=null (loading state),
    /// followed by updates with full MR/approval data.
    /// A final event with type "done" signals the end of the stream.
    /// </summary>
    [HttpGet("stream")]
    public async Task StreamPushActivity()
    {
        var accessToken = await _currentUser.GetValidAccessToken();
        if (accessToken == null)
        {
            _logger.LogWarning("Unauthorized request to stream activity");
            Response.StatusCode = 401;
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        _logger.LogInformation("Starting SSE activity stream");

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var cancellationToken = HttpContext.RequestAborted;

        try
        {
            await foreach (var activity in _activityService.StreamBranchActivity(_currentUser, cancellationToken))
            {
                var json = JsonSerializer.Serialize(activity, jsonOptions);
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
    /// Returns branch activity for events that occurred since the given time.
    /// Used by the frontend to poll for new activity after the initial SSE stream completes.
    /// </summary>
    [HttpGet("poll")]
    public async Task<IActionResult> PollActivity([FromQuery] DateTime since)
    {
        var accessToken = await _currentUser.GetValidAccessToken();
        if (accessToken == null)
            return Unauthorized();

        _logger.LogInformation("Polling for activity since {Since}", since);

        var results = await _activityService.GetActivitySince(
            _currentUser, since, HttpContext.RequestAborted);

        _logger.LogInformation("Returning {Count} poll results", results.Count);
        return Ok(results);
    }

    /// <summary>
    /// Refreshes MR and approval status for the specified branch-project pairs.
    /// Used by the frontend to update existing dashboard rows without re-fetching all activity.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshActivity([FromBody] List<BranchRefreshRequest> branches)
    {
        var accessToken = await _currentUser.GetValidAccessToken();
        if (accessToken == null)
            return Unauthorized();

        _logger.LogInformation("Refreshing status for {Count} branches", branches.Count);

        var results = await _activityService.RefreshBranchStatus(
            _currentUser, branches, HttpContext.RequestAborted);

        _logger.LogInformation("Returning {Count} refreshed results", results.Count);
        return Ok(results);
    }
}
