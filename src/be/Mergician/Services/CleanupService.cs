using Mergician.Services.Database;
using Mergician.Services.GitLab;

namespace Mergician.Services;

/// <summary>
///     Background service that periodically checks for deleted branches
///     and removes them from the database. Runs once daily at 3am NZST (New Zealand Standard Time).
///     Uses the GitLab service user token for API access.
/// </summary>
public class CleanupService : IHostedService, IDisposable
{
    // NZST is UTC+12, NZDT is UTC+13
    // TimeZoneInfo handles DST automatically
    private static readonly TimeZoneInfo _nzTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

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
        _logger.LogInformation("CleanupService started. Will run daily at 3am NZST");
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
        var delay = CalculateDelayUntilNext3amNZ();
        _logger.LogInformation("CleanupService sleeping for {Delay} until next 3am NZST run", delay);
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

    // ReSharper disable once InconsistentNaming
    private static TimeSpan CalculateDelayUntilNext3amNZ()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowNz = TimeZoneInfo.ConvertTime(nowUtc, _nzTimeZone);

        // Target 3am today or tomorrow in NZ timezone.
        // Use ConvertTimeToUtc so DST transitions are handled correctly.
        var todayAt3am = new DateTime(nowNz.Year, nowNz.Month, nowNz.Day, 3, 0, 0, DateTimeKind.Unspecified);
        var targetUtc = TimeZoneInfo.ConvertTimeToUtc(todayAt3am, _nzTimeZone);
        if (targetUtc <= nowUtc)
        {
            targetUtc = TimeZoneInfo.ConvertTimeToUtc(todayAt3am.AddDays(1), _nzTimeZone);
        }

        var delay = targetUtc - nowUtc;

        // Safety: ensure we always wait at least 1 minute
        if (delay < TimeSpan.FromMinutes(1))
        {
            delay = TimeSpan.FromMinutes(1);
        }

        return delay;
    }
}