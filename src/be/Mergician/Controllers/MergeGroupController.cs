using Mergician.Entities;
using Mergician.Services;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
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

    private readonly ICoreRepository _coreRepository;

    private readonly GitlabService _gitlabService;

    private readonly ILogger<MergeGroupController> _logger;

    private readonly SseService _sseService;

    private readonly UserActivitySyncService _syncService;

    public MergeGroupController(
        GitlabActivityService activityService,
        GitlabService gitlabService,
        ICoreRepository coreRepository,
        SseService sseService,
        UserActivitySyncService syncService,
        ILogger<MergeGroupController> logger)
    {
        _activityService = activityService;
        _gitlabService = gitlabService;
        _coreRepository = coreRepository;
        _sseService = sseService;
        _syncService = syncService;
        _logger = logger;
    }

    /// <summary>
    ///     Returns fully resolved details for a single merge group.
    /// </summary>
    [HttpGet("{mergeGroupId:int}")]
    // TODO: Remove this endpoint and just make it so that if the frontend calls the `refresh-branches` endpoint below with an empty list of known branches,
    // it will produce the same result. We do not need to "fully resolve" the branch details. They can be populated by a call to `refresh-activity`.
    public async Task<IActionResult> GetMergeGroupDetails(
        int mergeGroupId,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();
        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo == null)
        {
            return Unauthorized();
        }

        _logger.LogInformation(
            "Fetching merge group details for user {UserId}, merge group {MergeGroupId}",
            userInfo.Id,
            mergeGroupId);

        // TODO: Remove this method after this endpoint is removed.
        var details = await _activityService.GetMergeGroupDetails(
            currentUser,
            userInfo.Id,
            mergeGroupId,
            cancellationToken);

        if (details == null)
        {
            return NotFound(new ErrorResponse("Merge group not found"));
        }

        return Ok(details);
    }

    // TODO: Rename the endpoint to refresh-branches and the method to RefreshBranches. Update the frontend callers.
    /// <summary>
    ///     Returns a diff of the merge group's branches compared to what the frontend currently shows.
    ///     The frontend sends the branch database IDs it currently displays, and the backend
    ///     returns branches to add or remove based on the current database state.
    ///     Also checks for new or deleted branches in the merge group.
    /// </summary>
    [HttpPost("{mergeGroupId:int}/poll")]
    public async Task<IActionResult> PollMergeGroup(
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
            "Merge group poll for user {UserId}, merge group {MergeGroupId} with {Count} known branches",
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

        // TODO: In the frontend, make it so that if this method returns any diff, then immediately call the refresh-activity activity endpoint

        return Ok(result);
    }

    // TODO: Rename the endpoint to refresh-activity and the method to RefreshActivity. Update the frontend callers.
    /// <summary>
    ///     SSE stream that refreshes MR/approval/build status for branches in a merge group.
    ///     Yields updated BranchActivity records as each branch is resolved.
    ///     Yields BranchDeletedNotification when a branch no longer exists.
    /// </summary>
    [HttpPost("{mergeGroupId:int}/refresh")]
    public async Task RefreshMergeGroup(
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