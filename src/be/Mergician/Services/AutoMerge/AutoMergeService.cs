using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.GitLab;
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

    private static readonly TimeSpan _rebaseCheckDelay = TimeSpan.FromSeconds(5);

    private readonly AutoMergeGitLabApiService _apiService;

    private readonly GitLabRecoveryService _gitLabRecoveryService;

    private readonly GitLabService _gitLabService;

    private readonly HealthService _healthService;

    private readonly ILogger<AutoMergeService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    private readonly MergicianSettings _settings;

    private readonly GitLabUserFactory _userFactory;

    public AutoMergeService(
        AutoMergeGitLabApiService apiService,
        GitLabService gitLabService,
        GitLabRecoveryService gitLabRecoveryService,
        GitLabUserFactory userFactory,
        HealthService healthService,
        IMergeGroupRepository mergeGroupRepository,
        MergicianSettings settings,
        ILogger<AutoMergeService> logger)
    {
        _apiService = apiService;
        _gitLabService = gitLabService;
        _gitLabRecoveryService = gitLabRecoveryService;
        _userFactory = userFactory;
        _healthService = healthService;
        _mergeGroupRepository = mergeGroupRepository;
        _settings = settings;
        _logger = logger;
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
            catch (GitLabStartupRequiredException ex)
            {
                _logger.LogWarning(
                    ex,
                    "AutoMergeService: GitLab API failure while processing merge group {MergeGroupId} '{MergeGroupName}'",
                    group.Id,
                    group.Name);
            }
        }
    }

    private async Task ProcessMergeGroup(
        AccessDetailsForUser serviceUser,
        MergeGroup group,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "AutoMergeService: processing merge group {MergeGroupId} '{MergeGroupName}' (autoMerge={AutoMerge}, autoRebase={AutoRebase})",
            group.Id,
            group.Name,
            group.AutoMerge,
            group.AutoRebase);

        // Get detailed MR info for all branches that have MRs
        var branchMrDetails =
            new List<(BranchWithActivity Branch, GitLabDetailedMergeRequest MergeRequest)>();

        foreach (var branch in group.Branches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Find open MRs for this branch
            var mergeRequests = await _gitLabService.GetMergeRequests(
                serviceUser,
                branch.ProjectId,
                branch.BranchName);

            if (mergeRequests.Count == 0)
            {
                _logger.LogDebug(
                    "AutoMergeService: branch '{BranchName}' in project {ProjectId} has no open MR, skipping",
                    branch.BranchName,
                    branch.ProjectId);

                continue;
            }

            var detailedMr = await _apiService.GetDetailedMergeRequest(
                serviceUser,
                branch.ProjectId,
                mergeRequests[0].Iid);

            if (detailedMr != null)
            {
                branchMrDetails.Add((branch, detailedMr));
            }
        }

        // Step 1: Auto Rebase - rebase branches that are behind their target
        if (group.AutoRebase)
        {
            await ProcessAutoRebase(serviceUser, group, branchMrDetails, cancellationToken);
        }

        // Step 2: Auto Merge - check if all branches are ready and merge them all
        if (group.AutoMerge)
        {
            await ProcessAutoMerge(serviceUser, group, branchMrDetails, cancellationToken);
        }
    }

    private async Task ProcessAutoRebase(
        AccessDetailsForUser serviceUser,
        MergeGroup group,
        List<(BranchWithActivity Branch, GitLabDetailedMergeRequest MergeRequest)> branchMrDetails,
        CancellationToken cancellationToken)
    {
        foreach (var (branch, mr) in branchMrDetails)
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
                "AutoMergeService: rebasing branch '{BranchName}' in project {ProjectId} (MR !{MrIid}, divergedCommits={DivergedCommits}, hasConflicts={HasConflicts})",
                branch.BranchName,
                branch.ProjectId,
                mr.Iid,
                mr.DivergedCommitsCount,
                mr.HasConflicts);

            var rebaseInitiated = await _apiService.RebaseMergeRequest(serviceUser, branch.ProjectId, mr.Iid);
            if (!rebaseInitiated)
            {
                _logger.LogWarning(
                    "AutoMergeService: rebase could not be initiated for branch '{BranchName}' in project {ProjectId}",
                    branch.BranchName,
                    branch.ProjectId);

                continue;
            }

            // Wait briefly for GitLab to process the rebase, then check for conflicts
            await Task.Delay(_rebaseCheckDelay, cancellationToken);

            var updatedMr = await _apiService.GetDetailedMergeRequest(serviceUser, branch.ProjectId, mr.Iid);
            if (updatedMr is not { HasConflicts: true })
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
            await _apiService.PostMergeRequestComment(serviceUser, branch.ProjectId, mr.Iid, comment);

            // Stop processing further branches in this group since auto settings are now disabled
            break;
        }
    }

    private string BuildRebaseConflictComment(int mergeGroupId, string mergeGroupName)
    {
        var baseUrl = _settings.BaseUrl.TrimEnd('/');
        if (baseUrl.IsEmpty())
        {
            return
                $"Mergician can no longer rebase this branch due to conflicts. "
                + $"Auto merge and auto rebase have been disabled for merge group \"{mergeGroupName}\".";
        }

        var mergeGroupUrl = $"{baseUrl}/merge-group/{mergeGroupId}";
        return
            $"Mergician can no longer rebase this branch due to conflicts. "
            + $"Auto merge and auto rebase have been disabled for merge group [{mergeGroupName}]({mergeGroupUrl}).";
    }

    private async Task ProcessAutoMerge(
        AccessDetailsForUser serviceUser,
        MergeGroup group,
        List<(BranchWithActivity Branch, GitLabDetailedMergeRequest MergeRequest)> branchMrDetails,
        CancellationToken cancellationToken)
    {
        // Check if ALL branches in the merge group have MRs
        if (branchMrDetails.Count != group.Branches.Count)
        {
            var branchesWithoutMr = group.Branches.Count - branchMrDetails.Count;
            _logger.LogDebug(
                "AutoMergeService: merge group '{MergeGroupName}' has {Count} branches without MRs, not ready to merge",
                group.Name,
                branchesWithoutMr);

            return;
        }

        // Check each branch for readiness
        var allReady = true;
        var reasons = new List<string>();

        foreach (var (branch, mr) in branchMrDetails)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var branchReady = await IsBranchReadyToMerge(
                serviceUser,
                branch,
                mr,
                group.AutoRebase,
                reasons,
                cancellationToken);

            if (!branchReady)
            {
                allReady = false;
            }
        }

        if (!allReady)
        {
            _logger.LogDebug(
                "AutoMergeService: merge group '{MergeGroupName}' is not ready to merge: {Reasons}",
                group.Name,
                string.Join("; ", reasons));

            return;
        }

        _logger.LogInformation(
            "AutoMergeService: all {Count} branches in merge group '{MergeGroupName}' are ready, initiating merge",
            branchMrDetails.Count,
            group.Name);

        // Merge all branches in parallel
        var mergeTasks = branchMrDetails.Select(async item =>
            {
                var (branch, mr) = item;
                var result = await _apiService.AcceptMergeRequest(
                    serviceUser,
                    branch.ProjectId,
                    mr.Iid);

                return (branch, mr, result);
            })
            .ToList();

        var results = await Task.WhenAll(mergeTasks);

        var succeeded = results.Where(r => r.result != null).ToList();
        var failed = results.Where(r => r.result == null).ToList();

        if (failed.Count > 0 && succeeded.Count > 0)
        {
            // Some merged but some failed - this is the partial merge warning case
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
            _logger.LogWarning(
                "AutoMergeService: all {Count} merge attempts failed for merge group '{MergeGroupName}', will retry next cycle",
                failed.Count,
                group.Name);
        }
        else
        {
            _logger.LogInformation(
                "AutoMergeService: successfully merged all {Count} branches in merge group '{MergeGroupName}'",
                succeeded.Count,
                group.Name);

            // Clear any previous warning on full success
            _mergeGroupRepository.UpdateAutoMergeWarning(group.Id, null);
        }
    }

    private async Task<bool> IsBranchReadyToMerge(
        AccessDetailsForUser serviceUser,
        BranchWithActivity branch,
        GitLabDetailedMergeRequest mr,
        bool autoRebaseEnabled,
        List<string> reasons,
        CancellationToken cancellationToken)
    {
        var ready = true;
        var branchLabel = $"{branch.ProjectName}/{branch.BranchName}";

        // Check approvals
        var approvals = await _gitLabService.GetMergeRequestApprovals(
            serviceUser,
            branch.ProjectId,
            mr.Iid);

        if (approvals != null && approvals.ApprovalsRequired.GetValueOrDefault() > 0)
        {
            if (approvals.ApprovedBy.Count < approvals.ApprovalsRequired.GetValueOrDefault())
            {
                reasons.Add(
                    $"{branchLabel}: needs {approvals.ApprovalsRequired - approvals.ApprovedBy.Count} more approvals");

                ready = false;
            }
        }

        // Check pipelines
        var latestPipeline = await _apiService.GetLatestMergeRequestPipeline(
            serviceUser,
            branch.ProjectId,
            mr.Iid);

        if (latestPipeline != null)
        {
            if (latestPipeline.Status != "success")
            {
                reasons.Add($"{branchLabel}: latest pipeline status is '{latestPipeline.Status}'");
                ready = false;
            }
        }

        // Check if branch is up to date with target (when auto rebase is enabled)
        if (autoRebaseEnabled)
        {
            var needsRebase = mr.DivergedCommitsCount > 0
                              || mr.HasConflicts
                              || mr.DetailedMergeStatus == "need_rebase";

            if (needsRebase)
            {
                reasons.Add(
                    $"{branchLabel}: needs rebase (diverged={mr.DivergedCommitsCount}, conflicts={mr.HasConflicts})");

                ready = false;
            }
        }

        // Check merge status from GitLab
        if (mr.DetailedMergeStatus is "not_open" or "blocked_status" or "ci_must_pass"
            or "ci_still_running" or "discussions_not_resolved" or "draft_status" or "conflict")
        {
            reasons.Add($"{branchLabel}: merge status is '{mr.DetailedMergeStatus}'");
            ready = false;
        }

        return ready;
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
}