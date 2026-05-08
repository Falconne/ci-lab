using Mergician.Entities;
using Mergician.Services;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.GitLab;
using System.Collections.Concurrent;
using Util;

namespace Mergician.Services.AutoMerge;

/// <summary>
///     Background service that monitors merge groups with auto merge or auto rebase enabled.
///     Runs a loop every 5 seconds to check and act on eligible merge groups:
///     - Auto Rebase: rebases branches that are behind their target branch.
///     - Auto Merge: merges all branches in a group when they are all ready.
/// </summary>
public class AutoMergeService : BackgroundService
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan _rebaseCheckInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan _rebaseCheckTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan _mergePermissionRetryInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan _mergeBackoffInitial = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan _mergeBackoffMax = TimeSpan.FromMinutes(5);

    private readonly AutoMergeGitLabApiService _apiService;

    private readonly DeadBranchesService _deadBranchesService;

    private readonly GitLabRecoveryService _gitLabRecoveryService;

    private readonly GitLabService _gitLabService;

    private readonly HealthService _healthService;

    private readonly ILogger<AutoMergeService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    private readonly MergicianSettings _settings;

    private readonly GitLabUserFactory _userFactory;

    /// <summary>Per-group retry state: tracks next allowed merge attempt time and current backoff.</summary>
    private readonly ConcurrentDictionary<int, MergeGroupRetryState> _mergeGroupRetryState = new();

    public AutoMergeService(
        AutoMergeGitLabApiService apiService,
        DeadBranchesService deadBranchesService,
        GitLabService gitLabService,
        GitLabRecoveryService gitLabRecoveryService,
        GitLabUserFactory userFactory,
        HealthService healthService,
        IMergeGroupRepository mergeGroupRepository,
        MergicianSettings settings,
        ILogger<AutoMergeService> logger)
    {
        _apiService = apiService;
        _deadBranchesService = deadBranchesService;
        _gitLabService = gitLabService;
        _gitLabRecoveryService = gitLabRecoveryService;
        _userFactory = userFactory;
        _healthService = healthService;
        _mergeGroupRepository = mergeGroupRepository;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    ///     Clears the in-memory retry state for the given merge group, allowing the next
    ///     auto merge attempt to proceed immediately. Called when the user dismisses a warning
    ///     or changes auto merge settings.
    /// </summary>
    public void ResetRetryState(int mergeGroupId)
    {
        if (_mergeGroupRetryState.TryRemove(mergeGroupId, out _))
        {
            _logger.LogInformation(
                "AutoMergeService: cleared retry state for merge group {MergeGroupId}",
                mergeGroupId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForReady(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAutoMergeGroups(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (GitLabStartupRequiredException)
            {
                _logger.LogWarning("AutoMergeService: GitLab is in startup mode, pausing until ready");
                await WaitForReady(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AutoMergeService: unexpected error during processing");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        _logger.LogInformation("AutoMergeService stopped");
    }

    private async Task ProcessAutoMergeGroups(CancellationToken cancellationToken)
    {
        if (_gitLabRecoveryService.IsInGitLabRecoveryMode)
        {
            _logger.LogDebug("AutoMergeService: skipping cycle, GitLab recovery mode is active");
            return;
        }

        var mergeGroups = _mergeGroupRepository.GetMergeGroupsWithAutoSettings();
        if (mergeGroups.Count == 0)
        {
            return;
        }

        _logger.LogDebug(
            "AutoMergeService: processing {Count} merge groups with auto settings",
            mergeGroups.Count);

        var serviceUser = _userFactory.GetServiceUser();

        foreach (var group in mergeGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ProcessMergeGroup(serviceUser, group, cancellationToken);
            }
            catch (GitLabStartupRequiredException)
            {
                throw;
            }
        }
    }

    private async Task ProcessMergeGroup(
        AccessDetailsBase serviceUser,
        MergeGroup group,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "AutoMergeService: processing merge group {MergeGroupId} '{MergeGroupName}' (autoMerge={AutoMerge}, autoRebase={AutoRebase})",
            group.Id,
            group.Name,
            group.AutoMerge,
            group.AutoRebase);

        // Fetch open MRs for all branches in parallel, then fetch detailed info for those that have one.
        var mrFetchTasks = group.Branches.Select(async branch =>
            {
                var mrs = await _gitLabService.GetMergeRequests(
                    serviceUser,
                    branch.ProjectId,
                    branch.BranchName,
                    cancellationToken);

                if (mrs.Count == 0)
                {
                    _logger.LogDebug(
                        "AutoMergeService: branch '{BranchName}' in project {ProjectId} has no open MR, skipping",
                        branch.BranchName,
                        branch.ProjectId);
                }

                return (branch, mrs);
            })
            .ToList();

        var branchMrPairs = await Task.WhenAll(mrFetchTasks);

        var detailFetchTasks = branchMrPairs
            .Where(x => x.mrs.Count > 0)
            .Select(async x =>
            {
                var detailed = await _apiService.GetDetailedMergeRequest(
                    serviceUser,
                    x.branch.ProjectId,
                    x.mrs[0].Iid,
                    cancellationToken);

                return (x.branch, detailed);
            })
            .ToList();

        var detailResults = await Task.WhenAll(detailFetchTasks);

        var branchMergeRequestDetails = detailResults
            .Where(x => x.detailed != null)
            .Select(x => (Branch: x.branch, MergeRequest: x.detailed!))
            .ToList();

        // Step 1: Auto Rebase - rebase branches that are behind their target
        if (group.AutoRebase)
        {
            await ProcessAutoRebase(serviceUser, group, branchMergeRequestDetails, cancellationToken);
        }

        // Step 2: Auto Merge - check if all branches are ready and merge them all
        if (group.AutoMerge)
        {
            await ProcessAutoMerge(serviceUser, group, branchMergeRequestDetails, cancellationToken);
        }
    }

    private async Task ProcessAutoRebase(
        AccessDetailsBase serviceUser,
        MergeGroup group,
        List<(BranchWithActivity Branch, GitLabDetailedMergeRequest MergeRequest)> branchMergeRequestDetails,
        CancellationToken cancellationToken)
    {
        foreach (var (branch, mr) in branchMergeRequestDetails)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var needsRebase = mr.DivergedCommitsCount > 0
                              || mr.HasConflicts
                              || mr.DetailedMergeStatus == "need_rebase";

            if (!needsRebase)
            {
                _logger.LogDebug(
                    "AutoMergeService: branch '{BranchName}' in project {ProjectId} is up to date with target",
                    branch.BranchName,
                    branch.ProjectId);

                continue;
            }

            _logger.LogInformation(
                "AutoMergeService: rebasing branch '{BranchName}' in project {ProjectId} (MR !{MergeRequestIid}, divergedCommits={DivergedCommits}, hasConflicts={HasConflicts})",
                branch.BranchName,
                branch.ProjectId,
                mr.Iid,
                mr.DivergedCommitsCount,
                mr.HasConflicts);

            var rebaseInitiated = await _apiService.RebaseMergeRequest(
                serviceUser,
                branch.ProjectId,
                mr.Iid,
                cancellationToken);

            if (!rebaseInitiated)
            {
                _logger.LogWarning(
                    "AutoMergeService: rebase could not be initiated for branch '{BranchName}' in project {ProjectId}",
                    branch.BranchName,
                    branch.ProjectId);

                continue;
            }

            // Poll until GitLab finishes the rebase (or timeout), then check for conflicts
            var rebaseDeadline = DateTimeOffset.UtcNow + _rebaseCheckTimeout;
            GitLabDetailedMergeRequest? updatedMergeRequest = null;

            while (DateTimeOffset.UtcNow < rebaseDeadline)
            {
                updatedMergeRequest = await _apiService.GetDetailedMergeRequest(
                    serviceUser,
                    branch.ProjectId,
                    mr.Iid,
                    cancellationToken);

                if (updatedMergeRequest is not { RebaseInProgress: true })
                {
                    _logger.LogDebug(
                        "AutoMergeService: rebase completed for branch '{BranchName}' in project {ProjectId}",
                        branch.BranchName,
                        branch.ProjectId);
                    break;
                }

                _logger.LogDebug(
                    "AutoMergeService: rebase still in progress for branch '{BranchName}' in project {ProjectId}, waiting...",
                    branch.BranchName,
                    branch.ProjectId);

                await Task.Delay(_rebaseCheckInterval, cancellationToken);
            }

            if (updatedMergeRequest is { RebaseInProgress: true })
            {
                _logger.LogWarning(
                    "AutoMergeService: rebase timed out after {TimeoutSeconds}s for branch '{BranchName}' in project {ProjectId}, will retry next cycle",
                    _rebaseCheckTimeout.TotalSeconds,
                    branch.BranchName,
                    branch.ProjectId);
                continue;
            }

            if (updatedMergeRequest is not { HasConflicts: true })
            {
                continue;
            }

            _logger.LogWarning(
                "AutoMergeService: rebase conflict detected for branch '{BranchName}' in project {ProjectId}, disabling auto settings for merge group '{MergeGroupName}'",
                branch.BranchName,
                branch.ProjectId,
                group.Name);

            _mergeGroupRepository.UpdateAutoMergeSettings(group.Id, false, false);

            var warning =
                $"Rebase conflict on {branch.ProjectName}/{branch.BranchName} (MR !{mr.Iid}). Auto merge and auto rebase have been disabled.";

            _mergeGroupRepository.UpdateAutoMergeWarning(group.Id, warning);

            var comment = BuildRebaseConflictComment(group.Id, group.Name);
            await _apiService.PostComment(serviceUser, branch.ProjectId, mr.Iid, comment, cancellationToken);

            // Stop processing further branches in this group since auto settings are now disabled
            break;
        }
    }

    private string BuildRebaseConflictComment(int mergeGroupId, string mergeGroupName)
    {
        var groupRef = FormatMergeGroupLink(mergeGroupId, mergeGroupName);
        return
            $"Mergician can no longer rebase this branch due to conflicts. "
            + $"Auto merge and auto rebase have been disabled for merge group {groupRef}.";
    }

    private string FormatMergeGroupLink(int mergeGroupId, string mergeGroupName)
    {
        var baseUrl = _settings.BaseUrl.TrimEnd('/');
        if (baseUrl.IsEmpty())
        {
            return $"\"{mergeGroupName}\"";
        }

        return $"[{mergeGroupName}]({baseUrl}/merge-group/{mergeGroupId})";
    }

    private async Task ProcessAutoMerge(
        AccessDetailsBase serviceUser,
        MergeGroup group,
        List<(BranchWithActivity Branch, GitLabDetailedMergeRequest MergeRequest)> branchMergeRequestDetails,
        CancellationToken cancellationToken)
    {
        // Check if ALL branches in the merge group have MRs
        if (branchMergeRequestDetails.Count != group.Branches.Count)
        {
            var branchesWithoutMergeRequest = group.Branches.Count - branchMergeRequestDetails.Count;
            _logger.LogDebug(
                "AutoMergeService: merge group '{MergeGroupName}' has {Count} branches without MRs, not ready to merge",
                group.Name,
                branchesWithoutMergeRequest);

            return;
        }

        // Pre-compute which branches are only blocked due to intra-group MR dependencies.
        // These branches should not prevent the group from proceeding; instead, prerequisites
        // are merged first and the blocked branches will be merged in subsequent cycles.
        var intraGroupBlockedBranchIds = await GetIntraGroupBlockedBranchIds(
            serviceUser,
            branchMergeRequestDetails,
            cancellationToken);

        // Check each branch for readiness
        var allReady = true;
        var reasons = new List<string>();

        foreach (var (branch, mr) in branchMergeRequestDetails)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isIntraGroupBlockedOnly = intraGroupBlockedBranchIds.Contains(branch.Id);
            var branchReady = await IsBranchReadyToMerge(
                serviceUser,
                branch,
                mr,
                group.AutoRebase,
                isIntraGroupBlockedOnly,
                reasons,
                cancellationToken);

            if (!branchReady)
            {
                allReady = false;
            }
        }

        if (!allReady)
        {
            _logger.LogInformation(
                "AutoMergeService: merge group '{MergeGroupName}' is not ready to merge: {Reasons}",
                group.Name,
                string.Join("; ", reasons));

            return;
        }

        // Check retry window — skip the merge attempt if we are still in a backoff period.
        if (_mergeGroupRetryState.TryGetValue(group.Id, out var retryState)
            && DateTimeOffset.UtcNow < retryState.RetryAfter)
        {
            _logger.LogDebug(
                "AutoMergeService: skipping merge for group '{MergeGroupName}', in retry backoff until {RetryAfter}",
                group.Name,
                retryState.RetryAfter);

            return;
        }

        // Only merge branches whose intra-group prerequisites have not yet been merged.
        // Blocked branches wait for subsequent cycles once their prerequisites land.
        var branchesToMergeNow = intraGroupBlockedBranchIds.Count > 0
            ? branchMergeRequestDetails.Where(x => !intraGroupBlockedBranchIds.Contains(x.Branch.Id)).ToList()
            : branchMergeRequestDetails;

        if (branchesToMergeNow.Count == 0)
        {
            // Circular intra-group dependency — every branch is waiting on another in the group.
            // Fall back to merging everything and let GitLab sort it out.
            _logger.LogWarning(
                "AutoMergeService: merge group '{MergeGroupName}' has a circular intra-group dependency; merging all branches",
                group.Name);

            branchesToMergeNow = branchMergeRequestDetails;
        }
        else if (intraGroupBlockedBranchIds.Count > 0)
        {
            _logger.LogInformation(
                "AutoMergeService: merging {NowCount} of {TotalCount} branches in merge group '{MergeGroupName}' — {WaitingCount} branch(es) are waiting for intra-group prerequisites to merge first",
                branchesToMergeNow.Count,
                branchMergeRequestDetails.Count,
                group.Name,
                intraGroupBlockedBranchIds.Count);
        }
        else
        {
            _logger.LogInformation(
                "AutoMergeService: all {Count} branches in merge group '{MergeGroupName}' are ready, initiating merge",
                branchMergeRequestDetails.Count,
                group.Name);
        }

        // Merge all eligible branches in parallel; catch per-task so a single failure doesn't prevent
        // collecting results from the other tasks.
        var mergeTasks = branchesToMergeNow.Select(async item =>
            {
                var (branch, mr) = item;
                try
                {
                    var mergeResult = await _apiService.Merge(
                        serviceUser,
                        branch.ProjectId,
                        mr.Iid,
                        cancellationToken);

                    return (branch, mr, result: mergeResult);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(
                        ex,
                        "AutoMergeService: merge threw an exception for branch '{BranchName}' in project {ProjectName}",
                        branch.BranchName,
                        branch.ProjectName);

                    return (branch, mr, result: MergeAttemptResult.Failed());
                }
            })
            .ToList();

        var results = await Task.WhenAll(mergeTasks);

        var succeeded = results.Where(r => r.result.Success).ToList();
        var failed = results.Where(r => !r.result.Success).ToList();

        // Persist per-branch merge errors and clear them for succeeded branches.
        foreach (var (branch, _, _) in succeeded)
            _mergeGroupRepository.SetMergeError(branch.Id, null);

        foreach (var (branch, _, result) in failed)
        {
            var errorMsg = result.IsPermissionDenied
                ? "Auto merge failed: insufficient permissions"
                : "Auto merge failed";
            _mergeGroupRepository.SetMergeError(branch.Id, errorMsg);
        }

        // Update retry state based on failure type.
        if (failed.Count > 0)
        {
            var permissionDenied = failed.Any(f => f.result.IsPermissionDenied);
            TimeSpan nextBackoff;

            if (permissionDenied)
            {
                nextBackoff = _mergePermissionRetryInterval;
            }
            else
            {
                var hasPriorState = _mergeGroupRetryState.TryGetValue(group.Id, out var current);
                // Reset exponential backoff when transitioning away from a permission-denied failure.
                var currentBackoff = hasPriorState && !current!.IsPermissionDenied ? current.Backoff : TimeSpan.Zero;

                nextBackoff = currentBackoff == TimeSpan.Zero
                    ? _mergeBackoffInitial
                    : TimeSpan.FromSeconds(
                        Math.Min(currentBackoff.TotalSeconds * 2, _mergeBackoffMax.TotalSeconds));
            }

            _mergeGroupRetryState[group.Id] = new MergeGroupRetryState(DateTimeOffset.UtcNow + nextBackoff, nextBackoff, permissionDenied);

            _logger.LogInformation(
                "AutoMergeService: merge group '{MergeGroupName}' scheduled for retry in {Backoff}s (permissionDenied={PermissionDenied})",
                group.Name,
                nextBackoff.TotalSeconds,
                permissionDenied);
        }

        if (failed.Count > 0 && succeeded.Count > 0)
        {
            // Some merged but some failed — partial merge warning.
            var failedBranches = string.Join(
                ", ",
                failed.Select(f => $"{f.branch.ProjectName}/{f.branch.BranchName}"));

            var warning =
                $"Partial merge: {succeeded.Count} branches merged but {failed.Count} failed ({failedBranches}). "
                + "Some branches may have merged ahead of others.";

            _logger.LogWarning(
                "AutoMergeService: {Warning} in merge group '{MergeGroupName}'",
                warning,
                group.Name);

            _mergeGroupRepository.UpdateAutoMergeWarning(group.Id, warning);
        }
        else if (failed.Count > 0)
        {
            // All merge attempts failed — set warning so the user sees the reason.
            var permissionDenied = failed.Any(f => f.result.IsPermissionDenied);
            var warning = permissionDenied
                ? "Auto merge blocked: service account lacks merge permission. Will retry in 5 minutes."
                : "Auto merge failed unexpectedly. Will retry with backoff.";

            _logger.LogWarning(
                "AutoMergeService: {Warning} for merge group '{MergeGroupName}'",
                warning,
                group.Name);

            _mergeGroupRepository.UpdateAutoMergeWarning(group.Id, warning);
        }
        else
        {
            _logger.LogInformation(
                "AutoMergeService: successfully merged all {Count} branches in merge group '{MergeGroupName}'",
                succeeded.Count,
                group.Name);

            // Clear warning and retry state on full success.
            _mergeGroupRepository.UpdateAutoMergeWarning(group.Id, null);
            _mergeGroupRetryState.TryRemove(group.Id, out _);
        }

        // Remove successfully merged branches immediately so they disappear from the dashboard
        // without waiting for the background sync's dead-branch detection cycle.
        foreach (var (branch, _, _) in succeeded)
        {
            _logger.LogDebug(
                "AutoMergeService: removing merged branch '{BranchName}' in project {ProjectId} from database",
                branch.BranchName,
                branch.ProjectId);

            _deadBranchesService.RemoveBranchAndCleanup(branch.Id);
        }
    }

    private async Task<bool> IsBranchReadyToMerge(
        AccessDetailsBase serviceUser,
        BranchWithActivity branch,
        GitLabDetailedMergeRequest mr,
        bool autoRebaseEnabled,
        bool isIntraGroupBlockedOnly,
        List<string> reasons,
        CancellationToken cancellationToken)
    {
        var branchLabel = $"{branch.ProjectName}/{branch.BranchName}";

        // Check approvals via per-rule aggregation
        var approvalCounts = await _gitLabService.GetMergeRequestApprovalCounts(
            serviceUser,
            branch.ProjectId,
            mr.Iid,
            cancellationToken);

        int? approvalsRequired = null;
        int? approvalsGiven = null;
        if (approvalCounts != null)
        {
            approvalsRequired = approvalCounts.Value.Required;
            approvalsGiven = approvalCounts.Value.Given;
        }

        // Fetch latest pipeline and its jobs
        var latestPipeline = await _apiService.GetLatestMergeRequestPipeline(
            serviceUser,
            branch.ProjectId,
            mr.Iid,
            cancellationToken);

        List<BranchBuildJob> buildJobs = [];
        if (latestPipeline != null)
        {
            buildJobs = await _apiService.GetPipelineJobs(
                serviceUser,
                branch.ProjectId,
                latestPipeline.Id,
                cancellationToken);

            // Guard against pipeline fetch failures: if pipeline is not successful, block regardless
            if (latestPipeline.Status != "success")
            {
                reasons.Add($"{branchLabel}: latest pipeline status is '{latestPipeline.Status}'");
                return false;
            }
        }

        var needsRebase = autoRebaseEnabled
            && (mr.DivergedCommitsCount > 0 || mr.HasConflicts || mr.DetailedMergeStatus == "need_rebase");

        // When this branch's blocked_status is entirely due to intra-group prerequisites,
        // blocked_status is already in the handled set and will be ignored by MRStatusCalculator,
        // so the branch is assessed on its other attributes only.
        // For external blockers, the auto-merge service marks the branch as not ready directly.
        if (!isIntraGroupBlockedOnly && mr.DetailedMergeStatus == "blocked_status")
        {
            _logger.LogDebug(
                "AutoMergeService: branch '{BranchName}' in project {ProjectId} has blocked_status with external blockers; marking as not ready",
                branch.BranchName,
                branch.ProjectId);

            reasons.Add($"{branchLabel}: blocked by an external MR dependency");
            return false;
        }

        var (mrStatus, calcReasons) = MRStatusCalculator.Calculate(
            hasMergeRequest: true,
            isDraft: mr.Draft,
            approvalsRequired,
            approvalsGiven,
            buildJobs,
            needsRebase,
            mr.RebaseInProgress,
            hasConflicts: mr.HasConflicts,
            detailedMergeStatus: mr.DetailedMergeStatus);

        if (mrStatus != MRStatus.Ready)
        {
            foreach (var r in calcReasons)
                reasons.Add($"{branchLabel}: {r}");

            return false;
        }

        // Guard against MR being closed/merged between checks — not surfaced via detailed_merge_status handling.
        if (mr.DetailedMergeStatus == "not_open")
        {
            reasons.Add($"{branchLabel}: merge status is '{mr.DetailedMergeStatus}'");
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Returns the IDs of branches in the group whose <c>blocked_status</c> is caused exclusively
    ///     by MR dependencies on other branches within the same group. These branches do not need to
    ///     block the group from merging; Mergician merges their prerequisites first.
    /// </summary>
    private async Task<HashSet<int>> GetIntraGroupBlockedBranchIds(
        AccessDetailsBase serviceUser,
        List<(BranchWithActivity Branch, GitLabDetailedMergeRequest MergeRequest)> branchMergeRequestDetails,
        CancellationToken cancellationToken)
    {
        var groupMrKeys = branchMergeRequestDetails
            .Select(x => (x.Branch.ProjectId, x.MergeRequest.Iid))
            .ToHashSet();

        var result = new HashSet<int>();

        foreach (var (branch, mr) in branchMergeRequestDetails)
        {
            if (mr.DetailedMergeStatus != "blocked_status")
                continue;

            var blockingMrs = await _gitLabService.GetBlockingMergeRequests(
                serviceUser,
                branch.ProjectId,
                mr.Iid,
                cancellationToken);

            if (blockingMrs == null || blockingMrs.Count == 0)
            {
                _logger.LogDebug(
                    "AutoMergeService: branch '{BranchName}' in project {ProjectId} has blocked_status but no blocking MR details available",
                    branch.BranchName,
                    branch.ProjectId);

                continue;
            }

            var allBlockersInGroup = blockingMrs.All(b => groupMrKeys.Contains((b.ProjectId, b.Iid)));
            if (allBlockersInGroup)
            {
                _logger.LogInformation(
                    "AutoMergeService: branch '{BranchName}' in project {ProjectId} is blocked only by {Count} intra-group MR(s); will defer to a subsequent merge cycle",
                    branch.BranchName,
                    branch.ProjectId,
                    blockingMrs.Count);

                result.Add(branch.Id);
            }
            else
            {
                var externalBlockers = blockingMrs.Where(b => !groupMrKeys.Contains((b.ProjectId, b.Iid))).ToList();
                _logger.LogInformation(
                    "AutoMergeService: branch '{BranchName}' in project {ProjectId} is blocked by {Count} external MR(s): {Titles}",
                    branch.BranchName,
                    branch.ProjectId,
                    externalBlockers.Count,
                    string.Join(", ", externalBlockers.Select(b => b.Title)));
            }
        }

        return result;
    }

    private async Task WaitForReady(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AutoMergeService: waiting for application to be ready");
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = _healthService.GetStatus();
            if (status.IsReady)
            {
                _logger.LogInformation("AutoMergeService: application is ready starting merge checks");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private record MergeGroupRetryState(DateTimeOffset RetryAfter, TimeSpan Backoff, bool IsPermissionDenied = false);
}