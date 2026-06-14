using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.GitLab;

namespace Mergician.Services;

/// <summary>
///     Orchestrates merge group management operations that involve both GitLab API lookups
///     and database operations, such as adding branches by MR URL and finding or creating
///     merge groups by MR URL.
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
        var parsed = _mergeRequestLookupService.ParseMergeRequestUrl(mergeRequestUrl);
        if (parsed == null)
        {
            return new AddBranchResult(null, MergeGroupManagementError.InvalidUrl);
        }

        var existing = _mergeGroupRepository.GetMergeGroup(mergeGroupId);
        if (existing == null)
        {
            _logger.LogInformation(
                "Merge group {MergeGroupId} not found for AddBranchByMergeRequestUrl",
                mergeGroupId);

            return new AddBranchResult(null, MergeGroupManagementError.MergeGroupNotFound);
        }

        var lookupResult = await _mergeRequestLookupService.LookupMergeRequest(
            userAccessDetails,
            parsed.ProjectPath,
            parsed.MergeRequestIid,
            cancellationToken);

        if (lookupResult == null)
        {
            return new AddBranchResult(null, MergeGroupManagementError.MergeRequestNotFound);
        }

        var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(
            lookupResult.SourceBranch,
            lookupResult.Project);

        _mergeGroupRepository.EnsureBranchInMergeGroup(mergeGroupId, branchRecord.Id);
        var wasAdded = _mergeGroupRepository.EnsureUserInMergeGroup(userAccessDetails.UserId, mergeGroupId);
        await _ignoredBranchRepository.RemoveIgnoredBranch(
            userAccessDetails.UserId,
            lookupResult.SourceBranch);

        _logger.LogInformation(
            "User {UserId} added branch '{BranchName}' from project {ProjectId} to merge group {MergeGroupId} via MR URL",
            userAccessDetails.UserId,
            lookupResult.SourceBranch,
            lookupResult.Project.Id,
            mergeGroupId);

        if (wasAdded)
        {
            _logger.LogInformation(
                "User {UserId} added to tracked branches for merge group {MergeGroupId} via MR URL addition",
                userAccessDetails.UserId,
                mergeGroupId);
        }

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
        var parsed = _mergeRequestLookupService.ParseMergeRequestUrl(mergeRequestUrl);
        if (parsed == null)
        {
            return new FindOrCreateMergeGroupResult(null, false, MergeGroupManagementError.InvalidUrl);
        }

        var lookupResult = await _mergeRequestLookupService.LookupMergeRequest(
            userAccessDetails,
            parsed.ProjectPath,
            parsed.MergeRequestIid,
            cancellationToken);

        if (lookupResult == null)
        {
            return new FindOrCreateMergeGroupResult(
                null,
                false,
                MergeGroupManagementError.MergeRequestNotFound);
        }

        var existingMergeGroup = _mergeGroupRepository.FindMergeGroupByBranch(
            lookupResult.SourceBranch,
            lookupResult.Project.Id);

        if (existingMergeGroup != null)
        {
            var wasAdded = _mergeGroupRepository.EnsureUserInMergeGroup(
                userAccessDetails.UserId,
                existingMergeGroup.Id);

            await _ignoredBranchRepository.RemoveIgnoredBranch(
                userAccessDetails.UserId,
                lookupResult.SourceBranch);

            _logger.LogInformation(
                "User {UserId} found existing merge group {MergeGroupId} for branch '{BranchName}' via MR URL",
                userAccessDetails.UserId,
                existingMergeGroup.Id,
                lookupResult.SourceBranch);

            if (wasAdded)
            {
                _logger.LogInformation(
                    "User {UserId} added to tracked branches for merge group {MergeGroupId} ('{MergeGroupName}') via MR URL lookup",
                    userAccessDetails.UserId,
                    existingMergeGroup.Id,
                    existingMergeGroup.Name);
            }

            return new FindOrCreateMergeGroupResult(existingMergeGroup.Id, false, null);
        }

        var mergeGroup = _mergeGroupRepository.GetOrCreateMergeGroup(lookupResult.SourceBranch);
        var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(
            lookupResult.SourceBranch,
            lookupResult.Project);

        _mergeGroupRepository.EnsureBranchInMergeGroup(mergeGroup.Id, branchRecord.Id);
        var wasAddedToNewGroup =
            _mergeGroupRepository.EnsureUserInMergeGroup(userAccessDetails.UserId, mergeGroup.Id);

        await _ignoredBranchRepository.RemoveIgnoredBranch(
            userAccessDetails.UserId,
            lookupResult.SourceBranch);

        _logger.LogInformation(
            "User {UserId} created merge group {MergeGroupId} for branch '{BranchName}' via MR URL",
            userAccessDetails.UserId,
            mergeGroup.Id,
            lookupResult.SourceBranch);

        if (wasAddedToNewGroup)
        {
            _logger.LogInformation(
                "User {UserId} added to tracked branches for merge group {MergeGroupId} ('{MergeGroupName}') via MR URL lookup",
                userAccessDetails.UserId,
                mergeGroup.Id,
                mergeGroup.Name);
        }

        return new FindOrCreateMergeGroupResult(mergeGroup.Id, true, null);
    }
}