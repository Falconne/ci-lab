using Mergician.Entities;

namespace Mergician.Services;

public class StartupStateService
{
    private readonly SemaphoreSlim _gitLabRecoverySignal = new(0, 1);

    private volatile StartupStatus _status = new() { IsReady = false, Message = "Starting up..." };

    private int _gitLabRecoveryPending;

    public void EnterGitLabRecoveryMode()
    {
        SetStatus(
            false,
            "Checking GitLab...",
            "Error contacting GitLab, please contact administrator.");

        if (Interlocked.Exchange(ref _gitLabRecoveryPending, 1) == 0)
        {
            _gitLabRecoverySignal.Release();
        }
    }

    public StartupStatus GetStatus() => _status;

    public void SetStatus(bool isReady, string message, string? error = null)
    {
        _status = new StartupStatus
        {
            IsReady = isReady,
            Message = message,
            Error = error
        };
    }

    public async Task WaitForGitLabRecovery(CancellationToken cancellationToken)
    {
        await _gitLabRecoverySignal.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _gitLabRecoveryPending, 0);
    }
}