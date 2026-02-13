using Mergician.Entities;
using System.Runtime.CompilerServices;

namespace Mergician.Services.Gitlab;

/// <summary>
/// Provides activity-related operations for the current user,
/// streaming branch activity data as it is discovered from GitLab.
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
    /// Streams branch activity records as they are discovered, yielding each one
    /// as soon as the branch is confirmed to exist. MR and approval data may be
    /// populated in follow-up yields for the same branch-project combination.
    /// </summary>
    public async IAsyncEnumerable<BranchActivity> StreamBranchActivity(
        GitlabCurrentUser currentUser,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching push activity for last 14 days");
        var events = await _gitlabService.GetUserEvents(currentUser, days: 14);

        // Filter to push events with branch refs, excluding default branches
        var pushEvents = events
            .Where(e => e.PushData?.RefType == "branch" && e.PushData.Ref != null)
            .Where(e => !GitlabService.IsPossibleDefaultBranch(e.PushData!.Ref!))
            .ToList();

        _logger.LogInformation("Found {Count} push events after filtering default branches", pushEvents.Count);

        // Deduplicate by (branchName, projectId)
        var uniqueBranches = pushEvents
            .Select(e => new { BranchName = e.PushData!.Ref!, e.ProjectId })
            .Distinct()
            .ToList();

        _logger.LogInformation("Found {Count} unique branch/project combinations", uniqueBranches.Count);

        // Resolve project names
        var projectCache = new Dictionary<int, string>();
        foreach (var entry in uniqueBranches)
        {
            if (entry.ProjectId > 0 && !projectCache.ContainsKey(entry.ProjectId))
            {
                var project = await _gitlabService.GetProject(currentUser, entry.ProjectId);
                projectCache[entry.ProjectId] = project?.NameWithNamespace ?? $"Project #{entry.ProjectId}";
            }
        }

        // Track already-emitted branch-project pairs to avoid duplicates
        var emitted = new HashSet<(string BranchName, int ProjectId)>();

        foreach (var entry in uniqueBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!emitted.Add((entry.BranchName, entry.ProjectId)))
            {
                _logger.LogDebug("Skipping duplicate branch-project: '{BranchName}' in project {ProjectId}",
                    entry.BranchName, entry.ProjectId);
                continue;
            }

            // Check if the branch still exists
            var exists = await _gitlabService.BranchExists(currentUser, entry.ProjectId, entry.BranchName);
            if (!exists)
            {
                _logger.LogDebug("Skipping branch '{BranchName}' in project {ProjectId} - no longer exists",
                    entry.BranchName, entry.ProjectId);
                continue;
            }

            var projectName = projectCache.GetValueOrDefault(entry.ProjectId, $"Project #{entry.ProjectId}");

            // Yield the branch immediately with unknown MR/approval status
            _logger.LogDebug("Discovered branch '{BranchName}' in project '{ProjectName}', streaming initial record",
                entry.BranchName, projectName);

            yield return new BranchActivity(
                entry.BranchName,
                entry.ProjectId,
                projectName,
                HasMergeRequest: null,
                ApprovalsRequired: null,
                ApprovalsGiven: null);

            // Now look up MR and approval details
            var mergeRequests = await _gitlabService.GetMergeRequests(
                currentUser, entry.ProjectId, entry.BranchName);

            var hasMr = mergeRequests.Count > 0;
            int? approvalsRequired = null;
            int? approvalsGiven = null;

            if (hasMr)
            {
                _logger.LogDebug("Branch '{BranchName}' has {MrCount} open MR(s) in project {ProjectId}",
                    entry.BranchName, mergeRequests.Count, entry.ProjectId);

                var approval = await _gitlabService.GetMergeRequestApprovals(
                    currentUser, entry.ProjectId, mergeRequests[0].Iid);

                if (approval != null)
                {
                    approvalsGiven = approval.ApprovedBy.Count;
                    approvalsRequired = approvalsGiven > 0 ? approvalsGiven : null;
                }
            }

            // Yield the update with MR/approval data
            _logger.LogDebug("Streaming MR/approval update for '{BranchName}' in project '{ProjectName}': HasMR={HasMr}",
                entry.BranchName, projectName, hasMr);

            yield return new BranchActivity(
                entry.BranchName,
                entry.ProjectId,
                projectName,
                hasMr,
                approvalsRequired,
                approvalsGiven);
        }

        _logger.LogInformation("Finished streaming branch activity");
    }
}
