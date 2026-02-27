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

    private readonly GitlabService _gitlabService;

    private readonly ILogger<MergeGroupController> _logger;

    private readonly SseService _sseService;

    private readonly UserActivitySyncService _syncService;

    public MergeGroupController(
        GitlabActivityService activityService,
        GitlabService gitlabService,
        SseService sseService,
        UserActivitySyncService syncService,
        ILogger<MergeGroupController> logger)
    {
        _activityService = activityService;
        _gitlabService = gitlabService;
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
    public async Task<IActionResult> RefreshBranches(
        int mergeGroupId,
        [FromBody] MergeGroupPollRequest request,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo == null)
        {
            return Unauthorized();
        }

        // Keep the background sync thread alive during polling
        _syncService.EnsureSyncRunning(userInfo.Id, currentUser);

        _logger.LogDebug(
            "Merge group branch diff for user {UserId}, merge group {MergeGroupId} with {Count} known branches",
            userInfo.Id,
            mergeGroupId,
            request.KnownBranches.Count);

        var result = _activityService.GetMergeGroupDiff(userInfo.Id, mergeGroupId, request.KnownBranches);

        if (result == null)
        {
            return NotFound(new ErrorResponse("Merge group not found"));
        }

        _logger.LogDebug(
            "Returning {Added} added, {Removed} removed branches for user {UserId}, merge group {MergeGroupId}",
            result.Added.Count,
            result.Removed.Count,
            userInfo.Id,
            mergeGroupId);

        return Ok(result);
    }

    /// <summary>
    ///     SSE stream that refreshes MR/approval/build status for branches in a merge group.
    ///     Yields updated BranchActivity records as each branch is resolved.
    ///     Yields BranchDeletedNotification when a branch no longer exists.
    /// </summary>
    [HttpPost("{mergeGroupId:int}/refresh-activity")]
    public async Task RefreshActivity(
        int mergeGroupId,
        [FromBody] List<BranchRefreshRequest> branches,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        _logger.LogInformation(
            "Starting SSE refresh stream for merge group {MergeGroupId} with {Count} branches",
            mergeGroupId,
            branches.Count);

        // Keep the background sync thread alive during refresh
        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo != null)
        {
            _syncService.EnsureSyncRunning(userInfo.Id, currentUser);
        }

        await _sseService.StreamSse(
            Response,
            $"merge-group-{mergeGroupId}-refresh",
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