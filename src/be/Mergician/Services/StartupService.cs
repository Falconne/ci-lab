using Mergician.Entities;
using Mergician.Services.Database;
using Mergician.Services.GitLab;

namespace Mergician.Services;

/// <summary>
///     Background service that runs startup health checks in sequence before marking the
///     application as ready. Checks are retried until they succeed or a permanent error
///     is detected. Exposes the current startup state for the frontend to poll.
/// </summary>
public class StartupService : BackgroundService
{
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan _gitLabRecoveryDelay = TimeSpan.FromSeconds(15);

    private readonly DatabaseMigrationService _databaseMigrationService;

    private readonly ILogger<StartupService> _logger;

    private readonly MergicianSettings _settings;

    private readonly StartupStateService _startupStateService;

    private readonly GitLabTimezoneService _timezoneService;

    public StartupService(
        MergicianSettings settings,
        DatabaseMigrationService databaseMigrationService,
        StartupStateService startupStateService,
        GitLabTimezoneService timezoneService,
        ILogger<StartupService> logger)
    {
        _settings = settings;
        _databaseMigrationService = databaseMigrationService;
        _startupStateService = startupStateService;
        _timezoneService = timezoneService;
        _logger = logger;
    }

    /// <summary>
    ///     Runs the long-running startup workflow. <see cref="BackgroundService" /> calls this
    ///     asynchronously so <c>StartAsync</c> returns immediately without blocking host startup.
    ///     The service owns both the cold-start checks and any later GitLab recovery cycles.
    ///     An unhandled exception here propagates to the framework, which stops the host rather
    ///     than allowing the app to run silently in a broken state.
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return RunStartupChecks(stoppingToken);
    }

    /// <summary>
    ///     Exposes the current startup or recovery status to the controller layer.
    /// </summary>
    public StartupStatus GetStatus()
    {
        return _startupStateService.GetStatus();
    }

    /// <summary>
    ///     Centralizes status updates so this service remains the owner of the user-visible
    ///     startup messages while the shared state service handles publication and signalling.
    /// </summary>
    private void SetStatus(bool isReady, string message, string? error = null)
    {
        _startupStateService.SetStatus(isReady, message, error);
    }

    /// <summary>
    ///     Runs the initial startup sequence once, then waits for runtime GitLab failures to
    ///     request another GitLab-only recovery pass. Database setup is not repeated during
    ///     recovery because the runtime failures we care about are isolated to GitLab.
    /// </summary>
    private async Task RunStartupChecks(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("StartupService: beginning startup checks");

            if (!await RunInitialStartupChecks(cancellationToken))
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("StartupService: monitoring for a GitLab recovery request");
                await _startupStateService.WaitForGitLabRecoveryRequest(cancellationToken);
                _logger.LogInformation("StartupService: GitLab recovery requested, re-running GitLab checks");
                if (await WaitForGitlabHealthy(true, cancellationToken))
                {
                    SetStatus(true, "Ready");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("StartupService: stopping due to cancellation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartupService failed unexpectedly");
            SetStatus(false, "Startup failed", "Unexpected startup error, please contact administrator.");
        }
    }

    /// <summary>
    ///     Performs the cold-start validation path: configuration sanity checks, database
    ///     migration, then the first GitLab health check before the app is marked ready.
    /// </summary>
    private async Task<bool> RunInitialStartupChecks(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.GitLab.ServiceToken))
        {
            _logger.LogError("Startup check failed: GitLab service token is not configured");
            SetStatus(false, "Configuration error", "GitLab Service token is not configured");
            return false;
        }

        _logger.LogInformation("StartupService: service token is configured");

        while (!cancellationToken.IsCancellationRequested)
        {
            SetStatus(false, "Checking database...");
            try
            {
                _logger.LogInformation("StartupService: running database migration");
                _databaseMigrationService.MigrateDatabase();
                _logger.LogInformation("StartupService: database migration completed successfully");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "StartupService: database check failed, will retry in {Delay}",
                    _retryDelay);

                SetStatus(
                    false,
                    "Checking database...",
                    "Database is unavailable, please contact administrator.");

                await Task.Delay(_retryDelay, cancellationToken);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (await WaitForGitlabHealthy(false, cancellationToken))
        {
            SetStatus(true, "Ready");
        }

        return !cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    ///     Keeps probing GitLab until it becomes usable again. Recovery runs use a slower poll
    ///     interval than cold start so the app stays informative without hammering GitLab while
    ///     it is down.
    /// </summary>
    private async Task<bool> WaitForGitlabHealthy(
        bool isInRecoveryMode,
        CancellationToken cancellationToken)
    {
        var retryDelay = isInRecoveryMode ? _gitLabRecoveryDelay : _retryDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            SetStatus(false, "Checking GitLab...");

            try
            {
                _logger.LogInformation(
                    "StartupService: checking GitLab connectivity and detecting timezone{Suffix}",
                    isInRecoveryMode ? " during recovery" : string.Empty);

                await _timezoneService.DetectTimezone(cancellationToken);
                _logger.LogInformation("StartupService: GitLab check passed");
                return true;
            }
            catch (GitLabApiFailureException ex)
            {
                _logger.LogError(
                    ex,
                    "StartupService: GitLab check failed, will retry in {Delay}",
                    retryDelay);

                SetStatus(
                    false,
                    "Checking GitLab...",
                    "Error connecting to GitLab, please contact administrator.");

                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (GitLabUnexpectedResponseException ex)
            {
                _logger.LogError(
                    ex,
                    "StartupService: GitLab returned unexpected status {StatusCode}, will retry in {Delay}",
                    (int)ex.StatusCode,
                    retryDelay);

                SetStatus(
                    false,
                    "Checking GitLab...",
                    "Error connecting to GitLab, please contact administrator.");

                await Task.Delay(retryDelay, cancellationToken);
            }
        }

        return false;
    }
}