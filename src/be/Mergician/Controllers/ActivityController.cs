using Mergician.Entities;
using Mergician.Services;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ActivityController : ControllerBase
{
    private readonly UserActivityBackgroundSyncService _backgroundSyncService;

    private readonly ILogger<ActivityController> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    public ActivityController(
        IMergeGroupRepository mergeGroupRepository,
        UserActivityBackgroundSyncService backgroundSyncService,
        ILogger<ActivityController> logger)
    {
        _mergeGroupRepository = mergeGroupRepository;
        _backgroundSyncService = backgroundSyncService;
        _logger = logger;
    }

    /// <summary>
    ///     Returns a full snapshot of all merge groups for the current user.
    ///     MR, approval, and build details are populated by the background sync thread.
    ///     Also ensures the background sync thread is running and records user activity.
    /// </summary>
    [HttpPost("refresh")]
    public ActionResult<List<MergeGroup>> Refresh()
    {
        var currentUser = HttpContext.GetGitLabUser();

        var userId = currentUser.UserId;

        // Ensure the background sync thread is running (also records that user is still active)
        _backgroundSyncService.EnsureSyncRunning(userId, currentUser);

        _logger.LogDebug("Dashboard refresh for user {UserId}", userId);

        var result = _mergeGroupRepository.GetMergeGroupsForUser(userId);

        _logger.LogDebug(
            "Returning {GroupCount} merge groups with {BranchCount} branches for user {UserId}",
            result.Count,
            result.Sum(g => g.Branches.Count),
            userId);

        return Ok(result);
    }
}