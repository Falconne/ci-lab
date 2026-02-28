using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[Authorize]
[ApiController]
[Route("api/merge-groups")]
public class MergeGroupController : ControllerBase
{
    private readonly GitlabActivityService _activityService;

    private readonly ILogger<MergeGroupController> _logger;

    private readonly UserActivitySyncService _syncService;

    public MergeGroupController(
        GitlabActivityService activityService,
        UserActivitySyncService syncService,
        ILogger<MergeGroupController> logger)
    {
        _activityService = activityService;
        _syncService = syncService;
        _logger = logger;
    }

    /// <summary>
    ///     Returns a full snapshot of branches in the specified merge group.
    ///     MR, approval, and build details are populated by the background sync thread.
    ///     Also ensures the background sync thread is running and records user activity.
    ///     Returns 404 if the merge group does not exist for the current user.
    /// </summary>
    [HttpPost("{mergeGroupId:int}/refresh")]
    public IActionResult Refresh(int mergeGroupId)
    {
        var currentUser = HttpContext.GetGitlabUser();

        var userId = currentUser.UserId;

        // Keep the background sync thread alive during polling
        _syncService.EnsureSyncRunning(userId, currentUser);

        _logger.LogDebug(
            "Merge group refresh for user {UserId}, merge group {MergeGroupId}",
            userId,
            mergeGroupId);

        var result = _activityService.GetMergeGroupBranches(mergeGroupId);

        if (result == null)
        {
            return NotFound(new ErrorResponse("Merge group not found"));
        }

        _logger.LogDebug(
            "Returning {Count} branches for user {UserId}, merge group {MergeGroupId}",
            result.Branches.Count,
            userId,
            mergeGroupId);

        return Ok(result);
    }
}