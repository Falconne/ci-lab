using Mergician.Entities;
using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ActivityController : ControllerBase
{
    private readonly GitlabUserFactory _userFactory;
    private readonly GitlabActivityService _activityService;
    private readonly ILogger<ActivityController> _logger;

    public ActivityController(
        GitlabUserFactory userFactory,
        GitlabActivityService activityService,
        ILogger<ActivityController> logger)
    {
        _userFactory = userFactory;
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
    public async Task StreamPushActivity(CancellationToken cancellationToken)
    {
        var currentUser = await _userFactory.GetCurrentUser();
        if (currentUser == null)
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

        try
        {
            await foreach (var activity in _activityService.StreamBranchActivity(currentUser, cancellationToken))
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
    public async Task<IActionResult> PollActivity([FromQuery] DateTime since, CancellationToken cancellationToken)
    {
        var currentUser = await _userFactory.GetCurrentUser();
        if (currentUser == null)
            return Unauthorized();

        _logger.LogInformation("Polling for activity since {Since}", since);

        var results = await _activityService.GetActivitySince(
            currentUser, since, cancellationToken);

        _logger.LogInformation("Returning {Count} poll results", results.Count);
        return Ok(results);
    }

    /// <summary>
    /// Streams refreshed MR and approval status for the specified branch-project pairs
    /// as Server-Sent Events. Each event is a JSON-serialized BranchActivity record.
    /// A final event with type "done" signals the end of the stream.
    /// </summary>
    [HttpPost("refresh")]
    public async Task RefreshActivity([FromBody] List<BranchRefreshRequest> branches, CancellationToken cancellationToken)
    {
        var currentUser = await _userFactory.GetCurrentUser();
        if (currentUser == null)
        {
            _logger.LogWarning("Unauthorized request to refresh activity");
            Response.StatusCode = 401;
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        _logger.LogInformation("Starting SSE refresh stream for {Count} branches", branches.Count);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        try
        {
            await foreach (var activity in _activityService.StreamRefreshBranchStatus(
                               currentUser, branches, cancellationToken))
            {
                var json = JsonSerializer.Serialize(activity, jsonOptions);
                await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
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
