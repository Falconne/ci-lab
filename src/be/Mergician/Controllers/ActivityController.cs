using Mergician.Entities;
using Mergician.Services;
using Mergician.Services.Authentication;
using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ActivityController : ControllerBase
{
    private readonly GitlabActivityService _activityService;

    private readonly GitlabService _gitlabService;

    private readonly ILogger<ActivityController> _logger;

    private readonly SseService _sseService;

    private readonly UserActivitySyncService _syncService;

    public ActivityController(
        GitlabActivityService activityService,
        GitlabService gitlabService,
        SseService sseService,
        UserActivitySyncService syncService,
        ILogger<ActivityController> logger)
    {
        _activityService = activityService;
        _gitlabService = gitlabService;
        _sseService = sseService;
        _syncService = syncService;
        _logger = logger;
    }

    // TODO: Rename the endpoint to refresh-branches and the method to RefreshBranches. Update the frontend callers.
    [HttpPost("poll")]
    public async Task<IActionResult> PollDashboard(
        [FromBody] DashboardPollRequest request,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo == null)
        {
            return Unauthorized();
        }

        // Ensure the background sync thread is running (also records that user is still active)
        _syncService.EnsureSyncRunning(userInfo.Id, currentUser);

        _logger.LogDebug(
            "Dashboard poll for user {UserId} with {Count} known branches",
            userInfo.Id,
            request.KnownBranches.Count);

        var result = _activityService.GetDashboardDiff(userInfo.Id, request.KnownBranches);

        _logger.LogDebug(
            "Returning {Added} added, {Removed} removed branches for user {UserId}",
            result.Added.Count,
            result.Removed.Count,
            userInfo.Id);

        return Ok(result);
    }

    // TODO: Rename the endpoint to refresh-activity. Update the frontend callers.
    [HttpPost("refresh")]
    public async Task RefreshActivity(
        [FromBody] List<BranchRefreshRequest> branches,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        _logger.LogInformation("Starting SSE refresh stream for {Count} branches", branches.Count);

        // Also keep the background sync thread alive during refresh
        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo != null)
        {
            _syncService.EnsureSyncRunning(userInfo.Id, currentUser);
        }

        await _sseService.StreamSse(
            Response,
            "refresh",
            async streamToken =>
            {
                await foreach (var item in _activityService.StreamRefreshBranchStatus(
                                   currentUser,
                                   branches,
                                   streamToken))
                {
                    if (item is BranchDeletedNotification deleted)
                    {
                        await _sseService.WriteSseEvent(Response, deleted, streamToken, "deleted");
                    }
                    else
                    {
                        await _sseService.WriteSseEvent(Response, item, streamToken);
                    }
                }
            },
            cancellationToken,
            _logger);
    }
}