using Mergician.Entities.Database;
using Mergician.Services.Authentication;
using Mergician.Services.Database;

namespace Mergician.Services.GitLab;

/// <summary>
///     Provides methods for refreshing branch details against the GitLab API.
///     Used by the background sync threads managed by <see cref="UserActivitySyncService" />.
/// </summary>
public class GitLabActivityService
{
    private readonly GitLabPipelineService _gitLabPipelineService;

    private readonly GitLabService _gitLabService;

    private readonly ILogger<GitLabActivityService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    public GitLabActivityService(
        GitLabService gitLabService,
        GitLabPipelineService gitLabPipelineService,
        IMergeGroupRepository mergeGroupRepository,
        ILogger<GitLabActivityService> logger)
    {
        _gitLabService = gitLabService;
        _gitLabPipelineService = gitLabPipelineService;
        _mergeGroupRepository = mergeGroupRepository;
        _logger = logger;
    }

    /// <summary>
    ///     Refreshes MR, approval, and build job details for all branches tracked by the given user.
    ///     Called by the background sync thread as a second pass after activity sync.
    /// </summary>
    public async Task RefreshAllBranchDetails(
        AccessDetailsBase accessDetails,
        int gitLabUserId,
        CancellationToken cancellationToken)
    {
        var userGroups = _mergeGroupRepository.GetMergeGroupsForUser(gitLabUserId);
        var userBranches = userGroups.SelectMany(g => g.Branches).ToList();

        _logger.LogDebug(
            "Refreshing details for {Count} branches for user {UserId}",
            userBranches.Count,
            gitLabUserId);

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
            catch (GitLabApiFailureException ex)
            {
                _logger.LogError(
                    ex,
                    "GitLab became unavailable while refreshing branch details for user {UserId}; ending the current refresh cycle",
                    gitLabUserId);

                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to refresh details for branch '{BranchName}' in project {ProjectId}; skipping",
                    branch.BranchName,
                    branch.ProjectId);
            }
        }

        _logger.LogDebug("Finished refreshing details for user {UserId}", gitLabUserId);
    }

    /// <summary>
    ///     Fetches MR, approval, and build job details from GitLab for the given branch
    ///     and persists them in the database. Silently skips if project info is unavailable.
    /// </summary>
    private async Task RefreshBranchDetails(
        AccessDetailsBase accessDetails,
        BranchInProject branch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Refreshing details for branch '{BranchName}' in project {ProjectId}",
            branch.BranchName,
            branch.ProjectId);

        var project = await _gitLabService.GetProject(accessDetails, branch.ProjectId);
        if (project == null)
        {
            _logger.LogDebug(
                "Project {ProjectId} not found when refreshing details for '{BranchName}'; skipping",
                branch.ProjectId,
                branch.BranchName);

            return;
        }

        if (GitLabService.IsScheduledForDeletion(project.NameWithNamespace))
        {
            _logger.LogInformation(
                "Skipping detail refresh for branch '{BranchName}' in project {ProjectId}: project scheduled for deletion",
                branch.BranchName,
                branch.ProjectId);

            return;
        }

        var projectUrl = project.WebUrl;

        var mergeRequests = await _gitLabService.GetMergeRequests(
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

            var approval = await _gitLabService.GetMergeRequestApprovals(
                accessDetails,
                branch.ProjectId,
                first.Iid);

            if (approval != null)
            {
                approvalsGiven = approval.ApprovedBy.Count;
                approvalsRequired = Math.Max(approval.ApprovalsRequired ?? 0, 0);
            }
        }

        var buildJobs = await _gitLabPipelineService.GetLatestExternalJobsForBranch(
            accessDetails,
            branch.ProjectId,
            branch.BranchName,
            cancellationToken);

        // Fetch the branch's latest commit date from GitLab to use as the last updated timestamp
        var branchDetails = await _gitLabService.GetBranchDetails(
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
}