using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.GitLab;

namespace Mergician.Services;

/// <summary>
///     Service for checking if a user has permissions to view and merge an MR.
///     GitLab Developer access (level 30) or above is required to merge MRs in a project.
///     Reporter access (level 20) or above is required to view a merge group.
/// </summary>
public class MergePermissionService
{
    private readonly GitLabService _gitLabService;

    private readonly ILogger<MergePermissionService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    public MergePermissionService(
        GitLabService gitLabService,
        IMergeGroupRepository mergeGroupRepository,
        ILogger<MergePermissionService> logger)
    {
        _gitLabService = gitLabService;
        _mergeGroupRepository = mergeGroupRepository;
        _logger = logger;
    }

    public async Task<PermissionsCheckResult> CheckMergePermissions(
        UserAccessDetails userAccessDetails,
        int mergeGroupId,
        CancellationToken cancellationToken = default)
    {
        var mergeGroup = _mergeGroupRepository.GetMergeGroup(mergeGroupId);
        if (mergeGroup == null)
        {
            _logger.LogError("Merge group {MergeGroupId} not found during permissions check", mergeGroupId);
            return new PermissionsCheckResult(false, true, []);
        }

        return await GetProjectsWithInsufficientAccess(
            userAccessDetails,
            mergeGroup,
            GitLabAccessLevel.Developer,
            "merge",
            cancellationToken);
    }

    /// <summary>
    ///     Checks whether the current user has at least Reporter access (level 20) in all projects
    ///     belonging to the given merge group.
    /// </summary>
    public async Task<PermissionsCheckResult> CheckViewPermissions(
        UserAccessDetails userAccessDetails,
        MergeGroup mergeGroup,
        CancellationToken cancellationToken = default)
    {
        return await GetProjectsWithInsufficientAccess(
            userAccessDetails,
            mergeGroup,
            GitLabAccessLevel.Reporter,
            "view",
            cancellationToken);
    }

    private async Task<PermissionsCheckResult> GetProjectsWithInsufficientAccess(
        UserAccessDetails userAccessDetails,
        MergeGroup mergeGroup,
        GitLabAccessLevel minAccessLevel,
        string permissionType,
        CancellationToken cancellationToken)
    {
        var uniqueProjectIds = mergeGroup.Branches
            .Select(b => b.ProjectId)
            .Distinct()
            .ToList();

        _logger.LogDebug(
            "Checking {PermissionType} permissions for user {UserId} in {Count} projects of merge group {MergeGroupId}",
            permissionType,
            userAccessDetails.UserId,
            uniqueProjectIds.Count,
            mergeGroup.Id);

        var restrictedProjects = new List<string>();
        var checkFailed = false;

        foreach (var projectId in uniqueProjectIds)
        {
            var accessLevel = await _gitLabService.GetUserProjectAccessLevel(
                userAccessDetails,
                projectId,
                userAccessDetails.UserId,
                cancellationToken);

            if (accessLevel == null)
            {
                _logger.LogError(
                    "Could not verify {PermissionType} access level for user {UserId} in project {ProjectId}",
                    permissionType,
                    userAccessDetails.UserId,
                    projectId);

                checkFailed = true;
                continue;
            }

            if (accessLevel < (int)minAccessLevel)
            {
                var projectName = mergeGroup.Branches.First(b => b.ProjectId == projectId).ProjectName;
                _logger.LogInformation(
                    "User {UserId} does not have {PermissionType} access in project {ProjectId} '{ProjectName}' (access level {AccessLevel})",
                    userAccessDetails.UserId,
                    permissionType,
                    projectId,
                    projectName,
                    accessLevel);

                restrictedProjects.Add(projectName);
            }
        }

        var hasPermission = restrictedProjects.Count == 0 && !checkFailed;
        return new PermissionsCheckResult(hasPermission, checkFailed, restrictedProjects);
    }
}