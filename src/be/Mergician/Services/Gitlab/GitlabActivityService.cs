using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;

namespace Mergician.Services.Gitlab;

/// <summary>
///     Provides methods for refreshing and cleaning up branch data against the GitLab API.
///     Used by the background sync threads managed by <see cref="UserActivitySyncService" />.
/// </summary>
public class GitlabActivityService
{
    private readonly GitlabPipelineService _gitlabPipelineService;

    private readonly GitlabService _gitlabService;

    private readonly ILogger<GitlabActivityService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    public GitlabActivityService(
        GitlabService gitlabService,
        GitlabPipelineService gitlabPipelineService,
        IMergeGroupRepository mergeGroupRepository,
        ILogger<GitlabActivityService> logger)
    {
        _gitlabService = gitlabService;
        _gitlabPipelineService = gitlabPipelineService;
        _mergeGroupRepository = mergeGroupRepository;
        _logger = logger;
    }

    /// <summary>
    ///     Checks all tracked branches for a user and removes any that have been deleted from GitLab.
    ///     Called by the background sync thread during each poll cycle.
    /// </summary>
    public async Task CleanupDeletedBranches(
        AccessDetailsBase accessDetails,
        int gitlabUserId,
        CancellationToken cancellationToken)
    {
        var userGroups = _mergeGroupRepository.GetMergeGroupsForUser(gitlabUserId);
        var userBranches = userGroups.SelectMany(g => g.Branches).ToList();
        _logger.LogDebug(
            "Checking {Count} tracked branches for user {UserId} for deletion",
            userBranches.Count,
            gitlabUserId);

        foreach (var branch in userBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lookup = await _gitlabService.GetBranchLookupResult(
                accessDetails,
                branch.ProjectId,
                branch.BranchName);

            if (lookup.IsMissing)
            {
                _logger.LogInformation(
                    "Background sync: branch '{BranchName}' in project {ProjectId} no longer exists, removing",
                    branch.BranchName,
                    branch.ProjectId);

                RemoveBranchAndCleanup(branch.Id);
            }
            else if (lookup.IsUnavailable)
            {
                _logger.LogDebug(
                    "Background sync: branch lookup unavailable for '{BranchName}' in project {ProjectId}; skipping",
                    branch.BranchName,
                    branch.ProjectId);
            }
        }
    }

    /// <summary>
    ///     Refreshes MR, approval, and build job details for all branches tracked by the given user.
    ///     Called by the background sync thread as a second pass after activity sync.
    /// </summary>
    public async Task RefreshAllBranchDetails(
        AccessDetailsBase accessDetails,
        int gitlabUserId,
        CancellationToken cancellationToken)
    {
        var userGroups = _mergeGroupRepository.GetMergeGroupsForUser(gitlabUserId);
        var userBranches = userGroups.SelectMany(g => g.Branches).ToList();

        _logger.LogDebug(
            "Refreshing details for {Count} branches for user {UserId}",
            userBranches.Count,
            gitlabUserId);

        foreach (var branch in userBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await RefreshBranchDetails(accessDetails, branch, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to refresh details for branch '{BranchName}' in project {ProjectId}; skipping",
                    branch.BranchName,
                    branch.ProjectId);
            }
        }

        _logger.LogDebug("Finished refreshing details for user {UserId}", gitlabUserId);
    }

    /// <summary>
    ///     Checks if a branch should be skipped by looking it up in GitLab.
    ///     If the branch no longer exists and a tracked record ID is provided, removes it from the DB.
    ///     Returns true if the branch should be skipped.
    /// </summary>
    public async Task<bool> ShouldSkipBranchByLookup(
        AccessDetailsBase accessDetails,
        string branchName,
        int projectId,
        int? trackedBranchInProjectId,
        string operationName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var branchLookup = await _gitlabService.GetBranchLookupResult(
            accessDetails,
            projectId,
            branchName);

        if (branchLookup.IsMissing)
        {
            _logger.LogInformation(
                "Skipping branch '{BranchName}' in project {ProjectId} during {OperationName}: branch no longer exists",
                branchName,
                projectId,
                operationName);

            if (trackedBranchInProjectId.HasValue)
            {
                _logger.LogInformation(
                    "Removing tracked branch record {BranchRecordId} for '{BranchName}' in project {ProjectId} during {OperationName}",
                    trackedBranchInProjectId.Value,
                    branchName,
                    projectId,
                    operationName);

                RemoveBranchAndCleanup(trackedBranchInProjectId.Value);
            }

            return true;
        }

        if (branchLookup.IsUnavailable)
        {
            _logger.LogWarning(
                "Skipping branch '{BranchName}' in project {ProjectId} during {OperationName}: branch lookup unavailable",
                branchName,
                projectId,
                operationName);

            return true;
        }

        return false;
    }

    /// <summary>
    ///     Checks if a branch should be skipped because its project is scheduled for deletion.
    ///     If so, and a tracked record ID is provided, removes it from the DB.
    ///     Returns true if the branch should be skipped.
    /// </summary>
    public bool ShouldSkipScheduledForDeletion(
        string branchName,
        int projectId,
        string projectNameWithNamespace,
        int? trackedBranchInMergeGroupId,
        string operationName)
    {
        if (!GitlabService.IsScheduledForDeletion(projectNameWithNamespace))
        {
            return false;
        }

        _logger.LogInformation(
            "Skipping branch '{BranchName}' in project {ProjectId} during {OperationName}: project/group is scheduled for deletion ('{ProjectNameWithNamespace}')",
            branchName,
            projectId,
            operationName,
            projectNameWithNamespace);

        if (trackedBranchInMergeGroupId.HasValue)
        {
            _logger.LogInformation(
                "Removing tracked branch record {BranchRecordId} for '{BranchName}' in project {ProjectId} during {OperationName} because project is scheduled for deletion",
                trackedBranchInMergeGroupId.Value,
                branchName,
                projectId,
                operationName);

            RemoveBranchAndCleanup(trackedBranchInMergeGroupId.Value);
        }

        return true;
    }

    /// <summary>
    ///     Fetches MR, approval, and build job details from GitLab for the given branch
    ///     and persists them in the database. Silently skips if project info is unavailable.
    /// </summary>
    private async Task RefreshBranchDetails(
        AccessDetailsBase accessDetails,
        BranchWithActivity branch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Refreshing details for branch '{BranchName}' in project {ProjectId}",
            branch.BranchName,
            branch.ProjectId);

        var project = await _gitlabService.GetProject(accessDetails, branch.ProjectId);
        if (project == null)
        {
            _logger.LogDebug(
                "Project {ProjectId} not found when refreshing details for '{BranchName}'; skipping",
                branch.ProjectId,
                branch.BranchName);

            return;
        }

        if (GitlabService.IsScheduledForDeletion(project.NameWithNamespace))
        {
            _logger.LogInformation(
                "Skipping detail refresh for branch '{BranchName}' in project {ProjectId}: project scheduled for deletion",
                branch.BranchName,
                branch.ProjectId);

            return;
        }

        var projectUrl = project.WebUrl;

        var mergeRequests = await _gitlabService.GetMergeRequests(
            accessDetails,
            branch.ProjectId,
            branch.BranchName);

        var hasMr = mergeRequests.Count > 0;
        int? approvalsRequired = null;
        int? approvalsGiven = null;
        string? mrTitle = null;
        string? mrUrl = null;

        if (hasMr)
        {
            var first = mergeRequests[0];
            mrTitle = first.Title;
            mrUrl = first.WebUrl;

            var approval = await _gitlabService.GetMergeRequestApprovals(
                accessDetails,
                branch.ProjectId,
                first.Iid);

            if (approval != null)
            {
                approvalsGiven = approval.ApprovedBy.Count;
                approvalsRequired = Math.Max(approval.ApprovalsRequired ?? 0, 0);
            }
        }

        var buildJobs = await _gitlabPipelineService.GetLatestExternalJobsForBranch(
            accessDetails,
            branch.ProjectId,
            branch.BranchName,
            cancellationToken);

        // Fetch the branch's latest commit date from GitLab to use as the last updated timestamp
        var branchDetails = await _gitlabService.GetBranchDetails(
            accessDetails,
            branch.ProjectId,
            branch.BranchName);

        DateTimeOffset? lastCommitTime = null;
        if (branchDetails?.Commit?.CommittedDate != null)
        {
            lastCommitTime = branchDetails.Commit.CommittedDate.Value.ToUniversalTime();
            _logger.LogDebug(
                "Branch '{BranchName}' in project {ProjectId}: latest commit at {CommitTime}",
                branch.BranchName,
                branch.ProjectId,
                lastCommitTime);
        }

        _mergeGroupRepository.UpdateBranchDetails(
            branch.Id,
            hasMr,
            mrTitle,
            mrUrl,
            projectUrl,
            approvalsRequired,
            approvalsGiven,
            buildJobs,
            lastCommitTime);

        _logger.LogDebug(
            "Updated details for branch '{BranchName}' in project {ProjectId}: hasMr={HasMr}, {JobCount} jobs",
            branch.BranchName,
            branch.ProjectId,
            hasMr,
            buildJobs.Count);
    }

    private void RemoveBranchAndCleanup(int branchInMergeGroupId)
    {
        _mergeGroupRepository.DeleteBranch(branchInMergeGroupId);

        // Clean up any merge groups that are now empty
        var emptyGroups = _mergeGroupRepository.GetEmptyMergeGroups();
        foreach (var group in emptyGroups)
        {
            _logger.LogInformation(
                "Removing empty merge group {MergeGroupId} '{Name}'",
                group.Id,
                group.Name);

            _mergeGroupRepository.DeleteMergeGroup(group.Id);
        }
    }
}
