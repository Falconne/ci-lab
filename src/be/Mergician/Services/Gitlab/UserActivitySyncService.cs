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

    private readonly GitlabActivityService _activityService;

    private readonly BranchesService _branchesService;

    private readonly GitlabService _gitlabService;

    private readonly ILogger<UserActivitySyncService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    private readonly ConcurrentDictionary<int, UserSyncContext> _userContexts = new();

    private CancellationTokenSource? _globalCts;

    public UserActivitySyncService(
        GitlabService gitlabService,
        GitlabActivityService activityService,
        BranchesService branchesService,
        IMergeGroupRepository mergeGroupRepository,
        ILogger<UserActivitySyncService> logger)
    {
        _gitlabService = gitlabService;
        _activityService = activityService;
        _branchesService = branchesService;
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

    private async Task RunUserSync(int gitlabUserId, UserSyncContext context, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Background sync thread started for user {UserId}", gitlabUserId);
            var lastPollTime = DateTimeOffset.UtcNow;

            // Phase 1: Backfill from the user's last known activity or 14 days
            await BackfillUserActivity(gitlabUserId, context, ct);

            var firstRefresh = true;
            // Phase 2: Continuous polling loop
            while (!ct.IsCancellationRequested)
            {
                var accessUser = context.AccessUser;
                if (accessUser == null)
                {
                    _logger.LogError(
                        "No access token available for user {UserId}, skipping poll cycle",
                        gitlabUserId);

                    await Task.Delay(_pollInterval, ct);
                    continue;
                }

                if (!firstRefresh)
                {
                    firstRefresh = false;
                    await Task.Delay(_pollInterval, ct);
                }
                else
                {
                    await _activityService.RefreshAllBranchDetails(accessUser, gitlabUserId, ct);
                }

                var inactiveFor = DateTimeOffset.UtcNow - context.LastPollActivity;
                if (inactiveFor > _inactivityTimeout)
                {
                    _logger.LogInformation(
                        "User {UserId} inactive for {Inactive}, stopping sync thread",
                        gitlabUserId,
                        inactiveFor);

                    break;
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
                    await _activityService.RefreshAllBranchDetails(accessUser, gitlabUserId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
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

            var project = await _gitlabService.GetProject(accessDetails, pushEvent.ProjectId);
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
            if (!mergeGroup.Branches.Any(b => b.Id == branchRecord.Id))
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
            _logger.LogError(
                ex,
                "Backfill failed for user {UserId}, will continue with polling",
                gitlabUserId);
        }
    }
}