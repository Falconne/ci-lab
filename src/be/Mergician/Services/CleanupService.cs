using Mergician.Services.Database;
using Mergician.Services.GitLab;

namespace Mergician.Services;

/// <summary>
///     Background service that periodically checks for deleted branches
///     and removes them from the database. Runs once daily at 15:00 UTC
///     (equivalent to 3am NZST / 4am NZDT).
///     Uses the GitLab service user token for API access.
/// </summary>
public class CleanupService : IHostedService, IDisposable
{
    private readonly DeadBranchesService _deadBranchesService;

    private readonly ILogger<CleanupService> _logger;

    private readonly IServiceProvider _serviceProvider;

    private CancellationToken _stoppingToken;

    private Timer? _timer;

    public CleanupService(
        IServiceProvider serviceProvider,
        DeadBranchesService deadBranchesService,
        ILogger<CleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _deadBranchesService = deadBranchesService;
        _logger = logger;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingToken = cancellationToken;
        _logger.LogInformation("CleanupService started. Will run daily at 15:00 UTC");
        ScheduleNextRun();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CleanupService stopping");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private void ScheduleNextRun()
    {
        var delay = CalculateDelayUntilNext15UtcHour();
        _logger.LogInformation("CleanupService sleeping for {Delay} until next 15:00 UTC run", delay);
        _timer?.Dispose();
        _timer = new Timer(
            async _ => await RunAndReschedule(),
            null,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    private async Task RunAndReschedule()
    {
        if (_stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("CleanupService stopping due to cancellation");
            return;
        }

        try
        {
            await RunCleanup(_stoppingToken);
        }
        catch (GitLabStartupRequiredException ex)
        {
            _logger.LogError(ex, "CleanupService skipping this cleanup cycle because GitLab is unavailable");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CleanupService encountered an error during cleanup");
        }

        if (!_stoppingToken.IsCancellationRequested)
        {
            ScheduleNextRun();
        }
    }

    private async Task RunCleanup(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CleanupService starting cleanup run");

        using var scope = _serviceProvider.CreateScope();
        var mergeGroupRepository = scope.ServiceProvider.GetRequiredService<IMergeGroupRepository>();

        var allBranches = mergeGroupRepository.GetAllBranches();
        _logger.LogInformation("CleanupService checking {Count} tracked branches", allBranches.Count);

        var removedCount = 0;

        foreach (var branch in allBranches)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var removed = await _deadBranchesService.RemoveBranchIfGone(
                branch.BranchName,
                branch.ProjectId,
                branch.Id,
                stoppingToken);

            if (removed)
            {
                removedCount++;
            }
        }

        _logger.LogInformation("CleanupService completed: removed {RemovedBranches} branches", removedCount);
    }

    private static TimeSpan CalculateDelayUntilNext15UtcHour()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var targetToday = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, 15, 0, 0, TimeSpan.Zero);
        var target = targetToday <= nowUtc ? targetToday.AddDays(1) : targetToday;

        var delay = target - nowUtc;

        // Safety: ensure we always wait at least 1 minute
        if (delay < TimeSpan.FromMinutes(1))
        {
            delay = TimeSpan.FromMinutes(1);
        }

        return delay;
    }
}