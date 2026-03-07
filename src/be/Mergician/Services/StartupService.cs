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

    private readonly DatabaseMigrationService _databaseMigrationService;

    private readonly ILogger<StartupService> _logger;

    private readonly MergicianSettings _settings;

    private readonly GitLabTimezoneService _timezoneService;

    private volatile StartupStatus _status = new() { IsReady = false, Message = "Starting up..." };

    public StartupService(
        MergicianSettings settings,
        DatabaseMigrationService databaseMigrationService,
        GitLabTimezoneService timezoneService,
        ILogger<StartupService> logger)
    {
        _settings = settings;
        _databaseMigrationService = databaseMigrationService;
        _timezoneService = timezoneService;
        _logger = logger;
    }

    public StartupStatus GetStatus() => _status;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RunStartupChecks(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void SetStatus(bool isReady, string message, string? error = null)
    {
        _status = new StartupStatus { IsReady = isReady, Message = message, Error = error };
    }

    private async Task RunStartupChecks(CancellationToken cancellationToken)
    {
        _logger.LogInformation("StartupService: beginning startup checks");

        // Step 1: Validate service token is configured. This is a permanent error if missing.
        if (string.IsNullOrWhiteSpace(_settings.GitLab.ServiceToken))
        {
            _logger.LogError("Startup check failed: GitLab service token is not configured");
            SetStatus(false, "Configuration error", "Gitlab Service token is not configured");
            return;
        }

        _logger.LogInformation("StartupService: service token is configured");

        // Step 2: Run database migration with retry until it succeeds.
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
                _logger.LogError(ex, "StartupService: database check failed, will retry in {Delay}", _retryDelay);
                SetStatus(false, "Checking database...", "Database is unavailable, please contact administrator.");
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Step 3: Check GitLab health and detect timezone with retry until it succeeds.
        while (!cancellationToken.IsCancellationRequested)
        {
            SetStatus(false, "Checking GitLab...");
            try
            {
                _logger.LogInformation("StartupService: checking GitLab connectivity and detecting timezone");
                await _timezoneService.DetectTimezone();
                _logger.LogInformation("StartupService: GitLab check passed");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "StartupService: GitLab check failed, will retry in {Delay}",
                    _retryDelay);

                SetStatus(
                    false,
                    "Checking GitLab...",
                    "Error contacting GitLab, please contact administrator.");

                await Task.Delay(_retryDelay, cancellationToken);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _logger.LogInformation("StartupService: all startup checks passed, application is ready");
        SetStatus(true, "Ready");
    }
}
