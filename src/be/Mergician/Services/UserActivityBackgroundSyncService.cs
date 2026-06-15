using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.GitLab;
using System.Collections.Concurrent;
using Util;

namespace Mergician.Services;

/// <summary>
///     Manages per-user background sync threads that keep the database up-to-date
///     with each user's GitLab push activity. A sync thread is started the first time
///     a user makes an authenticated request, backfills recent activity from GitLab,
///     then polls regularly for new activity.
///     The thread stops some minutes after the user's last poll activity (i.e. they
///     have closed the Mergician web pages).
/// </summary>
public class UserActivityBackgroundSyncService : IHostedService, IDisposable
{
    private static readonly TimeSpan _inactivityTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan _maxActivityLookback = TimeSpan.FromDays(14);

    private readonly BranchDetailsRefreshService _branchDetailsRefreshService;

    private readonly DeadBranchesService _deadBranchesService;

    private readonly GitLabRecoveryService _gitLabRecoveryService;

    private readonly GitLabService _gitLabService;

    private readonly IIgnoredBranchRepository _ignoredBranchRepository;

    private readonly ILogger<UserActivityBackgroundSyncService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    private readonly ConcurrentDictionary<int, UserActivitySyncContext> _userContexts = new();

    private CancellationTokenSource? _globalCts;

    public UserActivityBackgroundSyncService(
        GitLabService gitLabService,
        BranchDetailsRefreshService branchDetailsRefreshService,
        DeadBranchesService deadBranchesService,
        IMergeGroupRepository mergeGroupRepository,
        IIgnoredBranchRepository ignoredBranchRepository,
        GitLabRecoveryService gitLabRecoveryService,
        ILogger<UserActivityBackgroundSyncService> logger)
    {
        _gitLabService = gitLabService;
        _branchDetailsRefreshService = branchDetailsRefreshService;
        _deadBranchesService = deadBranchesService;
        _mergeGroupRepository = mergeGroupRepository;
        _ignoredBranchRepository = ignoredBranchRepository;
        _gitLabRecoveryService = gitLabRecoveryService;
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
        _logger.LogInformation("UserActivityBackgroundSyncService started");
        return Task.CompletedTask;
    }

    // Stop background sync threads when server is shutting down
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "UserActivityBackgroundSyncService stopping, cancelling all user sync threads");

        if (_globalCts != null)
        {
            await _globalCts.CancelAsync();
        }

        // No lock needed here: StopAsync is called during graceful shutdown, well after all
        // EnsureSyncRunning calls have completed, so there is no contention on SyncTask.
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
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Some user sync threads did not stop within 15 seconds");
            }
            catch (OperationCanceledException)
            {
                // Ignored
            }
        }

        _logger.LogInformation("UserActivityBackgroundSyncService stopped");
    }

    /// <summary>
    ///     Ensures a background sync thread is running for the given user.
    ///     Updates the stored access token and records poll activity.
    ///     If a thread is already running, this is a no-op (apart from updating the token).
    /// </summary>
    public void EnsureSyncRunning(UserAccessDetails userAccessDetails)
    {
        var userId = userAccessDetails.UserId;
        var context = _userContexts.GetOrAdd(userId, _ => new UserActivitySyncContext(userAccessDetails));
        context.UpdateActivity(userAccessDetails);

        _logger.LogDebug("Starting background sync thread for user {UserId} if not running", userId);
        var linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(_globalCts?.Token ?? CancellationToken.None);

        var started = context.StartSyncIfNotRunning(
            () => RunUserActivitySync(userId, context, linkedCts.Token),
            _logger,
            _globalCts?.Token);

        if (started)
        {
            _logger.LogInformation("Started background sync thread for user {UserId}", userId);
        }
        else
        {
            _logger.LogDebug("Background thread already running for {UserId}", userId);
        }
    }

    private async Task RunUserActivitySync(
        int gitLabUserId,
        UserActivitySyncContext context,
        CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Background sync thread started for user {UserId}", gitLabUserId);
            var lastPollTime = DateTimeOffset.UtcNow;

            if (_gitLabRecoveryService.IsInGitLabRecoveryMode)
            {
                _logger.LogInformation(
                    "Sync thread for user {UserId} pausing before backfill: GitLab recovery mode is active",
                    gitLabUserId);

                while (_gitLabRecoveryService.IsInGitLabRecoveryMode && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }

                _logger.LogInformation("Sync thread for user {UserId} resuming after recovery", gitLabUserId);
            }

            // Since we only monitor for activity of users who are actively using the Mergician UI,
            // we must backfill data since the last time the user was active on Mergician before
            // going into regular monitoring mode.

            // Phase 1: Backfill from existing open MRs created by the user
            await BackfillFromExistingMergeRequests(context.UserAccessDetails, ct);

            // Phase 2: Backfill from the user's last known activity or 14 days
            await BackfillUserActivity(gitLabUserId, context, ct);

            var firstPoll = true;
            // Phase 3: Continuous polling loop
            while (!ct.IsCancellationRequested)
            {
                await EnsureNotInRecoveryMode(gitLabUserId, ct);
                var userAccessDetails = context.UserAccessDetails;

                if (firstPoll)
                {
                    firstPoll = false;
                    // Refresh branch details immediately on first poll for responsive UI
                    await RefreshAllBranchDetails(userAccessDetails, ct);
                }
                else
                {
                    await Task.Delay(_pollInterval, ct);
                }

                var inactiveFor = DateTimeOffset.UtcNow - context.LastPollActivity;
                if (inactiveFor > _inactivityTimeout)
                {
                    _logger.LogDebug(
                        "User {UserId} inactive for {Inactive}, stopping sync thread",
                        gitLabUserId,
                        inactiveFor);

                    break;
                }

                try
                {
                    var nextPollTimeFrom = DateTimeOffset.UtcNow;
                    // Poll for new push events since the last successful poll
                    await FetchNewUserActivityFromGitLab(userAccessDetails, lastPollTime, ct);

                    lastPollTime = nextPollTimeFrom;

                    // Refresh MR, approval, and build status for all tracked branches.
                    // Also removes branches that are no longer present in GitLab.
                    await RefreshAllBranchDetails(userAccessDetails, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (GitLabStartupRequiredException ex)
                {
                    _logger.LogError(
                        ex,
                        "GitLab became unavailable during the sync poll for user {UserId}; ending this poll cycle",
                        gitLabUserId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Error during sync poll for user {UserId}, will retry next cycle",
                        gitLabUserId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Background sync thread cancelled for user {UserId}",
                gitLabUserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Background sync thread failed unexpectedly for user {UserId}",
                gitLabUserId);
        }
        finally
        {
            _logger.LogInformation(
                "Background sync thread stopped for user {UserId}",
                gitLabUserId);
        }
    }

    private async Task EnsureNotInRecoveryMode(int userId, CancellationToken cancellationToken)
    {
        if (_gitLabRecoveryService.IsInGitLabRecoveryMode)
        {
            _logger.LogInformation(
                "Sync thread for user {UserId} pausing: GitLab recovery mode is active",
                userId);

            while (_gitLabRecoveryService.IsInGitLabRecoveryMode
                   && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }

            _logger.LogInformation(
                "Sync thread for user {UserId} resuming after recovery",
                userId);
        }
    }

    /// <summary>
    ///     Fetches push events from GitLab since the given time and stores discovered
    ///     branches in the database. Called by the background sync thread.
    /// </summary>
    private async Task FetchNewUserActivityFromGitLab(
        UserAccessDetails userAccessDetails,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var userId = userAccessDetails.UserId;
        _logger.LogDebug(
            "Syncing GitLab activity for user {UserId} since {Since}",
            userId,
            since);

        var ignoredBranches = await _ignoredBranchRepository.GetIgnoredBranchNames(userId);
        var pushEvents =
            _gitLabService.GetPushEventsForUserSince(userAccessDetails, since, cancellationToken);

        var processedBranches = new HashSet<(string BranchName, int ProjectId)>();

        await foreach (var pushEvent in pushEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ignoredBranches.Contains(pushEvent.BranchName))
            {
                _logger.LogDebug(
                    "User {UserId} has marked branch '{Branch}' as ignored — skipping it",
                    userId,
                    pushEvent.BranchName);

                continue;
            }

            if (GitLabService.IsPossibleDefaultBranch(pushEvent.BranchName))
            {
                _logger.LogDebug(
                    "Skipping default branch '{BranchName}' in project {ProjectId}",
                    pushEvent.BranchName,
                    pushEvent.ProjectId);

                continue;
            }

            var key = (pushEvent.BranchName, pushEvent.ProjectId);
            if (!processedBranches.Add(key))
            {
                _logger.LogDebug(
                    "Already processed branch '{BranchName}' in project {ProjectId}, skipping duplicate push event",
                    pushEvent.BranchName,
                    pushEvent.ProjectId);

                continue;
            }

            // Only check if the branch still exists for older push events,
            // to avoid unnecessary GitLab API calls.
            var pushEventAge = DateTimeOffset.UtcNow - pushEvent.CreatedAt;
            if (pushEventAge > TimeSpan.FromMinutes(10)
                && await _deadBranchesService.IsBranchGone(
                    pushEvent.BranchName,
                    pushEvent.ProjectId,
                    cancellationToken))
            {
                _logger.LogInformation(
                    "Skipping branch '{BranchName}' in project {ProjectId} during push-event processing: branch no longer exists",
                    pushEvent.BranchName,
                    pushEvent.ProjectId);

                continue;
            }

            var project = await _gitLabService.GetProject(
                userAccessDetails,
                pushEvent.ProjectId,
                cancellationToken);

            if (project == null)
            {
                _logger.LogInformation(
                    "Project {ProjectId} not found while processing push event for branch '{BranchName}'; skipping",
                    pushEvent.ProjectId,
                    pushEvent.BranchName);

                continue;
            }

            if (DeadBranchesService.IsScheduledForDeletion(project.NameWithNamespace))
            {
                _logger.LogInformation(
                    "Skipping branch '{BranchName}' in project {ProjectId} during push-event processing: project/group is scheduled for deletion ('{ProjectNameWithNamespace}')",
                    pushEvent.BranchName,
                    pushEvent.ProjectId,
                    project.NameWithNamespace
                );

                continue;
            }

            var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(pushEvent.BranchName, project);

            EnsureBranchTracked(
                branchRecord,
                pushEvent.BranchName,
                userId,
                "push event sync");

            _logger.LogDebug(
                "Stored branch '{BranchName}' in project {ProjectId} for user {UserId}",
                pushEvent.BranchName,
                pushEvent.ProjectId,
                userId);
        }
    }

    private async Task BackfillUserActivity(
        int gitLabUserId,
        UserActivitySyncContext context,
        CancellationToken ct)
    {
        var userAccessDetails = context.UserAccessDetails;

        var since = DateTimeOffset.UtcNow.Subtract(_maxActivityLookback);
        _logger.LogInformation(
            "Backfilling activity for user {UserId} since {Since}",
            gitLabUserId,
            since);

        try
        {
            await FetchNewUserActivityFromGitLab(userAccessDetails, since, ct);
            _logger.LogInformation("Backfill completed for user {UserId}", gitLabUserId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GitLabStartupRequiredException ex)
        {
            _logger.LogError(
                ex,
                "GitLab became unavailable during backfill for user {UserId}; continuing with the normal polling loop",
                gitLabUserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Backfill failed for user {UserId}, will continue with polling",
                gitLabUserId);
        }
    }

    /// <summary>
    ///     Fetches all open merge requests created by the user from GitLab and associates
    ///     any untracked branches with this user's merge groups.
    /// </summary>
    private async Task BackfillFromExistingMergeRequests(
        UserAccessDetails userAccessDetails,
        CancellationToken ct)
    {
        var userId = userAccessDetails.UserId;
        _logger.LogInformation("Syncing existing open MRs for user {UserId}", userId);

        try
        {
            var ignoredBranches = await _ignoredBranchRepository.GetIgnoredBranchNames(userId);
            var openMRs = await _gitLabService.GetOpenMergeRequestsForUser(userAccessDetails, userId, ct);

            _logger.LogInformation(
                "Found {Count} open MRs for user {UserId}, checking for untracked branches",
                openMRs.Count,
                userId);

            foreach (var mr in openMRs)
            {
                ct.ThrowIfCancellationRequested();
                if (ignoredBranches.Contains(mr.SourceBranch))
                {
                    _logger.LogDebug(
                        "User {UserId} has marked branch '{Branch}' as ignored — skipping it",
                        userId,
                        mr.SourceBranch);

                    continue;
                }

                var project = await _gitLabService.GetProject(userAccessDetails, mr.ProjectId, ct);

                if (project == null)
                {
                    _logger.LogInformation(
                        "Project {ProjectId} not found while syncing MR for branch '{BranchName}'; skipping",
                        mr.ProjectId,
                        mr.SourceBranch);

                    continue;
                }

                if (GitLabService.IsPossibleDefaultBranch(mr.SourceBranch))
                {
                    _logger.LogDebug(
                        "Skipping default branch '{BranchName}' in project {ProjectId} from MR sync",
                        mr.SourceBranch,
                        mr.ProjectId);

                    continue;
                }

                if (DeadBranchesService.IsScheduledForDeletion(project.NameWithNamespace))
                {
                    _logger.LogInformation(
                        "Skipping branch '{BranchName}' in project {ProjectId} during MR sync: project/group is scheduled for deletion ('{ProjectNameWithNamespace}')",
                        mr.SourceBranch,
                        mr.ProjectId,
                        project.NameWithNamespace);

                    continue;
                }

                var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(mr.SourceBranch, project);
                EnsureBranchTracked(branchRecord, mr.SourceBranch, userId, "open MR sync");
            }

            _logger.LogInformation("MR sync completed for user {UserId}", userId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GitLabStartupRequiredException ex)
        {
            _logger.LogError(
                ex,
                "GitLab became unavailable during MR sync for user {UserId}; continuing with backfill",
                userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "MR sync failed for user {UserId}, will continue with backfill",
                userId);
        }
    }

    /// <summary>
    ///     Refreshes MR, approval, and build job details for all branches tracked by the given user.
    ///     Called by the background sync thread as a second pass after activity sync.
    /// </summary>
    private async Task RefreshAllBranchDetails(
        UserAccessDetails userAccessDetails,
        CancellationToken cancellationToken)
    {
        var userId = userAccessDetails.UserId;
        var userGroups = _mergeGroupRepository.GetMergeGroupsForUser(userId);

        foreach (var group in userGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _branchDetailsRefreshService.RefreshBranches(
                    userAccessDetails,
                    group.Branches,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (GitLabStartupRequiredException ex)
            {
                _logger.LogError(
                    ex,
                    "GitLab became unavailable while refreshing branch details for user {UserId}; ending the current refresh cycle",
                    userId);

                return;
            }
        }

        _logger.LogDebug("Finished refreshing details for user {UserId}", userId);
    }

    /// <summary>
    ///     Ensures a branch record is associated with its merge group and that the user
    ///     is a member of that group.
    /// </summary>
    private void EnsureBranchTracked(
        BranchInProject branchRecord,
        string branchName,
        int userId,
        string reason)
    {
        var mergeGroup = _mergeGroupRepository.GetOrCreateMergeGroup(branchName);
        var isNewToMergeGroup = mergeGroup.Branches.NotAny(b => b.Id == branchRecord.Id);

        if (isNewToMergeGroup)
        {
            _logger.LogInformation(
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

        var wasAdded = _mergeGroupRepository.EnsureUserInMergeGroup(userId, mergeGroup.Id);

        if (wasAdded)
        {
            _logger.LogInformation(
                "User {UserId} added to tracked branches for merge group {MergeGroupId} ('{MergeGroupName}') via {Reason}",
                userId,
                mergeGroup.Id,
                mergeGroup.Name,
                reason);
        }
    }
}