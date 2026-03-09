using Mergician.Services.Authentication;

namespace Mergician.Services.GitLab;

/// <summary>
///     Thread-safe context tracking a single user's background activity sync state.
///     Stores the user's latest access token (updated on each request) and the timestamp
///     of their last dashboard poll activity (used to determine when to stop the sync thread).
/// </summary>
public class UserSyncContext
{
    public readonly object StartLock = new();

    private AccessDetailsBase? _accessUser;

    private long _lastPollTicks = DateTimeOffset.UtcNow.UtcTicks;

    public CancellationTokenSource? Cts { get; set; }

    public Task? SyncTask { get; set; }

    /// <summary>
    ///     The user's latest access token for GitLab API calls.
    ///     Updated on each incoming request so the background thread always uses a fresh token.
    /// </summary>
    public AccessDetailsBase? AccessUser => Volatile.Read(ref _accessUser);

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

    /// <summary>
    ///     Updates the access token and records a poll activity timestamp.
    /// </summary>
    public void UpdateActivity(AccessDetailsBase accessDetails)
    {
        Volatile.Write(ref _accessUser, accessDetails);
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