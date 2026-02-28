using Mergician.Entities;
using Mergician.Services;
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

    private readonly SseService _sseService;

    private readonly UserActivitySyncService _syncService;

    public MergeGroupController(
        GitlabActivityService activityService,
        SseService sseService,
        UserActivitySyncService syncService,
        ILogger<MergeGroupController> logger)
    {
        _activityService = activityService;
        _sseService = sseService;
        _syncService = syncService;
        _logger = logger;
    }

    /// <summary>
    ///     Returns a diff of the merge group's branches compared to what the frontend currently shows.
    ///     The frontend sends the branch database IDs it currently displays, and the backend
    ///     returns branches to add or remove based on the current database state.
    ///     Also checks for new or deleted branches in the merge group.
    /// </summary>
    [HttpPost("{mergeGroupId:int}/refresh-branches")]
    public IActionResult RefreshBranches(
        int mergeGroupId,
        [FromBody] MergeGroupPollRequest request)
    {
        var currentUser = HttpContext.GetGitlabUser();

        var userId = currentUser.UserId;

        // Keep the background sync thread alive during polling
        _syncService.EnsureSyncRunning(userId, currentUser);

        _logger.LogDebug(
            "Merge group branch diff for user {UserId}, merge group {MergeGroupId} with {Count} known branches",
            userId,
            mergeGroupId,
            request.KnownBranches.Count);

        var result = _activityService.GetMergeGroupDiff(userId, mergeGroupId, request.KnownBranches);

        if (result == null)
        {
            return NotFound(new ErrorResponse("Merge group not found"));
        }

        _logger.LogDebug(
            "Returning {Added} added, {Removed} removed branches for user {UserId}, merge group {MergeGroupId}",
            result.Added.Count,
            result.Removed.Count,
            userId,
            mergeGroupId);

        return Ok(result);
    }

    /// <summary>
    ///     SSE stream that refreshes MR/approval/build status for branches in a merge group.
    ///     Yields updated BranchActivity records as each branch is resolved.
    /// </summary>
    [HttpPost("{mergeGroupId:int}/refresh-activity")]
    public async Task RefreshActivity(
        int mergeGroupId,
        [FromBody] DashboardPollRequest request,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        _logger.LogInformation(
            "Starting SSE refresh stream for merge group {MergeGroupId} with {Count} branches",
            mergeGroupId,
            request.KnownBranches.Count);

        // Keep the background sync thread alive during refresh
        _syncService.EnsureSyncRunning(currentUser.UserId, currentUser);

        await _sseService.StreamSse(
            Response,
            $"merge-group-{mergeGroupId}-refresh",
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