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
    /// Returns all branch activity at once (non-streaming).
    /// Used by integration tests and as a fallback.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPushActivity()
    {
        var accessToken = await _currentUser.GetValidAccessToken();
        if (accessToken == null)
            return Unauthorized();

        _logger.LogInformation("Fetching all push activity (non-streaming)");

        var results = new List<BranchActivity>();
        await foreach (var activity in _activityService.StreamBranchActivity(_currentUser))
        {
            // Only keep records that have resolved MR status (skip initial loading records)
            if (activity.HasMergeRequest != null)
            {
                results.Add(activity);
            }
        }

        _logger.LogInformation("Returning {Count} branch activity records", results.Count);
        return Ok(results);
    }
}
