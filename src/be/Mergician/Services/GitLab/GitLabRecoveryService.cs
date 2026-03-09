using Mergician.Entities;

namespace Mergician.Services.GitLab;

/// <summary>
///     Owns the GitLab recovery state machine: tracks whether the application is in
///     GitLab recovery mode, signals recovery requests, and clears the flag when GitLab
///     becomes healthy again.
///     Intentionally separate from <see cref="GitLabApiClient" /> to avoid a
///     circular DI dependency: <c>GitLabApiClient</c> depends on this class.
/// </summary>
public class GitLabRecoveryService
{
    private readonly SemaphoreSlim _gitLabRecoverySignal = new(0, 1);

    private readonly HealthService _healthService;

    private int _gitLabRecoveryPending;

    private int _isInGitLabRecoveryMode;

    public GitLabRecoveryService(HealthService healthService)
    {
        _healthService = healthService;
    }

    /// <summary>
    ///     True when the application has entered GitLab recovery mode.
    ///     Used by <see cref="GitLabApiClient" /> to abort remaining retries
    ///     so that all threads stop hitting an unreachable GitLab instance.
    /// </summary>
    public bool IsInGitLabRecoveryMode => Volatile.Read(ref _isInGitLabRecoveryMode) != 0;

    /// <summary>
    ///     Signals that GitLab is unreachable and the application should enter recovery mode.
    ///     Updates the shared startup status and releases the recovery semaphore once,
    ///     even if multiple threads call this concurrently.
    /// </summary>
    public void EnterGitLabRecoveryMode()
    {
        Interlocked.Exchange(ref _isInGitLabRecoveryMode, 1);

        _healthService.SetStatus(
            false,
            "Checking GitLab...",
            "Error connecting to GitLab, please contact administrator.",
            isGitLabRecovery: true);

        if (Interlocked.Exchange(ref _gitLabRecoveryPending, 1) == 0)
        {
            _gitLabRecoverySignal.Release();
        }
    }

    /// <summary>
    ///     Clears recovery mode after GitLab becomes healthy again.
    ///     Called by <see cref="GitLabApiClient" /> once a health check passes.
    /// </summary>
    public void ClearGitLabRecoveryMode()
    {
        Interlocked.Exchange(ref _isInGitLabRecoveryMode, 0);
    }

    /// <summary>
    ///     Waits until a GitLab recovery has been requested by a background thread.
    ///     Clears the pending flag after waking so that the next recovery cycle can be signalled.
    /// </summary>
    public async Task WaitForGitLabRecoveryRequest(CancellationToken cancellationToken)
    {
        await _gitLabRecoverySignal.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _gitLabRecoveryPending, 0);
    }
}
