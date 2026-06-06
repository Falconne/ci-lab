using Mergician.Services.Authentication;

namespace Mergician.Services;

/// <summary>
///     Thread-safe context tracking a single user's background activity sync state.
///     Stores the user's latest access token (updated on each request) and the timestamp
///     of their last dashboard poll activity (used to determine when to stop the sync thread).
/// </summary>
public class UserActivitySyncContext
{
    private readonly ReaderWriterLockSlim _accessLock = new(LockRecursionPolicy.NoRecursion);

    private readonly Lock _startLock = new();

    private AccessDetailsForUser _accessDetailsForUser;

    private long _lastPollTicks = DateTimeOffset.UtcNow.UtcTicks;

    public UserActivitySyncContext(AccessDetailsForUser accessDetailsForUser)
    {
        _accessDetailsForUser = accessDetailsForUser;
    }

    public CancellationTokenSource? Cts { get; set; }

    public Task? SyncTask { get; set; }

    /// <summary>
    ///     The user's latest access token for GitLab API calls.
    ///     Updated on each incoming request so the background thread always uses a fresh token.
    /// </summary>
    public AccessDetailsForUser AccessDetailsForUser
    {
        get
        {
            _accessLock.EnterReadLock();
            try
            {
                return _accessDetailsForUser;
            }
            finally
            {
                _accessLock.ExitReadLock();
            }
        }
    }

    /// <summary>
    ///     Last time the user made a dashboard poll request.
    ///     Used to determine if the user is still active.
    /// </summary>
    public DateTimeOffset LastPollActivity =>
        new(Interlocked.Read(ref _lastPollTicks), TimeSpan.Zero);

    /// <summary>
    ///     True if the background sync task is currently running.
    /// </summary>
    public bool IsRunning => SyncTask is { IsCompleted: false };

    public bool StartSyncIfNotRunning(
        Func<Task> action,
        ILogger logger,
        CancellationToken? globalCancellationToken)
    {
        lock (_startLock)
        {
            if (IsRunning)
            {
                return false;
            }

            Cts?.Dispose();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                globalCancellationToken ?? CancellationToken.None);

            Cts = linkedCts;
            SyncTask = Task.Run(action);
        }

        return true;
    }

    /// <summary>
    ///     Updates the access token and records a poll activity timestamp.
    /// </summary>
    public void UpdateActivity(AccessDetailsForUser accessDetails)
    {
        _accessLock.EnterWriteLock();
        try
        {
            _accessDetailsForUser = accessDetails;
        }
        finally
        {
            _accessLock.ExitWriteLock();
        }

        RecordPollTime();
    }

    /// <summary>
    ///     Records that the user made a poll request just now.
    /// </summary>
    public void RecordPollTime()
    {
        Interlocked.Exchange(ref _lastPollTicks, DateTimeOffset.UtcNow.UtcTicks);
    }
}