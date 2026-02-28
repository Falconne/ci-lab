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

    private readonly UserActivitySyncService _syncService;

    public ActivityController(
        GitlabActivityService activityService,
        UserActivitySyncService syncService,
        ILogger<ActivityController> logger)
    {
        _activityService = activityService;
        _syncService = syncService;
        _logger = logger;
    }

    /// <summary>
    ///     Returns a full snapshot of all branches tracked by the current user.
    ///     MR, approval, and build details are populated by the background sync thread.
    ///     Also ensures the background sync thread is running and records user activity.
    /// </summary>
    [HttpPost("refresh")]
    public IActionResult Refresh()
    {
        var currentUser = HttpContext.GetGitlabUser();

        var userId = currentUser.UserId;

        // Ensure the background sync thread is running (also records that user is still active)
        _syncService.EnsureSyncRunning(userId, currentUser);

        _logger.LogDebug("Dashboard refresh for user {UserId}", userId);

        var result = _activityService.GetDashboardBranches(userId);

        _logger.LogDebug("Returning {Count} branches for user {UserId}", result.Branches.Count, userId);

        return Ok(result);
    }
}