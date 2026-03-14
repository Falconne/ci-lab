using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.GitLab;

namespace Mergician.Services;

/// <summary>
///     Provides helper methods for deciding whether a branch should be skipped
///     during sync or processing, and for removing stale branch records from the database.
///     Always uses the GitLab service user for API access.
/// </summary>
public class DeadBranchesService
{
    private readonly GitLabService _gitLabService;

    private readonly ILogger<DeadBranchesService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    private readonly GitLabUserFactory _userFactory;

    public DeadBranchesService(
        GitLabService gitLabService,
        GitLabUserFactory userFactory,
        IMergeGroupRepository mergeGroupRepository,
        ILogger<DeadBranchesService> logger)
    {
        _gitLabService = gitLabService;
        _userFactory = userFactory;
        _mergeGroupRepository = mergeGroupRepository;
        _logger = logger;
    }

    /// <summary>
    ///     Checks if a branch should be removed because it is missing from GitLab, has no file
    ///     differences from the project's default branch (squash-merged or no work), or has a
    ///     merged MR with no corresponding open MR (standard merge where branch is behind default).
    ///     If the branch should be removed, removes it from the database and cleans up empty merge groups.
    ///     Returns true if the branch was removed or should be skipped; false if it has changes and should be kept.
    /// </summary>
    public async Task<bool> ShouldRemoveAsInactiveOrMissing(
        string branchName,
        int projectId,
        int trackedBranchInProjectId,
        CancellationToken cancellationToken)
    {
        var accessDetails = _userFactory.GetServiceUser();

        cancellationToken.ThrowIfCancellationRequested();

        var lookup = await _gitLabService.GetBranchLookupResult(accessDetails, projectId, branchName);

        if (lookup.IsMissing)
        {
            _logger.LogInformation(
                "Branch '{BranchName}' in project {ProjectId} no longer exists, removing",
                branchName,
                projectId);

            RemoveBranchAndCleanup(trackedBranchInProjectId);
            return true;
        }

        if (lookup.IsUnavailable)
        {
            _logger.LogError(
                "Branch lookup unavailable for '{BranchName}' in project {ProjectId}; skipping this cycle",
                branchName,
                projectId);

            return true;
        }

        var project = await _gitLabService.GetProject(accessDetails, projectId);
        if (project == null)
        {
            _logger.LogError(
                "Cannot check diffs for branch '{BranchName}' in project {ProjectId}: project not found; skipping",
                branchName,
                projectId);

            return true;
        }

        if (string.IsNullOrEmpty(project.DefaultBranch))
        {
            _logger.LogError(
                "Cannot check diffs for branch '{BranchName}' in project {ProjectId}: project has no default branch; skipping",
                branchName,
                projectId);

            return true;
        }

        var hasDiffs = await _gitLabService.HasBranchDifferencesFromDefault(
            accessDetails,
            projectId,
            branchName,
            project.DefaultBranch);

        if (!hasDiffs)
        {
            _logger.LogInformation(
                "Branch '{BranchName}' in project {ProjectId} has no differences from '{DefaultBranch}'; treating as merged and removing",
                branchName,
                projectId,
                project.DefaultBranch);

            RemoveBranchAndCleanup(trackedBranchInProjectId);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Checks if a branch should be skipped by looking it up in GitLab.
    ///     If the branch no longer exists and a tracked record ID is provided, removes it from the DB.
    ///     Returns true if the branch should be skipped.
    /// </summary>
    public async Task<bool> ShouldSkipByLookup(
        string branchName,
        int projectId,
        int? trackedBranchInProjectId,
        string operationName,
        CancellationToken cancellationToken)
    {
        var accessDetails = _userFactory.GetServiceUser();

        cancellationToken.ThrowIfCancellationRequested();

        var branchLookup = await _gitLabService.GetBranchLookupResult(
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
            _logger.LogError(
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
        if (!GitLabService.IsScheduledForDeletion(projectNameWithNamespace))
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
    ///     Removes a branch record from the database and cleans up any merge groups that become empty.
    /// </summary>
    public void RemoveBranchAndCleanup(int branchInProjectId)
    {
        _mergeGroupRepository.RemoveBranch(branchInProjectId);

        var emptyGroups = _mergeGroupRepository.GetEmptyMergeGroups();
        foreach (var group in emptyGroups)
        {
            _logger.LogInformation(
                "Removing empty merge group {MergeGroupId} '{Name}'",
                group.Id,
                group.Name);

            _mergeGroupRepository.RemoveMergeGroup(group.Id);
        }
    }
}