using Mergician.Entities;
using Mergician.Services.Database;
using Mergician.Services.Gitlab;

namespace Mergician.Services;

/// <summary>
///     Background service that runs startup health checks in sequence before marking the
///     application as ready. Checks are retried until they succeed or a permanent error
///     is detected. Exposes the current startup state for the frontend to poll.
/// </summary>
public class StartupService : IHostedService
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RunStartupChecks(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public StartupStatus GetStatus()
    {
        return _startupStateService.GetStatus();
    }

    private void SetStatus(bool isReady, string message, string? error = null)
    {
        _startupStateService.SetStatus(isReady, message, error);
    }

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
                _logger.LogInformation("StartupService: waiting for a GitLab recovery request");
                await _startupStateService.WaitForGitLabRecovery(cancellationToken);
                _logger.LogInformation("StartupService: GitLab recovery requested, re-running GitLab checks");
                await RunGitLabChecksUntilReady(true, cancellationToken);
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

    private async Task<bool> RunInitialStartupChecks(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.GitLab.ServiceToken))
        {
            _logger.LogError("Startup check failed: GitLab service token is not configured");
            SetStatus(false, "Configuration error", "Gitlab Service token is not configured");
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

        if (await RunGitLabChecksUntilReady(false, cancellationToken))
        {
            SetStatus(true, "Ready");
        }

        return !cancellationToken.IsCancellationRequested;
    }

    private async Task<bool> RunGitLabChecksUntilReady(
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