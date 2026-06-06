using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.GitLab;

namespace Mergician.Services;

/// <summary>
///     Checks whether the current user has merge permissions for all projects in a merge group.
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
        AccessDetailsForUser accessDetails,
        int mergeGroupId,
        CancellationToken cancellationToken = default)
    {
        var mergeGroup = _mergeGroupRepository.GetMergeGroup(mergeGroupId);
        if (mergeGroup == null)
        {
            _logger.LogError("Merge group {MergeGroupId} not found during permissions check", mergeGroupId);
            return new MergePermissionsResponse(false, true, []);
        }

        var uniqueProjectIds = mergeGroup.Branches
            .Select(b => b.ProjectId)
            .Distinct()
            .ToList();

        _logger.LogDebug(
            "Checking merge permissions for user {UserId} in {Count} projects of merge group {MergeGroupId}",
            accessDetails.UserId,
            uniqueProjectIds.Count,
            mergeGroupId);

        var blockedProjects = new List<string>();
        var checkFailed = false;

        foreach (var projectId in uniqueProjectIds)
        {
            var accessLevel = await _gitLabService.GetUserProjectAccessLevel(
                accessDetails,
                projectId,
                accessDetails.UserId,
                cancellationToken);

            if (accessLevel == null)
            {
                _logger.LogError(
                    "Could not verify access level for user {UserId} in project {ProjectId}",
                    accessDetails.UserId,
                    projectId);

                checkFailed = true;
                continue;
            }

            if (accessLevel < MinMergeAccessLevel)
            {
                var projectName = mergeGroup.Branches.First(b => b.ProjectId == projectId).ProjectName;
                _logger.LogInformation(
                    "User {UserId} cannot merge in project {ProjectId} '{ProjectName}' (access level {AccessLevel})",
                    accessDetails.UserId,
                    projectId,
                    projectName,
                    accessLevel);

                blockedProjects.Add(projectName);
            }
        }

        var canMerge = blockedProjects.Count == 0 && !checkFailed;
        _logger.LogInformation(
            "Merge permission check complete: user {UserId}, merge group {MergeGroupId}, canMerge={CanMerge}, checkFailed={CheckFailed}, blocked=[{BlockedProjects}]",
            accessDetails.UserId,
            mergeGroupId,
            canMerge,
            checkFailed,
            string.Join(", ", blockedProjects));

        return new MergePermissionsResponse(canMerge, checkFailed, blockedProjects);
    }

    /// <summary>
    ///     Checks whether the current user has at least Reporter access (level 20) in all projects
    ///     belonging to the given merge group.
    ///     Fails open on API errors to avoid blocking users due to transient GitLab unavailability.
    /// </summary>
    public async Task<ViewPermissionsResult> CheckViewPermissions(
        AccessDetailsForUser accessDetails,
        MergeGroup mergeGroup,
        CancellationToken cancellationToken = default)
    {
        var uniqueProjectIds = mergeGroup.Branches
            .Select(b => b.ProjectId)
            .Distinct()
            .ToList();

        _logger.LogDebug(
            "Checking view permissions for user {UserId} in {Count} projects of merge group {MergeGroupId}",
            accessDetails.UserId,
            uniqueProjectIds.Count,
            mergeGroup.Id);

        var deniedProjects = new List<string>();
        var checkFailed = false;

        foreach (var projectId in uniqueProjectIds)
        {
            var accessLevel = await _gitLabService.GetUserProjectAccessLevel(
                accessDetails,
                projectId,
                accessDetails.UserId,
                cancellationToken);

            if (accessLevel == null)
            {
                _logger.LogError(
                    "Could not verify view access level for user {UserId} in project {ProjectId}; failing open",
                    accessDetails.UserId,
                    projectId);

                checkFailed = true;
                continue;
            }

            if (accessLevel < MinViewAccessLevel)
            {
                var projectName = mergeGroup.Branches.First(b => b.ProjectId == projectId).ProjectName;
                _logger.LogInformation(
                    "User {UserId} cannot view project {ProjectId} '{ProjectName}' (access level {AccessLevel})",
                    accessDetails.UserId,
                    projectId,
                    projectName,
                    accessLevel);

                deniedProjects.Add(projectName);
            }
        }

        var canView = deniedProjects.Count == 0;
        _logger.LogInformation(
            "View permission check complete: user {UserId}, merge group {MergeGroupId}, canView={CanView}, checkFailed={CheckFailed}, denied=[{DeniedProjects}]",
            accessDetails.UserId,
            mergeGroup.Id,
            canView,
            checkFailed,
            string.Join(", ", deniedProjects));

        return new ViewPermissionsResult(canView, checkFailed, deniedProjects);
    }
}