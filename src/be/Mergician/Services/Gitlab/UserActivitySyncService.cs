using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using System.Collections.Concurrent;

namespace Mergician.Services.Gitlab;

/// <summary>
///     Manages per-user background sync threads that keep the database up-to-date
///     with each user's GitLab push activity. A sync thread is started the first time
///     a user makes an authenticated request, backfills recent activity from GitLab,
///     then polls every 10 seconds for new activity and checks for deleted branches.
///     The thread stops 5 minutes after the user's last dashboard poll activity.
/// </summary>
public class UserActivitySyncService : IHostedService, IDisposable
{
    private static readonly TimeSpan _inactivityTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan _maxActivityLookback = TimeSpan.FromDays(14);

    private readonly GitlabPipelineService _gitlabPipelineService;

    private readonly GitlabService _gitlabService;

    private readonly ILogger<UserActivitySyncService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    private readonly ConcurrentDictionary<int, UserSyncContext> _userContexts = new();

    private CancellationTokenSource? _globalCts;

    public UserActivitySyncService(
        GitlabService gitlabService,
        GitlabPipelineService gitlabPipelineService,
        IMergeGroupRepository mergeGroupRepository,
        ILogger<UserActivitySyncService> logger)
    {
        _gitlabService = gitlabService;
        _gitlabPipelineService = gitlabPipelineService;
        _mergeGroupRepository = mergeGroupRepository;
        _logger = logger;
    }

    public void Dispose()
    {
        _globalCts?.Dispose();
        foreach (var context in _userContexts.Values)
        {
            context.Cts?.Dispose();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _globalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _logger.LogInformation("UserActivitySyncService started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("UserActivitySyncService stopping, cancelling all user sync threads");

        if (_globalCts != null)
        {
            await _globalCts.CancelAsync();
        }

        var tasks = _userContexts.Values
            .Select(c => c.SyncTask)
            .Where(t => t is { IsCompleted: false })
            .Select(t => t!)
            .ToArray();

        if (tasks.Length > 0)
        {
            _logger.LogInformation("Waiting for {Count} user sync threads to stop", tasks.Length);
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Some user sync threads did not stop within 30 seconds");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Cancellation requested while waiting for sync threads to stop");
            }
        }

        _logger.LogInformation("UserActivitySyncService stopped");
    }

    /// <summary>
    ///     Ensures a background sync thread is running for the given user.
    ///     Updates the stored access token and records poll activity.
    ///     If a thread is already running, this is a no-op (apart from updating the token).
    /// </summary>
    public void EnsureSyncRunning(int gitlabUserId, AccessDetailsBase accessDetails)
    {
        var context = _userContexts.GetOrAdd(gitlabUserId, _ => new UserSyncContext());
        context.UpdateActivity(accessDetails);

        if (context.IsRunning)
        {
            _logger.LogDebug("Sync thread already running for user {UserId}", gitlabUserId);
            return;
        }

        lock (context.StartLock)
        {
            if (context.IsRunning)
            {
                return;
            }

            _logger.LogInformation("Starting background sync thread for user {UserId}", gitlabUserId);

            context.Cts?.Dispose();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _globalCts?.Token ?? CancellationToken.None);

            context.Cts = linkedCts;
            context.SyncTask = Task.Run(() => RunUserSync(gitlabUserId, context, linkedCts.Token));
        }
    }

    /// <summary>
    ///     Fetches push events from GitLab since the given time and stores discovered
    ///     branches in the database. Called by the background sync thread.
    /// </summary>
    private async Task SyncUserActivityFromGitLab(
        AccessDetailsBase accessDetails,
        int gitlabUserId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Syncing GitLab activity for user {UserId} since {Since}",
            gitlabUserId,
            since);

        var pushEvents = _gitlabService.GetPushEventsSince(accessDetails, since, cancellationToken);
        var processedKeys = new HashSet<string>();

        await foreach (var pushEvent in pushEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (GitlabService.IsPossibleDefaultBranch(pushEvent.BranchName))
            {
                _logger.LogDebug(
                    "Skipping default branch '{BranchName}' in project {ProjectId}",
                    pushEvent.BranchName,
                    pushEvent.ProjectId);

                continue;
            }

            var key = $"{pushEvent.BranchName}:{pushEvent.ProjectId}";
            if (!processedKeys.Add(key))
            {
                _logger.LogDebug(
                    "Already processed branch '{BranchName}' in project {ProjectId}, skipping duplicate push event",
                    pushEvent.BranchName,
                    pushEvent.ProjectId);

                continue;
            }

            if (await ShouldSkipBranchByLookup(
                    accessDetails,
                    pushEvent.BranchName,
                    pushEvent.ProjectId,
                    null,
                    "push-event processing",
                    cancellationToken))
            {
                continue;
            }

            var project = await _gitlabService.GetProject(accessDetails, pushEvent.ProjectId);
            if (project == null)
            {
                _logger.LogInformation(
                    "Project {ProjectId} not found while processing push event for branch '{BranchName}'; skipping",
                    pushEvent.ProjectId,
                    pushEvent.BranchName);

                continue;
            }

            if (ShouldSkipScheduledForDeletion(
                    pushEvent.BranchName,
                    pushEvent.ProjectId,
                    project.NameWithNamespace,
                    null,
                    "push-event processing"))
            {
                continue;
            }

            var projectNameWithNamespace = project.NameWithNamespace;
            if (string.IsNullOrWhiteSpace(project.Name))
            {
                _logger.LogError("Invalid empty project name in project id {id}", pushEvent.ProjectId);
                continue;
            }

            var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(
                pushEvent.BranchName,
                pushEvent.ProjectId,
                projectNameWithNamespace,
                project.Name);

            var mergeGroup = _mergeGroupRepository.GetOrCreateMergeGroup(pushEvent.BranchName);
            if (!mergeGroup.Branches.Any(b => b.BranchInProjectId == branchRecord.Id))
            {
                _logger.LogDebug(
                    "Branch {BranchId} not yet in merge group {MergeGroupId}, associating",
                    branchRecord.Id,
                    mergeGroup.Id);

                _mergeGroupRepository.EnsureBranchInMergeGroup(mergeGroup.Id, branchRecord.Id);
            }
            else
            {
                _logger.LogDebug(
                    "Branch {BranchId} already in merge group {MergeGroupId}, skipping association",
                    branchRecord.Id,
                    mergeGroup.Id);
            }

            _mergeGroupRepository.EnsureUserInMergeGroup(gitlabUserId, mergeGroup.Id);

            _logger.LogDebug(
                "Stored branch '{BranchName}' in project {ProjectId} for user {UserId}",
                pushEvent.BranchName,
                pushEvent.ProjectId,
                gitlabUserId);
        }
    }

    /// <summary>
    ///     Checks all tracked branches for a user and removes any that have been deleted from GitLab.
    ///     Called by the background sync thread during each poll cycle.
    /// </summary>
    private async Task CleanupDeletedBranches(
        AccessDetailsBase accessDetails,
        int gitlabUserId,
        CancellationToken cancellationToken)
    {
        var userGroups = _mergeGroupRepository.GetMergeGroupsForUser(gitlabUserId);
        var userBranches = userGroups.SelectMany(g => g.Branches).ToList();
        _logger.LogDebug(
            "Checking {Count} tracked branches for user {UserId} for deletion",
            userBranches.Count,
            gitlabUserId);

        foreach (var branch in userBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lookup = await _gitlabService.GetBranchLookupResult(
                accessDetails,
                branch.ProjectId,
                branch.BranchName);

            if (lookup.IsMissing)
            {
                _logger.LogInformation(
                    "Background sync: branch '{BranchName}' in project {ProjectId} no longer exists, removing",
                    branch.BranchName,
                    branch.ProjectId);

                if (branch.BranchInProjectId.HasValue)
                {
                    RemoveBranchAndCleanup(branch.BranchInProjectId.Value);
                }
            }
            else if (lookup.IsUnavailable)
            {
                _logger.LogDebug(
                    "Background sync: branch lookup unavailable for '{BranchName}' in project {ProjectId}; skipping",
                    branch.BranchName,
                    branch.ProjectId);
            }
        }
    }

    /// <summary>
    ///     Fetches MR, approval, and build job details from GitLab for the given branch
    ///     and persists them in the database. Called by the background sync thread.
    ///     Silently skips if project info is unavailable.
    /// </summary>
    private async Task RefreshBranchDetails(
        AccessDetailsBase accessDetails,
        BranchRecord branch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!branch.BranchInProjectId.HasValue)
        {
            _logger.LogWarning(
                "BranchRecord for '{BranchName}' in project {ProjectId} has no BranchInProjectId; skipping detail refresh",
                branch.BranchName,
                branch.ProjectId);

            return;
        }

        _logger.LogDebug(
            "Refreshing details for branch '{BranchName}' in project {ProjectId}",
            branch.BranchName,
            branch.ProjectId);

        var project = await _gitlabService.GetProject(accessDetails, branch.ProjectId);
        if (project == null)
        {
            _logger.LogDebug(
                "Project {ProjectId} not found when refreshing details for '{BranchName}'; skipping",
                branch.ProjectId,
                branch.BranchName);

            return;
        }

        if (GitlabService.IsScheduledForDeletion(project.NameWithNamespace))
        {
            _logger.LogInformation(
                "Skipping detail refresh for branch '{BranchName}' in project {ProjectId}: project scheduled for deletion",
                branch.BranchName,
                branch.ProjectId);

            return;
        }

        var projectUrl = project.WebUrl;

        var mergeRequests = await _gitlabService.GetMergeRequests(
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

            var approval = await _gitlabService.GetMergeRequestApprovals(
                accessDetails,
                branch.ProjectId,
                first.Iid);

            if (approval != null)
            {
                approvalsGiven = approval.ApprovedBy.Count;
                approvalsRequired = Math.Max(approval.ApprovalsRequired ?? 0, 0);
            }
        }

        var buildJobs = await _gitlabPipelineService.GetLatestExternalJobsForBranch(
            accessDetails,
            branch.ProjectId,
            branch.BranchName,
            cancellationToken);

        // Fetch the branch's latest commit date from GitLab to use as the last updated timestamp
        var branchDetails = await _gitlabService.GetBranchDetails(
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
            branch.BranchInProjectId.Value,
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

    /// <summary>
    ///     Refreshes MR, approval, and build details for all branches tracked by the given user.
    ///     Called by the background sync thread as a second pass after activity sync.
    /// </summary>
    private async Task RefreshAllBranchDetails(
        AccessDetailsBase accessDetails,
        int gitlabUserId,
        CancellationToken cancellationToken)
    {
        var userGroups = _mergeGroupRepository.GetMergeGroupsForUser(gitlabUserId);
        var userBranches = userGroups.SelectMany(g => g.Branches).ToList();

        _logger.LogDebug(
            "Refreshing details for {Count} branches for user {UserId}",
            userBranches.Count,
            gitlabUserId);

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
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to refresh details for branch '{BranchName}' in project {ProjectId}; skipping",
                    branch.BranchName,
                    branch.ProjectId);
            }
        }

        _logger.LogDebug("Finished refreshing details for user {UserId}", gitlabUserId);
    }

    private async Task RunUserSync(int gitlabUserId, UserSyncContext context, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Background sync thread started for user {UserId}", gitlabUserId);
            var lastPollTime = DateTimeOffset.UtcNow;

            // Phase 1: Backfill from the user's last known activity or 14 days
            await BackfillUserActivity(gitlabUserId, context, ct);

            // Phase 2: Continuous polling loop
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_pollInterval, ct);

                var inactiveFor = DateTimeOffset.UtcNow - context.LastPollActivity;
                if (inactiveFor > _inactivityTimeout)
                {
                    _logger.LogInformation(
                        "User {UserId} inactive for {Inactive}, stopping sync thread",
                        gitlabUserId,
                        inactiveFor);

                    break;
                }

                var accessUser = context.AccessUser;
                if (accessUser == null)
                {
                    _logger.LogWarning(
                        "No access token available for user {UserId}, skipping poll cycle",
                        gitlabUserId);

                    continue;
                }

                try
                {
                    var now = DateTimeOffset.UtcNow;
                    // Poll for new push events since the last successful poll
                    await SyncUserActivityFromGitLab(accessUser, gitlabUserId, lastPollTime, ct);

                    lastPollTime = now;

                    // Check for deleted branches and clean up DB records
                    await CleanupDeletedBranches(accessUser, gitlabUserId, ct);

                    // Refresh MR, approval, and build status for all tracked branches
                    await RefreshAllBranchDetails(accessUser, gitlabUserId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Error during sync poll for user {UserId}, will retry next cycle",
                        gitlabUserId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Background sync thread cancelled for user {UserId}",
                gitlabUserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Background sync thread failed unexpectedly for user {UserId}",
                gitlabUserId);
        }
        finally
        {
            _logger.LogInformation(
                "Background sync thread stopped for user {UserId}",
                gitlabUserId);
        }
    }

    private async Task BackfillUserActivity(
        int gitlabUserId,
        UserSyncContext context,
        CancellationToken ct)
    {
        var accessUser = context.AccessUser;
        if (accessUser == null)
        {
            _logger.LogWarning(
                "No access token available for backfill for user {UserId}",
                gitlabUserId);

            return;
        }

        var since = DateTimeOffset.UtcNow.Subtract(_maxActivityLookback);
        _logger.LogInformation(
            "Backfilling activity for user {UserId} since {Since}",
            gitlabUserId,
            since);

        try
        {
            await SyncUserActivityFromGitLab(accessUser, gitlabUserId, since, ct);

            _logger.LogInformation("Backfill completed for user {UserId}", gitlabUserId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Backfill failed for user {UserId}, will continue with polling",
                gitlabUserId);
        }
    }

    private async Task<bool> ShouldSkipBranchByLookup(
        AccessDetailsBase accessDetails,
        string branchName,
        int projectId,
        int? trackedBranchInProjectId,
        string operationName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var branchLookup = await _gitlabService.GetBranchLookupResult(
            accessDetails,
            projectId,
            branchName);

        if (branchLookup.IsMissing)
        {
            _logger.LogInformation(
                "Skipping branch '{BranchName}' in project {ProjectId} during {OperationName}: branch no longer exists",
                branchName,
                projectId,
                operationName);

            if (trackedBranchInProjectId.HasValue)
            {
                _logger.LogInformation(
                    "Removing tracked branch record {BranchRecordId} for '{BranchName}' in project {ProjectId} during {OperationName}",
                    trackedBranchInProjectId.Value,
                    branchName,
                    projectId,
                    operationName);

                RemoveBranchAndCleanup(trackedBranchInProjectId.Value);
            }

            return true;
        }

        if (branchLookup.IsUnavailable)
        {
            _logger.LogWarning(
                "Skipping branch '{BranchName}' in project {ProjectId} during {OperationName}: branch lookup unavailable",
                branchName,
                projectId,
                operationName);

            return true;
        }

        return false;
    }

    private bool ShouldSkipScheduledForDeletion(
        string branchName,
        int projectId,
        string projectNameWithNamespace,
        int? trackedBranchInMergeGroupId,
        string operationName)
    {
        if (!GitlabService.IsScheduledForDeletion(projectNameWithNamespace))
        {
            return false;
        }

        _logger.LogInformation(
            "Skipping branch '{BranchName}' in project {ProjectId} during {OperationName}: project/group is scheduled for deletion ('{ProjectNameWithNamespace}')",
            branchName,
            projectId,
            operationName,
            projectNameWithNamespace);

        if (trackedBranchInMergeGroupId.HasValue)
        {
            _logger.LogInformation(
                "Removing tracked branch record {BranchRecordId} for '{BranchName}' in project {ProjectId} during {OperationName} because project is scheduled for deletion",
                trackedBranchInMergeGroupId.Value,
                branchName,
                projectId,
                operationName);

            RemoveBranchAndCleanup(trackedBranchInMergeGroupId.Value);
        }

        return true;
    }

    private void RemoveBranchAndCleanup(int branchInMergeGroupId)
    {
        _mergeGroupRepository.DeleteBranch(branchInMergeGroupId);

        // Clean up any merge groups that are now empty
        var emptyGroups = _mergeGroupRepository.GetEmptyMergeGroups();
        foreach (var group in emptyGroups)
        {
            _logger.LogInformation(
                "Removing empty merge group {MergeGroupId} '{Name}'",
                group.Id,
                group.Name);

            _mergeGroupRepository.DeleteMergeGroup(group.Id);
        }
    }
}