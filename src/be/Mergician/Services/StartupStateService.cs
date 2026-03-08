using Mergician.Entities;

namespace Mergician.Services;

public class StartupStateService
{
    private readonly SemaphoreSlim _gitLabRecoverySignal = new(0, 1);

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
            _gitLabRecoverySignal.Release();
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

    public async Task WaitForGitLabRecovery(CancellationToken cancellationToken)
    {
        await _gitLabRecoverySignal.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _gitLabRecoveryPending, 0);
    }
}