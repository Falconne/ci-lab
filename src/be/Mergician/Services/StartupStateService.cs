using Mergician.Entities;

namespace Mergician.Services;

public class StartupStateService
{
    private readonly SemaphoreSlim _gitLabRecoverySignal = new(0, 1);

    private volatile StartupStatus _status = new() { IsReady = false, Message = "Starting up..." };

    private int _gitLabRecoveryPending;

    private volatile bool _isInGitLabRecoveryMode;

    /// <summary>
    ///     True when the application has entered GitLab recovery mode.
    ///     Used by <see cref="Gitlab.GitLabApiClient" /> to abort remaining retries
    ///     so that all threads stop hitting an unreachable GitLab instance.
    /// </summary>
    public bool IsInGitLabRecoveryMode => _isInGitLabRecoveryMode;

    /// <summary>
    ///     Switches the application into GitLab recovery mode and signals the background
    ///     startup service to re-run the GitLab checks. This is the single transition point
    ///     used after runtime GitLab failures, so new requests and new browser tabs all see
    ///     the same recovery status.
    /// </summary>
    public void EnterGitLabRecoveryMode()
    {
        _isInGitLabRecoveryMode = true;

        _status = new StartupStatus
        {
            IsReady = false,
            Message = "Checking GitLab...",
            Error = "Error contacting GitLab, please contact administrator.",
            IsGitLabRecovery = true
        };

        if (Interlocked.Exchange(ref _gitLabRecoveryPending, 1) == 0)
        {
            _gitLabRecoverySignal.Release();
        }
    }

    /// <summary>
    ///     Returns the current startup or recovery status snapshot for middleware,
    ///     controllers, and frontend polling.
    /// </summary>
    public StartupStatus GetStatus() => _status;

    /// <summary>
    ///     Updates the published startup status. Marking the app ready also clears the
    ///     recovery flag so subsequent requests leave the recovery flow completely.
    /// </summary>
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

    /// <summary>
    ///     Waits until some runtime GitLab failure requests a recovery pass. The pending flag
    ///     coalesces repeated failures into a single wake-up so the recovery loop does not spin.
    /// </summary>
    public async Task WaitForGitLabRecovery(CancellationToken cancellationToken)
    {
        await _gitLabRecoverySignal.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _gitLabRecoveryPending, 0);
    }
}