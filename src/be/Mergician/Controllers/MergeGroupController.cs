using Mergician.Entities;
using Mergician.Services;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[Authorize]
[ApiController]
[Route("api/merge-groups")]
public class MergeGroupController : ControllerBase
{
    private readonly UserActivityBackgroundSyncService _backgroundSyncService;

    private readonly ILogger<MergeGroupController> _logger;

    private readonly MergeGroupManagementService _mergeGroupManagementService;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    public MergeGroupController(
        IMergeGroupRepository mergeGroupRepository,
        UserActivityBackgroundSyncService backgroundSyncService,
        MergeGroupManagementService mergeGroupManagementService,
        ILogger<MergeGroupController> logger)
    {
        _mergeGroupRepository = mergeGroupRepository;
        _backgroundSyncService = backgroundSyncService;
        _mergeGroupManagementService = mergeGroupManagementService;
        _logger = logger;
    }

    /// <summary>
    ///     Returns a full snapshot of branches in the specified merge group.
    ///     MR, approval, and build details are populated by the background sync thread.
    ///     Also ensures the background sync thread is running and records user activity.
    ///     Returns 404 if the merge group does not exist for the current user.
    /// </summary>
    [HttpPost("{mergeGroupId:int}/refresh")]
    public ActionResult<MergeGroup> Refresh(int mergeGroupId)
    {
        var currentUser = HttpContext.GetGitLabUser();

        var userId = currentUser.UserId;

        // Keep the background sync thread alive during polling
        _backgroundSyncService.EnsureSyncRunning(currentUser);

        _logger.LogDebug(
            "Merge group refresh for user {UserId}, merge group {MergeGroupId}",
            userId,
            mergeGroupId);

        var result = _mergeGroupRepository.GetMergeGroup(mergeGroupId);

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

    /// <summary>
    ///     Updates auto merge and auto rebase settings for a merge group.
    ///     Returns 404 if the merge group does not exist.
    /// </summary>
    [HttpPut("{mergeGroupId:int}/settings")]
    public ActionResult<MergeGroup> UpdateSettings(
        int mergeGroupId,
        [FromBody] UpdateAutoMergeSettingsRequest request)
    {
        var currentUser = HttpContext.GetGitLabUser();

        _logger.LogInformation(
            "User {UserId} updating auto merge settings for merge group {MergeGroupId}: autoMerge={AutoMerge}, autoRebase={AutoRebase}",
            currentUser.UserId,
            mergeGroupId,
            request.AutoMerge,
            request.AutoRebase);

        var existing = _mergeGroupRepository.GetMergeGroup(mergeGroupId);
        if (existing == null)
        {
            return NotFound(new ErrorResponse("Merge group not found"));
        }

        _mergeGroupRepository.UpdateAutoMergeSettings(mergeGroupId, request.AutoMerge, request.AutoRebase);

        // Clear any existing warning when settings change
        _mergeGroupRepository.UpdateAutoMergeWarning(mergeGroupId, null);

        var updated = _mergeGroupRepository.GetMergeGroup(mergeGroupId);

        return Ok(updated);
    }

    /// <summary>
    ///     Clears the auto merge warning for a merge group.
    /// </summary>
    [HttpPost("{mergeGroupId:int}/settings/clear-warning")]
    public IActionResult ClearWarning(int mergeGroupId)
    {
        _logger.LogInformation("Clearing auto merge warning for merge group {MergeGroupId}", mergeGroupId);
        _mergeGroupRepository.UpdateAutoMergeWarning(mergeGroupId, null);
        return NoContent();
    }

    /// <summary>
    ///     Returns whether the current user is subscribed to the specified merge group.
    /// </summary>
    [HttpGet("{mergeGroupId:int}/subscription")]
    public ActionResult<SubscriptionResponse> GetSubscription(int mergeGroupId)
    {
        var currentUser = HttpContext.GetGitLabUser();

        var existing = _mergeGroupRepository.GetMergeGroup(mergeGroupId);
        if (existing == null)
        {
            return NotFound(new ErrorResponse("Merge group not found"));
        }

        var isSubscribed = _mergeGroupRepository.IsUserInMergeGroup(currentUser.UserId, mergeGroupId);

        return Ok(new SubscriptionResponse(isSubscribed));
    }

    /// <summary>
    ///     Subscribes the current user to the specified merge group ("Add to my Merge Groups").
    /// </summary>
    [HttpPut("{mergeGroupId:int}/subscription")]
    public ActionResult<SubscriptionResponse> Subscribe(int mergeGroupId)
    {
        var currentUser = HttpContext.GetGitLabUser();

        var existing = _mergeGroupRepository.GetMergeGroup(mergeGroupId);
        if (existing == null)
        {
            return NotFound(new ErrorResponse("Merge group not found"));
        }

        _mergeGroupRepository.EnsureUserInMergeGroup(currentUser.UserId, mergeGroupId);

        _logger.LogInformation(
            "User {UserId} subscribed to merge group {MergeGroupId}",
            currentUser.UserId,
            mergeGroupId);

        return Ok(new SubscriptionResponse(true));
    }

    /// <summary>
    ///     Unsubscribes the current user from the specified merge group ("Remove from my Merge Groups").
    /// </summary>
    [HttpDelete("{mergeGroupId:int}/subscription")]
    public ActionResult<SubscriptionResponse> Unsubscribe(int mergeGroupId)
    {
        var currentUser = HttpContext.GetGitLabUser();

        var existing = _mergeGroupRepository.GetMergeGroup(mergeGroupId);
        if (existing == null)
        {
            return NotFound(new ErrorResponse("Merge group not found"));
        }

        _mergeGroupRepository.RemoveUserFromMergeGroup(currentUser.UserId, mergeGroupId);

        _logger.LogInformation(
            "User {UserId} unsubscribed from merge group {MergeGroupId}",
            currentUser.UserId,
            mergeGroupId);

        return Ok(new SubscriptionResponse(false));
    }

    /// <summary>
    ///     Adds a branch to the merge group by looking up a GitLab merge request URL.
    ///     Parses the URL, fetches the MR from GitLab to get the source branch,
    ///     then adds that branch to this merge group.
    /// </summary>
    [HttpPost("{mergeGroupId:int}/add-by-merge-request")]
    public async Task<ActionResult<MergeGroup>> AddByMergeRequest(
        int mergeGroupId,
        [FromBody] MergeRequestUrlRequest request)
    {
        var currentUser = HttpContext.GetGitLabUser();

        var result = await _mergeGroupManagementService.AddBranchByMergeRequestUrl(
            currentUser,
            mergeGroupId,
            request.MergeRequestUrl);

        return result.Error switch
        {
            MergeGroupManagementError.InvalidUrl => BadRequest(
                new ErrorResponse(
                    "Invalid merge request URL. Expected format: https://gitlab.example.com/group/project/-/merge_requests/123")),

            MergeGroupManagementError.MergeGroupNotFound => NotFound(
                new ErrorResponse("Merge group not found")),

            MergeGroupManagementError.MergeRequestNotFound => NotFound(
                new ErrorResponse(
                    "Merge request not found in GitLab. Check the URL and ensure you have access to the project.")),

            _ => Ok(result.UpdatedMergeGroup!)
        };
    }

    /// <summary>
    ///     Finds or creates a merge group for a given merge request URL.
    ///     Looks up the MR in GitLab to get the source branch, then finds an existing
    ///     merge group containing that branch or creates a new one.
    /// </summary>
    [HttpPost("find-by-merge-request")]
    public async Task<ActionResult<FindByMergeRequestResponse>> FindByMergeRequest(
        [FromBody] MergeRequestUrlRequest request)
    {
        var currentUser = HttpContext.GetGitLabUser();

        var result = await _mergeGroupManagementService.FindOrCreateMergeGroupByMergeRequestUrl(
            currentUser,
            request.MergeRequestUrl);

        return result.Error switch
        {
            MergeGroupManagementError.InvalidUrl => BadRequest(
                new ErrorResponse(
                    "Invalid merge request URL. Expected format: https://gitlab.example.com/group/project/-/merge_requests/123")),
            MergeGroupManagementError.MergeRequestNotFound => NotFound(
                new ErrorResponse(
                    "Merge request not found in GitLab. Check the URL and ensure you have access to the project.")),
            _ => Ok(new FindByMergeRequestResponse(result.MergeGroupId!.Value, result.WasCreated))
        };
    }
}