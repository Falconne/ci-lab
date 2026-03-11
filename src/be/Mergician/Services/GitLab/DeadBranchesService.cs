using Mergician.Services.Authentication;
using Mergician.Services.Database;

namespace Mergician.Services.GitLab;

/// <summary>
///     Provides helper methods for deciding whether a branch should be skipped
///     during sync or processing, and for removing stale branch records from the database.
/// </summary>
public class DeadBranchesService
{
    private readonly GitLabService _gitLabService;

    private readonly ILogger<DeadBranchesService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    public DeadBranchesService(
        GitLabService gitLabService,
        IMergeGroupRepository mergeGroupRepository,
        ILogger<DeadBranchesService> logger)
    {
        _gitLabService = gitLabService;
        _mergeGroupRepository = mergeGroupRepository;
        _logger = logger;
    }

    /// <summary>
    ///     Checks if a branch should be skipped by looking it up in GitLab.
    ///     If the branch no longer exists and a tracked record ID is provided, removes it from the DB.
    ///     Returns true if the branch should be skipped.
    /// </summary>
    public async Task<bool> ShouldSkipByLookup(
        AccessDetailsBase accessDetails,
        string branchName,
        int projectId,
        int? trackedBranchInProjectId,
        string operationName,
        CancellationToken cancellationToken)
    {
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