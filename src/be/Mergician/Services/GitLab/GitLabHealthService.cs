using Mergician.Entities;

namespace Mergician.Services.GitLab;

/// <summary>
///     Manages GitLab-specific health checks and recovery signalling.
///     Tracks whether the application is in GitLab recovery mode, signals recovery requests,
///     and runs the retry loop that probes GitLab until it becomes reachable again.
///     Background workers call <see cref="EnterGitLabRecoveryMode" /> when GitLab is unreachable.
///     <see cref="StartupAndRecoveryService" /> waits on <see cref="WaitForGitLabRecoveryRequest" />
///     and then calls <see cref="WaitForGitLabHealthy" /> to re-run the health check.
/// </summary>
/// <remarks>
///     Implementation details:
///     - <c>_gitLabRecoverySignal</c> is a <c>SemaphoreSlim(0,1)</c> used to wake a single waiter.
///     - <c>_gitLabRecoveryPending</c> (managed via <c>Interlocked.Exchange</c>) prevents multiple
///       releases when many threads detect the same outage simultaneously.
///     - <see cref="EnterGitLabRecoveryMode" /> sets <see cref="IsInGitLabRecoveryMode" />, updates
///       the public status via <see cref="HealthService" />, and releases the semaphore once.
///     - <see cref="WaitForGitLabRecoveryRequest" /> blocks until the semaphore is released and then
///       clears the pending flag so subsequent recovery cycles can be signalled.
///     - Calling <see cref="HealthService.SetStatus" /> with <c>isReady = true</c> clears
///       recovery mode so normal operations resume.
/// </remarks>
public class GitLabHealthService
{
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan _recoveryRetryDelay = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gitLabRecoverySignal = new(0, 1);

    private readonly ILogger<GitLabHealthService> _logger;

    private readonly HealthService _healthService;

    private readonly GitLabTimezoneService _timezoneService;

    private int _gitLabRecoveryPending;

    private volatile bool _isInGitLabRecoveryMode;

    public GitLabHealthService(
        HealthService healthService,
        GitLabTimezoneService timezoneService,
        ILogger<GitLabHealthService> logger)
    {
        _healthService = healthService;
        _timezoneService = timezoneService;
        _logger = logger;
    }

    /// <summary>
    ///     True when the application has entered GitLab recovery mode.
    ///     Used by <see cref="GitLabApiClient" /> to abort remaining retries
    ///     so that all threads stop hitting an unreachable GitLab instance.
    /// </summary>
    public bool IsInGitLabRecoveryMode => _isInGitLabRecoveryMode;

    /// <summary>
    ///     Signals that GitLab is unreachable and the application should enter recovery mode.
    ///     Updates the shared startup status and releases the recovery semaphore once,
    ///     even if multiple threads call this concurrently.
    /// </summary>
    public void EnterGitLabRecoveryMode()
    {
        _isInGitLabRecoveryMode = true;

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
    ///     Waits until a GitLab recovery has been requested by a background thread.
    ///     Clears the pending flag after waking so that the next recovery cycle can be signalled.
    /// </summary>
    public async Task WaitForGitLabRecoveryRequest(CancellationToken cancellationToken)
    {
        await _gitLabRecoverySignal.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _gitLabRecoveryPending, 0);
    }

    /// <summary>
    ///     Keeps probing GitLab until it becomes usable again. Recovery runs use a slower poll
    ///     interval than cold start so the app stays informative without hammering GitLab while
    ///     it is down.
    /// </summary>
    public async Task<bool> WaitForGitLabHealthy(
        bool isInRecoveryMode,
        CancellationToken cancellationToken)
    {
        var retryDelay = isInRecoveryMode ? _recoveryRetryDelay : _retryDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            _healthService.SetStatus(false, "Checking GitLab...");

            try
            {
                _logger.LogInformation(
                    "GitLabHealthService: checking GitLab connectivity and detecting timezone{Suffix}",
                    isInRecoveryMode ? " during recovery" : string.Empty);

                await _timezoneService.DetectTimezone(cancellationToken);
                _isInGitLabRecoveryMode = false;
                _logger.LogInformation("GitLabHealthService: GitLab check passed");
                return true;
            }
            catch (GitLabApiFailureException ex)
            {
                _logger.LogError(
                    ex,
                    "GitLabHealthService: GitLab check failed, will retry in {Delay}",
                    retryDelay);

                _healthService.SetStatus(
                    false,
                    "Checking GitLab...",
                    "Error connecting to GitLab, please contact administrator.");

                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (GitLabUnexpectedResponseException ex)
            {
                _logger.LogError(
                    ex,
                    "GitLabHealthService: GitLab returned unexpected status {StatusCode}, will retry in {Delay}",
                    (int)ex.StatusCode,
                    retryDelay);

                _healthService.SetStatus(
                    false,
                    "Checking GitLab...",
                    "Error connecting to GitLab, please contact administrator.");

                await Task.Delay(retryDelay, cancellationToken);
            }
        }

        return false;
    }
}
