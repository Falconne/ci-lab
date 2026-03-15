using Mergician.Entities;
using Mergician.Services.Database;
using Mergician.Services.GitLab;
using Util;

namespace Mergician.Services;

/// <summary>
///     Background service that runs startup health checks in sequence before marking the
///     application as ready. Checks are retried until they succeed or a permanent error
///     is detected. Exposes the current startup state for the frontend to poll.
/// </summary>
public class StartupAndRecoveryService : BackgroundService
{
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(5);

    private readonly DatabaseMigrationService _databaseMigrationService;

    private readonly GitLabApiClient _gitLabApiClient;

    private readonly GitLabRecoveryService _gitLabRecoveryService;

    private readonly ILogger<StartupAndRecoveryService> _logger;

    private readonly MergicianSettings _settings;

    private readonly HealthService _startupStateService;

    public StartupAndRecoveryService(
        MergicianSettings settings,
        DatabaseMigrationService databaseMigrationService,
        HealthService startupStateService,
        GitLabApiClient gitLabApiClient,
        GitLabRecoveryService gitLabRecoveryService,
        ILogger<StartupAndRecoveryService> logger)
    {
        _settings = settings;
        _databaseMigrationService = databaseMigrationService;
        _startupStateService = startupStateService;
        _gitLabApiClient = gitLabApiClient;
        _gitLabRecoveryService = gitLabRecoveryService;
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
    public HealthStatus GetStatus()
    {
        return _startupStateService.GetStatus();
    }

    /// <summary>
    ///     Centralizes status updates so this service remains the owner of the user-visible
    ///     startup messages while the shared state service handles publication.
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
            _logger.LogInformation("StartupAndRecoveryService: beginning startup checks");

            if (!await RunInitialStartupChecks(cancellationToken))
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("StartupAndRecoveryService: monitoring for a GitLab recovery request");
                await _gitLabRecoveryService.WaitForGitLabRecoveryRequest(cancellationToken);
                _logger.LogInformation(
                    "StartupAndRecoveryService: GitLab recovery requested, re-running GitLab checks");

                if (await _gitLabApiClient.WaitForGitLabHealthy(true, cancellationToken))
                {
                    SetStatus(true, "Ready");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("StartupAndRecoveryService: stopping due to cancellation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartupAndRecoveryService failed unexpectedly");
            SetStatus(false, "Startup failed", "Unexpected startup error, please contact administrator.");
        }
    }

    /// <summary>
    ///     Performs the cold-start validation path: configuration sanity checks, database
    ///     migration, then the first GitLab health check before the app is marked ready.
    /// </summary>
    private async Task<bool> RunInitialStartupChecks(CancellationToken cancellationToken)
    {
        if (_settings.GitLab.ServiceToken.IsEmpty())
        {
            _logger.LogError("Startup check failed: GitLab service token is not configured");
            SetStatus(false, "Configuration error", "GitLab Service token is not configured");
            return false;
        }

        _logger.LogInformation("StartupAndRecoveryService: service token is configured");

        while (!cancellationToken.IsCancellationRequested)
        {
            SetStatus(false, "Checking database...");
            try
            {
                _logger.LogInformation("StartupAndRecoveryService: running database migration");
                _databaseMigrationService.MigrateDatabase();
                _logger.LogInformation(
                    "StartupAndRecoveryService: database migration completed successfully");

                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "StartupAndRecoveryService: database check failed, will retry in {Delay}",
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

        if (await _gitLabApiClient.WaitForGitLabHealthy(false, cancellationToken))
        {
            SetStatus(true, "Ready");
        }

        return !cancellationToken.IsCancellationRequested;
    }
}