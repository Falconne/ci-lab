using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.GitLab;

namespace Mergician.Services;

/// <summary>
///     Service used to modify a merge group, such as add branches or create a merge group manually.
/// </summary>
public class MergeGroupManagementService
{
    private readonly IIgnoredBranchRepository _ignoredBranchRepository;

    private readonly ILogger<MergeGroupManagementService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    private readonly MergeRequestLookupService _mergeRequestLookupService;

    public MergeGroupManagementService(
        IMergeGroupRepository mergeGroupRepository,
        IIgnoredBranchRepository ignoredBranchRepository,
        MergeRequestLookupService mergeRequestLookupService,
        ILogger<MergeGroupManagementService> logger)
    {
        _mergeGroupRepository = mergeGroupRepository;
        _ignoredBranchRepository = ignoredBranchRepository;
        _mergeRequestLookupService = mergeRequestLookupService;
        _logger = logger;
    }

    /// <summary>
    ///     Parses a merge request URL, looks up the MR in GitLab, and adds its source branch
    ///     to the specified merge group, subscribing the user if not already subscribed.
    /// </summary>
    public async Task<AddBranchResult> AddBranchByMergeRequestUrl(
        UserAccessDetails userAccessDetails,
        int mergeGroupId,
        string mergeRequestUrl,
        CancellationToken cancellationToken = default)
    {
        var lookupResult = await LookupMergeRequestFromUrl(
            userAccessDetails,
            mergeRequestUrl,
            cancellationToken);

        if (lookupResult.Error != null)
        {
            return new AddBranchResult(null, lookupResult.Error);
        }

        var mr = lookupResult.Result!;

        var existing = _mergeGroupRepository.GetMergeGroup(mergeGroupId);
        if (existing == null)
        {
            _logger.LogInformation(
                "Merge group {MergeGroupId} not found for AddBranchByMergeRequestUrl",
                mergeGroupId);

            return new AddBranchResult(null, MergeGroupManagementError.MergeGroupNotFound);
        }

        var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(mr.SourceBranch, mr.Project);
        _mergeGroupRepository.EnsureBranchInMergeGroup(mergeGroupId, branchRecord.Id);

        await SubscribeUserToMergeGroup(userAccessDetails.UserId, mergeGroupId, null, mr.SourceBranch);

        _logger.LogInformation(
            "User {UserId} added branch '{BranchName}' from project {ProjectId} to merge group {MergeGroupId} via MR URL",
            userAccessDetails.UserId,
            mr.SourceBranch,
            mr.Project.Id,
            mergeGroupId);

        var updated = _mergeGroupRepository.GetMergeGroup(mergeGroupId);
        return new AddBranchResult(updated, null);
    }

    /// <summary>
    ///     Parses a merge request URL, looks up the MR in GitLab, then finds an existing
    ///     merge group containing that branch or creates a new one, subscribing the user.
    /// </summary>
    public async Task<FindOrCreateMergeGroupResult> FindOrCreateMergeGroupByMergeRequestUrl(
        UserAccessDetails userAccessDetails,
        string mergeRequestUrl,
        CancellationToken cancellationToken = default)
    {
        var lookupResult = await LookupMergeRequestFromUrl(
            userAccessDetails,
            mergeRequestUrl,
            cancellationToken);

        if (lookupResult.Error != null)
        {
            return new FindOrCreateMergeGroupResult(null, false, lookupResult.Error);
        }

        var mr = lookupResult.Result!;

        var existingMergeGroup = _mergeGroupRepository.FindMergeGroupByBranch(mr.SourceBranch, mr.Project.Id);
        if (existingMergeGroup != null)
        {
            await SubscribeUserToMergeGroup(
                userAccessDetails.UserId,
                existingMergeGroup.Id,
                existingMergeGroup.Name,
                mr.SourceBranch);

            _logger.LogInformation(
                "User {UserId} found existing merge group {MergeGroupId} for branch '{BranchName}' via MR URL",
                userAccessDetails.UserId,
                existingMergeGroup.Id,
                mr.SourceBranch);

            return new FindOrCreateMergeGroupResult(existingMergeGroup.Id, false, null);
        }

        var mergeGroup = _mergeGroupRepository.GetOrCreateMergeGroup(mr.SourceBranch);
        var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(mr.SourceBranch, mr.Project);
        _mergeGroupRepository.EnsureBranchInMergeGroup(mergeGroup.Id, branchRecord.Id);

        await SubscribeUserToMergeGroup(
            userAccessDetails.UserId,
            mergeGroup.Id,
            mergeGroup.Name,
            mr.SourceBranch);

        _logger.LogInformation(
            "User {UserId} created merge group {MergeGroupId} for branch '{BranchName}' via MR URL",
            userAccessDetails.UserId,
            mergeGroup.Id,
            mr.SourceBranch);

        return new FindOrCreateMergeGroupResult(mergeGroup.Id, true, null);
    }

    /// <summary>
    ///     Parses a merge request URL and looks up the MR in GitLab.
    ///     Returns the lookup result or a management error if either step fails.
    /// </summary>
    private async Task<MrLookupOutcome> LookupMergeRequestFromUrl(
        UserAccessDetails userAccessDetails,
        string mergeRequestUrl,
        CancellationToken cancellationToken)
    {
        var parsed = _mergeRequestLookupService.ParseMergeRequestUrl(mergeRequestUrl);
        if (parsed == null)
        {
            return new MrLookupOutcome(null, MergeGroupManagementError.InvalidUrl);
        }

        var result = await _mergeRequestLookupService.LookupMergeRequest(
            userAccessDetails,
            parsed.ProjectPath,
            parsed.MergeRequestIid,
            cancellationToken);

        return result == null
            ? new MrLookupOutcome(null, MergeGroupManagementError.MergeRequestNotFound)
            : new MrLookupOutcome(result, null);
    }

    /// <summary>
    ///     Subscribes the user to a merge group and removes the branch from the user's ignored
    ///     list. Logs if the user was newly added to the group.
    /// </summary>
    private async Task SubscribeUserToMergeGroup(
        int userId,
        int mergeGroupId,
        string? mergeGroupName,
        string sourceBranch)
    {
        var wasAdded = _mergeGroupRepository.EnsureUserInMergeGroup(userId, mergeGroupId);
        await _ignoredBranchRepository.RemoveIgnoredBranch(userId, sourceBranch);

        if (wasAdded)
        {
            _logger.LogInformation(
                "User {UserId} added to tracked branches for merge group {MergeGroupId}{GroupNameSuffix}",
                userId,
                mergeGroupId,
                mergeGroupName != null ? $" ('{mergeGroupName}')" : "");
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private record MrLookupOutcome(MergeRequestLookupResult? Result, MergeGroupManagementError? Error);
}