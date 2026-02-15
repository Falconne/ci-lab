using Mergician.Entities;
using System.Runtime.CompilerServices;

namespace Mergician.Services.Gitlab;

/// <summary>
///     Provides activity-related operations for the current user,
///     streaming branch activity data as it is discovered from GitLab.
/// </summary>
public class GitlabActivityService
{
    private readonly GitlabService _gitlabService;

    private readonly ILogger<GitlabActivityService> _logger;

    public GitlabActivityService(GitlabService gitlabService, ILogger<GitlabActivityService> logger)
    {
        _gitlabService = gitlabService;
        _logger = logger;
    }

    /// <summary>
    ///     Streams branch activity records as they are discovered, yielding each one
    ///     as soon as the branch is confirmed to exist. MR and approval data may be
    ///     populated in follow-up yields for the same branch-project combination.
    /// </summary>
    public async IAsyncEnumerable<BranchActivity> StreamBranchActivity(
        GitlabCurrentUser currentUser,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching push activity for last 14 days");
        var events = await _gitlabService.GetUserEventsSince(currentUser, DateTime.UtcNow.AddDays(-14));

        await foreach (var branch in DiscoverBranches(currentUser, events, cancellationToken))
        {
            // Yield the branch immediately with unknown MR/approval status
            _logger.LogDebug(
                "Discovered branch '{BranchName}' in project '{ProjectName}', streaming initial record",
                branch.BranchName,
                branch.ProjectName);

            yield return branch;

            // Yield the update with resolved MR/approval data
            yield return await ResolveBranchActivity(
                currentUser,
                branch.BranchName,
                branch.ProjectId,
                branch.ProjectName);
        }

        _logger.LogInformation("Finished streaming branch activity");
    }

    /// <summary>
    ///     Returns branch activity records for events that occurred since the given time.
    ///     Used for polling to detect new pushes without re-fetching the full history.
    ///     Returns fully resolved records (MR and approval data included).
    /// </summary>
    public async Task<List<BranchActivity>> GetActivitySince(
        GitlabCurrentUser currentUser,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Polling for activity since {Since}", since);
        var events = await _gitlabService.GetUserEventsSince(currentUser, since);

        var results = new List<BranchActivity>();

        await foreach (var branch in DiscoverBranches(currentUser, events, cancellationToken))
        {
            var activity = await ResolveBranchActivity(
                currentUser,
                branch.BranchName,
                branch.ProjectId,
                branch.ProjectName);

            _logger.LogDebug(
                "Poll found activity for '{BranchName}' in '{ProjectName}': HasMR={HasMr}",
                branch.BranchName,
                branch.ProjectName,
                activity.HasMergeRequest);

            results.Add(activity);
        }

        _logger.LogInformation("Returning {Count} branch activity records from poll", results.Count);
        return results;
    }

    /// <summary>
    ///     Refreshes MR and approval status for specific branch-project pairs.
    ///     Used to update the dashboard when existing rows may have changed.
    /// </summary>
    public async Task<List<BranchActivity>> RefreshBranchStatus(
        GitlabAccessUserBase user,
        List<BranchRefreshRequest> branches,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Refreshing status for {Count} branch-project pairs", branches.Count);

        var results = new List<BranchActivity>();

        foreach (var branch in branches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var project = await _gitlabService.GetProject(user, branch.ProjectId);
            var projectName = project?.NameWithNamespace ?? $"Project #{branch.ProjectId}";

            var activity = await ResolveBranchActivity(
                user,
                branch.BranchName,
                branch.ProjectId,
                projectName);

            results.Add(activity);
        }

        _logger.LogInformation("Returning {Count} refreshed branch activity records", results.Count);
        return results;
    }

    /// <summary>
    ///     Discovers branches from events that still exist, yielding partially filled
    ///     BranchActivity records (with null MR/approval data) for each valid branch.
    ///     This is the common logic shared by StreamBranchActivity and GetActivitySince.
    /// </summary>
    private async IAsyncEnumerable<BranchActivity> DiscoverBranches(
        GitlabAccessUserBase user,
        List<GitLabEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var activeBranches = ExtractActiveBranches(events);
        _logger.LogInformation("Found {Count} unique branch/project combinations", activeBranches.Count);

        var emitted = new HashSet<(string BranchName, int ProjectId)>();

        foreach (var entry in activeBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!emitted.Add((entry.BranchName, entry.ProjectId)))
            {
                _logger.LogDebug(
                    "Skipping duplicate branch-project: '{BranchName}' in project {ProjectId}",
                    entry.BranchName,
                    entry.ProjectId);

                continue;
            }

            var exists = await _gitlabService.BranchExists(user, entry.ProjectId, entry.BranchName);
            if (!exists)
            {
                _logger.LogDebug(
                    "Skipping branch '{BranchName}' in project {ProjectId} - no longer exists",
                    entry.BranchName,
                    entry.ProjectId);

                continue;
            }

            var project = await _gitlabService.GetProject(user, entry.ProjectId);
            var projectName = project?.NameWithNamespace ?? $"Project #{entry.ProjectId}";

            yield return new BranchActivity(
                entry.BranchName,
                entry.ProjectId,
                projectName,
                null,
                null,
                null);
        }
    }

    /// <summary>
    ///     Extracts distinct branch-project pairs from push events, excluding default branches.
    /// </summary>
    private static List<(string BranchName, int ProjectId)> ExtractActiveBranches(List<GitLabEvent> events)
    {
        return events
            .Where(e => e.PushData is { RefType: "branch", Ref: not null })
            .Where(e => !GitlabService.IsPossibleDefaultBranch(e.PushData!.Ref!))
            .Select(e => (BranchName: e.PushData!.Ref!, e.ProjectId))
            .Distinct()
            .ToList();
    }

    /// <summary>
    ///     Resolves a branch's MR and approval status into a fully populated BranchActivity record.
    /// </summary>
    private async Task<BranchActivity> ResolveBranchActivity(
        GitlabAccessUserBase user,
        string branchName,
        int projectId,
        string projectName)
    {
        var mergeRequests = await _gitlabService.GetMergeRequests(user, projectId, branchName);

        var hasMr = mergeRequests.Count > 0;
        int? approvalsRequired = null;
        int? approvalsGiven = null;

        if (hasMr)
        {
            _logger.LogDebug(
                "Branch '{BranchName}' has {MrCount} open MR(s) in project {ProjectId}",
                branchName,
                mergeRequests.Count,
                projectId);

            var approval = await _gitlabService.GetMergeRequestApprovals(
                user,
                projectId,
                mergeRequests[0].Iid);

            if (approval != null)
            {
                approvalsGiven = approval.ApprovedBy.Count;
                approvalsRequired = approvalsGiven > 0 ? approvalsGiven : null;
            }
        }

        _logger.LogDebug(
            "Resolved activity for '{BranchName}' in '{ProjectName}': HasMR={HasMr}",
            branchName,
            projectName,
            hasMr);

        return new BranchActivity(
            branchName,
            projectId,
            projectName,
            hasMr,
            approvalsRequired,
            approvalsGiven);
    }
}