using Mergician.Entities;

namespace Mergician.Services;

/// <summary>
///     Coordinates and publishes the application's startup and GitLab recovery state.
///     Background workers call into this service to signal when GitLab is unreachable.
///     The service coalesces multiple signals, exposes a snapshot `StartupStatus`, and
///     provides a waitable notification for the `StartupService` to begin recovery work.
/// </summary>
/// <remarks>
///     Implementation details:
///     - `_gitLabRecoverySignal` is a `SemaphoreSlim(0,1)` used to wake a single waiter.
///     - `_gitLabRecoveryPending` (managed via `Interlocked.Exchange`) prevents multiple
///       releases when many threads detect the same outage.
///     - `EnterGitLabRecoveryMode()` sets `IsInGitLabRecoveryMode`, updates the public
///       `StartupStatus` visible to the UI, and releases the semaphore once.
///     - `WaitForGitLabRecovery()` blocks until the semaphore is released and then
///       clears the pending flag so subsequent recovery cycles can be signalled.
///     - `SetStatus(true, ...)` clears recovery mode so normal operations resume.
/// </remarks>
public class StartupStateService
{
    private readonly SemaphoreSlim _gitLabRecoveryRequiredSignal = new(0, 1);

    private int _gitLabRecoveryPending;

    private volatile bool _isInGitLabRecoveryMode;

    private volatile StartupStatus _status = new() { IsReady = false, Message = "Starting up..." };

    /// <summary>
    ///     True when the application has entered GitLab recovery mode.
    ///     Used by <see cref="Gitlab.GitLabApiClient" /> to abort remaining retries
    ///     so that all threads stop hitting an unreachable GitLab instance.
    /// </summary>
    public bool IsInGitLabRecoveryMode => _isInGitLabRecoveryMode;

    public void EnterGitLabRecoveryMode()
    {
        _isInGitLabRecoveryMode = true;

        _status = new StartupStatus
        {
            IsReady = false,
            Message = "Checking GitLab...",
            Error = "Error connecting to GitLab, please contact administrator.",
            IsGitLabRecovery = true
        };

        if (Interlocked.Exchange(ref _gitLabRecoveryPending, 1) == 0)
        {
            _gitLabRecoveryRequiredSignal.Release();
        }
    }

    public StartupStatus GetStatus()
    {
        return _status;
    }

    public void SetStatus(bool isReady, string message, string? error = null)
    {
        if (isReady)
        {
            _isInGitLabRecoveryMode = false;
        }

        _status = new StartupStatus
        {
            IsReady = isReady,
            Message = message,
            Error = error,
            IsGitLabRecovery = _isInGitLabRecoveryMode
        };
    }

    public async Task WaitForGitLabRecoveryRequest(CancellationToken cancellationToken)
    {
        await _gitLabRecoveryRequiredSignal.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _gitLabRecoveryPending, 0);
    }
}