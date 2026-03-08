using Mergician.Services.Authentication;
using Mergician.Services.Database;
using System.Collections.Concurrent;

namespace Mergician.Services.GitLab;

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

    private readonly GitLabActivityService _activityService;

    private readonly BranchesService _branchesService;

    private readonly GitLabService _gitLabService;

    private readonly ILogger<UserActivitySyncService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    private readonly StartupStateService _startupStateService;

    private readonly ConcurrentDictionary<int, UserSyncContext> _userContexts = new();

    private CancellationTokenSource? _globalCts;

    public UserActivitySyncService(
        GitLabService gitLabService,
        GitLabActivityService activityService,
        BranchesService branchesService,
        IMergeGroupRepository mergeGroupRepository,
        StartupStateService startupStateService,
        ILogger<UserActivitySyncService> logger)
    {
        _gitLabService = gitLabService;
        _activityService = activityService;
        _branchesService = branchesService;
        _mergeGroupRepository = mergeGroupRepository;
        _startupStateService = startupStateService;
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

    // Stop background sync threads when server is shutting down
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

        _logger.LogInformation("UserActivitySyncService stopped");
    }

    /// <summary>
    ///     Ensures a background sync thread is running for the given user.
    ///     Updates the stored access token and records poll activity.
    ///     If a thread is already running, this is a no-op (apart from updating the token).
    /// </summary>
    public void EnsureSyncRunning(int gitLabUserId, AccessDetailsBase accessDetails)
    {
        var context = _userContexts.GetOrAdd(gitLabUserId, _ => new UserSyncContext());
        context.UpdateActivity(accessDetails);

        if (context.IsRunning)
        {
            _logger.LogDebug("Sync thread already running for user {UserId}", gitLabUserId);
            return;
        }

        lock (context.StartLock)
        {
            if (context.IsRunning)
            {
                return;
            }

            _logger.LogInformation("Starting background sync thread for user {UserId}", gitLabUserId);

            context.Cts?.Dispose();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _globalCts?.Token ?? CancellationToken.None);

            context.Cts = linkedCts;
            context.SyncTask = Task.Run(() => RunUserSync(gitLabUserId, context, linkedCts.Token));
        }
    }

    private async Task RunUserSync(int gitLabUserId, UserSyncContext context, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Background sync thread started for user {UserId}", gitLabUserId);
            var lastPollTime = DateTimeOffset.UtcNow;

            // Phase 1: Backfill from the user's last known activity or 14 days
            if (_startupStateService.IsInGitLabRecoveryMode)
            {
                _logger.LogInformation(
                    "Skipping sync for user {UserId}: GitLab recovery mode is active",
                    gitLabUserId);
                return;
            }

            await BackfillUserActivity(gitLabUserId, context, ct);

            var firstPoll = true;
            // Phase 2: Continuous polling loop
            while (!ct.IsCancellationRequested)
            {
                if (_startupStateService.IsInGitLabRecoveryMode)
                {
                    _logger.LogInformation(
                        "Stopping sync thread for user {UserId}: GitLab recovery mode is active",
                        gitLabUserId);
                    break;
                }

                var accessUser = context.AccessUser;
                if (accessUser == null)
                {
                    _logger.LogError(
                        "No access token available for user {UserId}, skipping poll cycle",
                        gitLabUserId);

                    await Task.Delay(_pollInterval, ct);
                    continue;
                }

                if (firstPoll)
                {
                    firstPoll = false;
                    // Refresh branch details immediately on first poll for responsive UI
                    await _activityService.RefreshAllBranchDetails(accessUser, gitLabUserId, ct);
                }
                else
                {
                    await Task.Delay(_pollInterval, ct);
                }

                var inactiveFor = DateTimeOffset.UtcNow - context.LastPollActivity;
                if (inactiveFor > _inactivityTimeout)
                {
                    _logger.LogInformation(
                        "User {UserId} inactive for {Inactive}, stopping sync thread",
                        gitLabUserId,
                        inactiveFor);

                    break;
                }

                try
                {
                    var now = DateTimeOffset.UtcNow;
                    // Poll for new push events since the last successful poll
                    await FetchNewUserActivityFromGitLab(accessUser, gitLabUserId, lastPollTime, ct);

                    lastPollTime = now;

                    // Check for deleted branches and clean up DB records
                    await CleanupDeletedBranches(accessUser, gitLabUserId, ct);

                    // Refresh MR, approval, and build status for all tracked branches
                    await _activityService.RefreshAllBranchDetails(accessUser, gitLabUserId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (GitLabApiFailureException ex)
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

    /// <summary>
    ///     Checks all tracked branches for a user and removes any that have been deleted from GitLab.
    ///     Called by the background sync thread during each poll cycle.
    /// </summary>
    private async Task CleanupDeletedBranches(
        AccessDetailsBase accessDetails,
        int gitLabUserId,
        CancellationToken cancellationToken)
    {
        var userGroups = _mergeGroupRepository.GetMergeGroupsForUser(gitLabUserId);
        var userBranches = userGroups.SelectMany(g => g.Branches).ToList();
        _logger.LogDebug(
            "Checking {Count} tracked branches for user {UserId} for deletion",
            userBranches.Count,
            gitLabUserId);

        foreach (var branch in userBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lookup = await _gitLabService.GetBranchLookupResult(
                accessDetails,
                branch.ProjectId,
                branch.BranchName);

            if (lookup.Exists)
            {
                continue;
            }

            _logger.LogInformation(
                "Background sync: branch '{BranchName}' in project {ProjectId} no longer exists or is in error state, removing",
                branch.BranchName,
                branch.ProjectId);

            _branchesService.RemoveBranchAndCleanup(branch.Id);
        }
    }

    /// <summary>
    ///     Fetches push events from GitLab since the given time and stores discovered
    ///     branches in the database. Called by the background sync thread.
    /// </summary>
    private async Task FetchNewUserActivityFromGitLab(
        AccessDetailsBase accessDetails,
        int gitLabUserId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Syncing GitLab activity for user {UserId} since {Since}",
            gitLabUserId,
            since);

        var pushEvents = _gitLabService.GetPushEventsForUserSince(accessDetails, since, cancellationToken);
        var processedKeys = new HashSet<string>();

        await foreach (var pushEvent in pushEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (GitLabService.IsPossibleDefaultBranch(pushEvent.BranchName))
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

            if (await _branchesService.ShouldSkipByLookup(
                    accessDetails,
                    pushEvent.BranchName,
                    pushEvent.ProjectId,
                    null,
                    "push-event processing",
                    cancellationToken))
            {
                continue;
            }

            var project = await _gitLabService.GetProject(accessDetails, pushEvent.ProjectId);
            if (project == null)
            {
                _logger.LogInformation(
                    "Project {ProjectId} not found while processing push event for branch '{BranchName}'; skipping",
                    pushEvent.ProjectId,
                    pushEvent.BranchName);

                continue;
            }

            if (_branchesService.ShouldSkipScheduledForDeletion(
                    pushEvent.BranchName,
                    pushEvent.ProjectId,
                    project.NameWithNamespace,
                    null,
                    "push-event processing"))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(project.Name))
            {
                _logger.LogError("Invalid empty project name in project id {id}", pushEvent.ProjectId);
                continue;
            }

            var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(
                pushEvent.BranchName,
                pushEvent.ProjectId,
                project.Name,
                project.NameWithNamespace);

            var mergeGroup = _mergeGroupRepository.GetOrCreateMergeGroup(pushEvent.BranchName);
            var isNewToMergeGroup = !mergeGroup.Branches.Any(b => b.Id == branchRecord.Id);

            if (isNewToMergeGroup)
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

            _mergeGroupRepository.EnsureUserInMergeGroup(gitLabUserId, mergeGroup.Id);

            _logger.LogDebug(
                "Stored branch '{BranchName}' in project {ProjectId} for user {UserId}",
                pushEvent.BranchName,
                pushEvent.ProjectId,
                gitLabUserId);
        }
    }

    private async Task BackfillUserActivity(
        int gitLabUserId,
        UserSyncContext context,
        CancellationToken ct)
    {
        var accessUser = context.AccessUser;
        if (accessUser == null)
        {
            _logger.LogWarning(
                "No access token available for backfill for user {UserId}",
                gitLabUserId);

            return;
        }

        var since = DateTimeOffset.UtcNow.Subtract(_maxActivityLookback);
        _logger.LogInformation(
            "Backfilling activity for user {UserId} since {Since}",
            gitLabUserId,
            since);

        try
        {
            await FetchNewUserActivityFromGitLab(accessUser, gitLabUserId, since, ct);

            _logger.LogInformation("Backfill completed for user {UserId}", gitLabUserId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GitLabApiFailureException ex)
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
}