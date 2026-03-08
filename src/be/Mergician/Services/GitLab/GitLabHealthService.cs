using Mergician.Entities;

namespace Mergician.Services.GitLab;

/// <summary>
///     Runs the GitLab health check loop, probing GitLab until it becomes reachable.
///     Recovery state (mode flag, semaphore) is owned by <see cref="GitLabRecoveryService" />,
///     which has no dependency on <see cref="GitLabTimezoneService" /> or
///     <see cref="GitLabApiClient" />, breaking the circular DI chain.
///     <see cref="StartupAndRecoveryService" /> calls <see cref="WaitForGitLabHealthy" /> to
///     run health checks during cold start and after a recovery request.
/// </summary>
public class GitLabHealthService
{
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan _recoveryRetryDelay = TimeSpan.FromSeconds(15);

    private readonly HealthService _healthService;

    private readonly ILogger<GitLabHealthService> _logger;

    private readonly GitLabRecoveryService _recoveryService;

    private readonly GitLabTimezoneService _timezoneService;

    public GitLabHealthService(
        HealthService healthService,
        GitLabRecoveryService recoveryService,
        GitLabTimezoneService timezoneService,
        ILogger<GitLabHealthService> logger)
    {
        _healthService = healthService;
        _recoveryService = recoveryService;
        _timezoneService = timezoneService;
        _logger = logger;
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
                _recoveryService.ClearGitLabRecoveryMode();
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
