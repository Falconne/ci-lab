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
    private const int MinMergeAccessLevel = 30;

    private const int MinViewAccessLevel = 20;

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

    public async Task<MergePermissionsResponse> CheckMergePermissions(
        UserAccessDetails userAccessDetails,
        int mergeGroupId,
        CancellationToken cancellationToken = default)
    {
        var mergeGroup = _mergeGroupRepository.GetMergeGroup(mergeGroupId);
        if (mergeGroup == null)
        {
            _logger.LogError("Merge group {MergeGroupId} not found during permissions check", mergeGroupId);
            return new MergePermissionsResponse(false, true, []);
        }

        var (blockedProjects, checkFailed) = await GetProjectsWithInsufficientAccess(
            userAccessDetails, mergeGroup, MinMergeAccessLevel, "merge", cancellationToken);

        var canMerge = blockedProjects.Count == 0 && !checkFailed;
        _logger.LogInformation(
            "Merge permission check complete: user {UserId}, merge group {MergeGroupId}, canMerge={CanMerge}, checkFailed={CheckFailed}, blocked=[{BlockedProjects}]",
            userAccessDetails.UserId,
            mergeGroupId,
            canMerge,
            checkFailed,
            string.Join(", ", blockedProjects));

        return new MergePermissionsResponse(canMerge, checkFailed, blockedProjects);
    }

    /// <summary>
    ///     Checks whether the current user has at least Reporter access (level 20) in all projects
    ///     belonging to the given merge group.
    /// </summary>
    public async Task<ViewPermissionsResult> CheckViewPermissions(
        UserAccessDetails userAccessDetails,
        MergeGroup mergeGroup,
        CancellationToken cancellationToken = default)
    {
        var (deniedProjects, checkFailed) = await GetProjectsWithInsufficientAccess(
            userAccessDetails, mergeGroup, MinViewAccessLevel, "view", cancellationToken);

        var canView = deniedProjects.Count == 0;
        _logger.LogInformation(
            "View permission check complete: user {UserId}, merge group {MergeGroupId}, canView={CanView}, checkFailed={CheckFailed}, denied=[{DeniedProjects}]",
            userAccessDetails.UserId,
            mergeGroup.Id,
            canView,
            checkFailed,
            string.Join(", ", deniedProjects));

        return new ViewPermissionsResult(canView, checkFailed, deniedProjects);
    }

    private async Task<(List<string> RestrictedProjects, bool CheckFailed)> GetProjectsWithInsufficientAccess(
        UserAccessDetails userAccessDetails,
        MergeGroup mergeGroup,
        int minAccessLevel,
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

            if (accessLevel < minAccessLevel)
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

        return (restrictedProjects, checkFailed);
    }
}
