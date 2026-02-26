using Mergician.Services.Authentication;
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

    private readonly GitlabActivityService _activityService;

    private readonly ILogger<UserActivitySyncService> _logger;

    private readonly ConcurrentDictionary<int, UserSyncContext> _userContexts = new();

    private CancellationTokenSource? _globalCts;

    public UserActivitySyncService(
        GitlabActivityService activityService,
        ILogger<UserActivitySyncService> logger)
    {
        _activityService = activityService;
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
    public void EnsureSyncRunning(int gitlabUserId, GitlabAccessUser accessUser)
    {
        var context = _userContexts.GetOrAdd(gitlabUserId, _ => new UserSyncContext());
        context.UpdateActivity(accessUser);

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

            // Phase 1: Backfill from the user's last known activity or 14 days
            await BackfillUserActivity(gitlabUserId, context, ct);

            // Phase 2: Continuous polling loop
            var lastPollTime = DateTimeOffset.UtcNow;

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
                    // Poll for new push events since the last successful poll
                    await _activityService.SyncUserActivityFromGitLab(
                        accessUser,
                        gitlabUserId,
                        lastPollTime,
                        ct);

                    lastPollTime = DateTimeOffset.UtcNow;

                    // Check for deleted branches and clean up DB records
                    await _activityService.CleanupDeletedBranches(
                        accessUser,
                        gitlabUserId,
                        ct);
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

        var since = _activityService.GetBackfillSince(gitlabUserId);
        _logger.LogInformation(
            "Backfilling activity for user {UserId} since {Since}",
            gitlabUserId,
            since);

        try
        {
            await _activityService.SyncUserActivityFromGitLab(
                accessUser,
                gitlabUserId,
                since,
                ct);

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
}