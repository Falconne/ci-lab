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
    ///     Returns true if the project or group name indicates it is scheduled for deletion.
    ///     GitLab renames groups and their projects to include "deletion_scheduled" in the
    ///     namespace during its asynchronous deletion process.
    /// </summary>
    public static bool IsScheduledForDeletion(string nameWithNamespace)
    {
        return nameWithNamespace.Contains("deletion_scheduled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Checks if a branch should be removed because it is missing from GitLab,
    ///     or because its project has been deleted or is scheduled for deletion.
    ///     If the branch should be removed, removes it from the database and cleans up empty merge groups.
    ///     Returns true if the branch was removed or should be skipped; false if it should be kept.
    /// </summary>
    public async Task<bool> RemoveBranchIfGone(
        string branchName,
        int projectId,
        int trackedBranchInProjectId,
        CancellationToken cancellationToken)
    {
        if (await IsBranchGone(branchName, projectId, cancellationToken))
        {
            _logger.LogInformation(
                "Branch '{BranchName}' in project {ProjectId} no longer exists, removing",
                branchName,
                projectId);

            RemoveBranchAndCleanup(trackedBranchInProjectId);
            return true;
        }

        var accessDetails = _userFactory.GetServiceUser();

        cancellationToken.ThrowIfCancellationRequested();

        var project = await _gitLabService.GetProject(accessDetails, projectId, cancellationToken);

        if (project == null)
        {
            _logger.LogInformation(
                "Project {ProjectId} not found for branch '{BranchName}'; removing branch record",
                projectId,
                branchName);

            RemoveBranchAndCleanup(trackedBranchInProjectId);
            return true;
        }

        if (IsScheduledForDeletion(project.NameWithNamespace))
        {
            _logger.LogInformation(
                "Project {ProjectId} ('{NameWithNamespace}') is scheduled for deletion; removing branch '{BranchName}'",
                projectId,
                project.NameWithNamespace,
                branchName);

            RemoveBranchAndCleanup(trackedBranchInProjectId);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Checks if a branch is deleted/gone by looking it up in GitLab.
    /// </summary>
    public async Task<bool> IsBranchGone(
        string branchName,
        int projectId,
        CancellationToken cancellationToken)
    {
        var accessDetails = _userFactory.GetServiceUser();

        cancellationToken.ThrowIfCancellationRequested();

        var branchLookup = await _gitLabService.GetBranchLookupResult(
            accessDetails,
            projectId,
            branchName,
            cancellationToken);

        if (branchLookup.IsMissing)
        {
            return true;
        }

        if (branchLookup.IsUnavailable)
        {
            _logger.LogError(
                "'{BranchName}' in project {ProjectId} is unavailable in Gitlab API",
                branchName,
                projectId);

            return true;
        }

        return false;
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