using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;

namespace Mergician.Services.Gitlab;

/// <summary>
///     Provides activity-related operations for the current user.
///     Dashboard data is served from the database; background sync threads
///     (managed by <see cref="UserActivitySyncService" />) keep the database current,
///     including MR, approval, and build status details.
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
    ///     Returns all current merge groups for the authenticated user as a full snapshot.
    ///     Data is read from the database and includes persisted MR, approval, and build details.
    /// </summary>
    public List<MergeGroup> GetMergeGroupsForUser(int gitlabUserId)
    {
        var result = _mergeGroupRepository.GetMergeGroupsForUser(gitlabUserId);

        _logger.LogDebug(
            "Returning {GroupCount} merge groups with {BranchCount} branches for user {UserId}",
            result.Count,
            result.Sum(g => g.Branches.Count),
            gitlabUserId);

        return result;
    }

    /// <summary>
    ///     Fetches push events from GitLab since the given time and stores discovered
    ///     branches in the database. Called by the background sync thread.
    /// </summary>
    public async Task SyncUserActivityFromGitLab(
        AccessDetailsBase accessDetails,
        int gitlabUserId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Syncing GitLab activity for user {UserId} since {Since}",
            gitlabUserId,
            since);

        var pushEvents = _gitlabService.GetPushEventsSince(accessDetails, since, cancellationToken);
        var processedKeys = new HashSet<string>();

        await foreach (var pushEvent in pushEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (GitlabService.IsPossibleDefaultBranch(pushEvent.BranchName))
            {
                _logger.LogDebug(
                    "Skipping default branch '{BranchName}' in project {ProjectId}",
                    pushEvent.BranchName,
                    pushEvent.ProjectId);

                continue;
            }

            var key = $"{pushEvent.BranchName}:{pushEvent.ProjectId}";
            if (!processedKeys.Add(key))
            {
                _logger.LogDebug(
                    "Already processed branch '{BranchName}' in project {ProjectId}, skipping duplicate push event",
                    pushEvent.BranchName,
                    pushEvent.ProjectId);

                continue;
            }

            if (await ShouldSkipBranchByLookup(
                    accessDetails,
                    pushEvent.BranchName,
                    pushEvent.ProjectId,
                    null,
                    "push-event processing",
                    cancellationToken))
            {
                continue;
            }

            var project = await _gitlabService.GetProject(accessDetails, pushEvent.ProjectId);
            if (project == null)
            {
                _logger.LogInformation(
                    "Project {ProjectId} not found while processing push event for branch '{BranchName}'; skipping",
                    pushEvent.ProjectId,
                    pushEvent.BranchName);

                continue;
            }

            if (ShouldSkipScheduledForDeletion(
                    pushEvent.BranchName,
                    pushEvent.ProjectId,
                    project.NameWithNamespace,
                    null,
                    "push-event processing"))
            {
                continue;
            }

            var projectNameWithNamespace = project.NameWithNamespace;
            if (string.IsNullOrWhiteSpace(project.Name))
            {
                _logger.LogError("Invalid empty project name in project id {id}", pushEvent.ProjectId);
                continue;
            }

            var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(
                pushEvent.BranchName,
                pushEvent.ProjectId,
                projectNameWithNamespace);

            var mergeGroup = _mergeGroupRepository.GetOrCreateMergeGroup(pushEvent.BranchName);
            if (!mergeGroup.Branches.Any(b => b.BranchInProjectId == branchRecord.Id))
            {
                _logger.LogDebug(
                    "Branch {BranchId} not yet in merge group {MergeGroupId}, associating",
                    branchRecord.Id,
                    mergeGroup.Id);

                _mergeGroupRepository.EnsureBranchInMergeGroup(mergeGroup.Id, branchRecord.Id);
            }
            else
            {
                _logger.LogDebug(
                    "Branch {BranchId} already in merge group {MergeGroupId}, skipping association",
                    branchRecord.Id,
                    mergeGroup.Id);
            }

            _mergeGroupRepository.EnsureUserInMergeGroup(gitlabUserId, mergeGroup.Id);

            var lastUpdated = pushEvent.CreatedAt.ToUniversalTime();
            _mergeGroupRepository.UpdateBranchTimestamp(branchRecord.Id, lastUpdated);

            _logger.LogDebug(
                "Stored branch '{BranchName}' in project {ProjectId} for user {UserId}",
                pushEvent.BranchName,
                pushEvent.ProjectId,
                gitlabUserId);
        }
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

                if (branch.BranchInProjectId.HasValue)
                {
                    RemoveBranchAndCleanup(branch.BranchInProjectId.Value);
                }
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
    ///     Returns all current branches in a specific merge group as a full snapshot.
    ///     Returns null if the merge group does not exist for the user.
    ///     Data is read from the database and includes persisted MR, approval, and build details.
    /// </summary>
    public MergeGroup? GetMergeGroupBranches(int mergeGroupId)
    {
        var mergeGroup = _mergeGroupRepository.GetMergeGroup(mergeGroupId);
        if (mergeGroup == null)
        {
            _logger.LogInformation(
                "No merge group found for merge group {MergeGroupId} during poll",
                mergeGroupId);

            return null;
        }

        _logger.LogDebug(
            "Returning {Count} branches for merge group {MergeGroupId}",
            mergeGroup.Branches.Count,
            mergeGroupId);

        return mergeGroup;
    }

    /// <summary>
    ///     Fetches MR, approval, and build job details from GitLab for the given branch
    ///     and persists them in the database. Called by the background sync thread.
    ///     Silently skips if project info is unavailable.
    /// </summary>
    public async Task RefreshBranchDetails(
        AccessDetailsBase accessDetails,
        BranchRecord branch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!branch.BranchInProjectId.HasValue)
        {
            _logger.LogWarning(
                "BranchRecord for '{BranchName}' in project {ProjectId} has no BranchInProjectId; skipping detail refresh",
                branch.BranchName,
                branch.ProjectId);

            return;
        }

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

        // TODO: Check the branch's last updated time via the Gitlab API to see when the last push was.
        // Use this time to update the LastUpdatedTime in the branch record in the database. The database
        // should not use the current time as the default value for that field. For newly discovered
        // branches, it should be null until a value is filled in from here. Make sure the UI handles
        // null date. Merge groups that have branches with null LastUpdatedTime should be sorted to the
        // top, as it implies they were more recently updated than anything displayed. The relative order
        // between merge groups that all have null LastUpdatedTime can be arbitrary.

        _mergeGroupRepository.UpdateBranchDetails(
            branch.BranchInProjectId.Value,
            hasMr,
            mrTitle,
            mrUrl,
            projectUrl,
            approvalsRequired,
            approvalsGiven,
            buildJobs);

        _logger.LogDebug(
            "Updated details for branch '{BranchName}' in project {ProjectId}: hasMr={HasMr}, {JobCount} jobs",
            branch.BranchName,
            branch.ProjectId,
            hasMr,
            buildJobs.Count);
    }

    /// <summary>
    ///     Refreshes MR, approval, and build details for all branches tracked by the given user.
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

    private async Task<bool> ShouldSkipBranchByLookup(
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

    private bool ShouldSkipScheduledForDeletion(
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