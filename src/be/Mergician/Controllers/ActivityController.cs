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

    private readonly ILogger<ActivityController> _logger;

    private readonly SseService _sseService;

    private readonly UserActivitySyncService _syncService;

    public ActivityController(
        GitlabActivityService activityService,
        SseService sseService,
        UserActivitySyncService syncService,
        ILogger<ActivityController> logger)
    {
        _activityService = activityService;
        _sseService = sseService;
        _syncService = syncService;
        _logger = logger;
    }

    [HttpPost("refresh-branches")]
    public IActionResult RefreshBranches(
        [FromBody] DashboardPollRequest request)
    {
        var currentUser = HttpContext.GetGitlabUser();

        var userId = currentUser.UserId;

        // Ensure the background sync thread is running (also records that user is still active)
        _syncService.EnsureSyncRunning(userId, currentUser);

        _logger.LogDebug(
            "Dashboard branch diff for user {UserId} with {Count} known branches",
            userId,
            request.KnownBranches.Count);

        var result = _activityService.GetDashboardDiff(userId, request.KnownBranches);

        _logger.LogDebug(
            "Returning {Added} added, {Removed} removed branches for user {UserId}",
            result.Added.Count,
            result.Removed.Count,
            userId);

        return Ok(result);
    }

    [HttpPost("refresh-activity")]
    public async Task RefreshActivity(
        [FromBody] DashboardPollRequest request,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        _logger.LogInformation("Starting SSE refresh stream for {Count} branches", request.KnownBranches.Count);

        // Keep the background sync thread alive during refresh
        _syncService.EnsureSyncRunning(currentUser.UserId, currentUser);

        await _sseService.StreamSse(
            Response,
            "refresh",
            async streamToken =>
            {
                await foreach (var item in _activityService.StreamRefreshBranchStatus(
                                   currentUser,
                                   request.KnownBranches,
                                   streamToken))
                {
                    await _sseService.WriteSseEvent(Response, item, streamToken);
                }
            },
            cancellationToken,
            _logger);
    }
}