using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.GitLab;
using System.Net;
using System.Text.Json;
using Util;

namespace Mergician.Services;

/// <summary>
///     Fetches up-to-date MR, approval, and build job details from GitLab for one or more
///     branches and persists them in the database. Used by both the background sync thread
///     and on-demand force-refresh triggered when a user opens the merge group details page.
/// </summary>
public class BranchDetailsRefreshService(
    GitLabService gitLabService,
    GitLabPipelineService gitLabPipelineService,
    DeadBranchesService deadBranchesService,
    IMergeGroupRepository mergeGroupRepository,
    ILogger<BranchDetailsRefreshService> logger)
{
    /// <summary>
    ///     Refreshes details for every branch in the given list.
    ///     Skips individual branches on error; propagates <see cref="GitLabStartupRequiredException" />
    ///     so callers can react to GitLab going unavailable mid-cycle.
    /// </summary>
    public async Task RefreshBranches(
        UserAccessDetails userAccessDetails,
        IEnumerable<BranchInProject> branches,
        CancellationToken cancellationToken)
    {
        foreach (var branch in branches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await RefreshBranchDetails(userAccessDetails, branch, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (GitLabStartupRequiredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to refresh details for branch '{BranchName}' in project {ProjectId}; skipping",
                    branch.BranchName,
                    branch.ProjectId);
            }
        }
    }

    /// <summary>
    ///     Fetches MR, approval, and build job details from GitLab for the given branch
    ///     and persists them in the database.
    /// </summary>
    public async Task RefreshBranchDetails(
        UserAccessDetails userAccessDetails,
        BranchInProject branch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogDebug(
            "Refreshing details for branch '{BranchName}' in project {ProjectId}",
            branch.BranchName,
            branch.ProjectId);

        var project = await gitLabService.GetProject(userAccessDetails, branch.ProjectId, cancellationToken);
        if (project == null)
        {
            logger.LogDebug(
                "Project {ProjectId} not found when refreshing details for '{BranchName}'; skipping",
                branch.ProjectId,
                branch.BranchName);

            return;
        }

        if (DeadBranchesService.IsScheduledForDeletion(project.NameWithNamespace))
        {
            logger.LogInformation(
                "Skipping detail refresh for branch '{BranchName}' in project {ProjectId}: project scheduled for deletion",
                branch.BranchName,
                branch.ProjectId);

            return;
        }

        List<GitLabMergeRequest> mergeRequests;
        try
        {
            mergeRequests = await gitLabService.GetOpenMergeRequestsForBranch(
                userAccessDetails,
                branch.ProjectId,
                branch.BranchName,
                cancellationToken);
        }
        catch (GitLabUnexpectedResponseException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            logger.LogWarning(
                "Skipping branch '{BranchName}' in project {ProjectId}: received 401 (token may be expired)",
                branch.BranchName,
                branch.ProjectId);

            return;
        }

        var mr = mergeRequests.FirstOrDefault();
        int? approvalsRequired = null;
        int? approvalsGiven = null;
        string? mergeRequestTitle = null;
        string? mergeRequestUrl = null;
        bool? needsRebase = null;

        if (mr != null)
        {
            mergeRequestTitle = mr.Title;
            mergeRequestUrl = mr.WebUrl;
            needsRebase = mr.DetailedMergeStatus == "need_rebase";
        }

        var approvalCountsTask = mr != null
            ? gitLabService.GetMergeRequestApprovalCounts(
                userAccessDetails,
                branch.ProjectId,
                mr.Iid,
                cancellationToken)
            : Task.FromResult<(int Required, int Given)?>(null);

        var buildJobsTask = gitLabPipelineService.GetLatestBuildJobsForBranch(
            userAccessDetails,
            branch.ProjectId,
            branch.BranchName,
            cancellationToken);

        // Fetch the branch's latest commit date from GitLab to use as the last updated timestamp
        var branchDetailsTask = gitLabService.GetBranchDetails(
            userAccessDetails,
            branch.ProjectId,
            branch.BranchName,
            cancellationToken);

        await Task.WhenAll(approvalCountsTask, buildJobsTask, branchDetailsTask);

        var approvalCounts = approvalCountsTask.Result;
        if (approvalCounts != null)
        {
            approvalsRequired = approvalCounts.Value.Required;
            approvalsGiven = approvalCounts.Value.Given;
        }

        var buildJobs = buildJobsTask.Result;

        // Can't currently support intra-group MR dependencies, because there's no way to tell if that
        // is the only reason an MR is blocked. We would have to check all other known blocking conditions
        // which should be done at some point.
        // See https://docs.gitlab.com/api/merge_requests/#merge-status
        // Known blockers are:
        // * Rebase needed / Merge conflicts
        // * CI pipeline failures
        // * Not enough approvals
        // * Discussions not resolved
        // * MR is in Draft

        //if (mr?.DetailedMergeStatus == "blocked_status")
        //{
        //    var resolved = await ResolveBlockingMRDescriptions(
        //        userAccessDetails,
        //        branch,
        //        mr.Iid,
        //        groupSiblings,
        //        cancellationToken);

        //    // null means the endpoint is unavailable (GitLab CE / non-Premium); use a generic reason
        //    blockingMRDescriptions =
        //        resolved ?? ["Blocked by a dependency (details unavailable on this GitLab tier)"];
        //}

        var (branchDetails, status) = branchDetailsTask.Result;

        if (status == GitLabBranchLookupStatus.Missing)
        {
            logger.LogInformation(
                "Branch '{BranchName}' in project {ProjectId} not found, removing from database",
                branch.BranchName,
                branch.ProjectId);

            deadBranchesService.RemoveBranchAndCleanup(branch.Id);
            return;
        }

        if (status == GitLabBranchLookupStatus.Unavailable)
        {
            logger.LogWarning(
                "Branch '{BranchName}' in project {ProjectId} cannot be fetched, ignoring this branch",
                branch.BranchName,
                branch.ProjectId);

            return;
        }

        DateTimeOffset? lastCommitTime;
        var lastCommitMessage = branchDetails?.Commit?.Title;
        if (branchDetails?.Commit?.CommittedDate != null)
        {
            lastCommitTime = branchDetails.Commit.CommittedDate.Value.ToUniversalTime();
            logger.LogDebug(
                "Branch '{BranchName}' in project {ProjectId}: latest commit at {CommitTime}",
                branch.BranchName,
                branch.ProjectId,
                lastCommitTime);
        }
        else
        {
            logger.LogWarning(
                "Branch '{BranchName}' in project {ProjectId} has no last commit time",
                branch.BranchName,
                branch.ProjectId);

            return;
        }

        var (mrStatus, reason) =
            MergeRequestStatusCalculator.Calculate(
                mr != null ? mergeRequests[0].DetailedMergeStatus : null);

        var reasons = new List<string>();
        if (reason != null)
        {
            reasons.Add(reason);
        }

        var mrStatusReasons = reasons.Count > 0 ? JsonSerializer.Serialize(reasons) : null;

        logger.LogDebug(
            "Branch '{BranchName}' in project {ProjectId}: computed mrStatus={MRStatus}, reasons={Reasons}",
            branch.BranchName,
            branch.ProjectId,
            mrStatus,
            mrStatusReasons);

        mergeGroupRepository.UpdateBranchDetails(
            branch.Id,
            new BranchDetailsUpdate(
                mr != null,
                mergeRequestTitle,
                mergeRequestUrl,
                project.WebUrl,
                approvalsRequired,
                approvalsGiven,
                buildJobs,
                needsRebase,
                lastCommitTime,
                lastCommitMessage,
                mrStatus,
                mrStatusReasons));

        logger.LogDebug(
            "Updated details for branch '{BranchName}' in project {ProjectId}: hasMergeRequest={HasMergeRequest}, {JobCount} jobs",
            branch.BranchName,
            branch.ProjectId,
            mr != null,
            buildJobs.Count);
    }

    /// <summary>
    ///     Fetches the blocking MRs for the given MR and returns human-readable descriptions
    ///     for those that are outside the current merge group (external blockers).
    ///     Intra-group blocking MRs are intentionally excluded because Mergician handles merge
    ///     ordering automatically. Returns null when the blocking MR endpoint is unavailable
    ///     (GitLab CE / non-Premium), which signals to the caller to use a generic fallback.
    ///     Returns an empty list when the endpoint is available but no external blockers exist.
    ///     Currently not used until MR blockers can be checked for reliably.
    /// </summary>
#pragma warning disable IDE0051
    private async Task<List<string>?> ResolveBlockingMRDescriptions(
#pragma warning restore IDE0051
        UserAccessDetails userAccessDetails,
        BranchInProject branch,
        int mrIid,
        IReadOnlyList<BranchWithActivity> groupSiblings,
        CancellationToken cancellationToken)
    {
        var blockingMRs = await gitLabService.GetBlockingMergeRequests(
            userAccessDetails,
            branch.ProjectId,
            mrIid,
            cancellationToken);

        if (blockingMRs == null)
        {
            logger.LogDebug(
                "Branch '{BranchName}' in project {ProjectId}: blocking MRs endpoint unavailable, using generic block reason",
                branch.BranchName,
                branch.ProjectId);

            return null;
        }

        if (blockingMRs.Count == 0)
        {
            logger.LogDebug(
                "Branch '{BranchName}' in project {ProjectId}: blocked_status but no blocking MRs returned",
                branch.BranchName,
                branch.ProjectId);

            return [];
        }

        var descriptions = new List<string>();
        foreach (var blocker in blockingMRs)
        {
            var isIntraGroup = groupSiblings.Any(s => s.ProjectId == blocker.ProjectId
                                                      && string.Equals(
                                                          s.BranchName,
                                                          blocker.SourceBranch,
                                                          StringComparison.OrdinalIgnoreCase));

            if (isIntraGroup)
            {
                logger.LogInformation(
                    "Branch '{BranchName}' in project {ProjectId}: blocked by intra-group MR !{BlockerIid} '{BlockerTitle}'; Mergician handles ordering",
                    branch.BranchName,
                    branch.ProjectId,
                    blocker.Iid,
                    blocker.Title);

                continue;
            }

            logger.LogInformation(
                "Branch '{BranchName}' in project {ProjectId}: blocked by external MR !{BlockerIid} '{BlockerTitle}'",
                branch.BranchName,
                branch.ProjectId,
                blocker.Iid,
                blocker.Title);

            var description = blocker.WebUrl.IsNotEmpty()
                ? $"Blocked by MR: {blocker.Title} ({blocker.WebUrl})"
                : $"Blocked by MR: {blocker.Title}";

            descriptions.Add(description);
        }

        return descriptions;
    }
}