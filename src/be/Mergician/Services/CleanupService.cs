using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.Gitlab;

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

    private readonly ILogger<CleanupService> _logger;

    private readonly IServiceProvider _serviceProvider;

    private CancellationToken _stoppingToken;

    private Timer? _timer;

    public CleanupService(IServiceProvider serviceProvider, ILogger<CleanupService> logger)
    {
        _serviceProvider = serviceProvider;
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
        var gitlabService = scope.ServiceProvider.GetRequiredService<GitlabService>();
        var userFactory = scope.ServiceProvider.GetRequiredService<GitlabUserFactory>();

        var serviceUser = userFactory.GetServiceUser();
        var allBranches = mergeGroupRepository.GetAllBranches();
        _logger.LogInformation("CleanupService checking {Count} tracked branches", allBranches.Count);

        var deletedCount = 0;

        foreach (var branch in allBranches)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var branchLookup = await gitlabService.GetBranchLookupResult(
                serviceUser,
                branch.ProjectId,
                branch.BranchName);

            if (branchLookup.IsMissing)
            {
                _logger.LogInformation(
                    "CleanupService: branch '{BranchName}' in project {ProjectId} no longer exists, removing",
                    branch.BranchName,
                    branch.ProjectId);

                mergeGroupRepository.RemoveBranch(branch.Id);
                deletedCount++;
            }
            else if (branchLookup.IsUnavailable)
            {
                _logger.LogWarning(
                    "CleanupService: branch lookup unavailable for '{BranchName}' in project {ProjectId}; skipping deletion this cycle",
                    branch.BranchName,
                    branch.ProjectId);
            }
        }

        // Clean up any empty merge groups
        var emptyGroups = mergeGroupRepository.GetEmptyMergeGroups();
        foreach (var group in emptyGroups)
        {
            _logger.LogInformation(
                "CleanupService: removing empty merge group {MergeGroupId} '{Name}'",
                group.Id,
                group.Name);

            mergeGroupRepository.RemoveMergeGroup(group.Id);
        }

        _logger.LogInformation(
            "CleanupService completed: removed {DeletedBranches} branches, {EmptyGroups} empty merge groups",
            deletedCount,
            emptyGroups.Count);
    }

    // ReSharper disable once InconsistentNaming
    private static TimeSpan CalculateDelayUntilNext3amNZ()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowNz = TimeZoneInfo.ConvertTime(nowUtc, _nzTimeZone);

        // Target 3am today or tomorrow in NZ timezone
        var targetNz = new DateTimeOffset(nowNz.Year, nowNz.Month, nowNz.Day, 3, 0, 0, nowNz.Offset);
        if (nowNz >= targetNz)
        {
            targetNz = targetNz.AddDays(1);
        }

        var targetUtc = targetNz.ToUniversalTime();
        var delay = targetUtc - nowUtc;

        // Safety: ensure we always wait at least 1 minute
        if (delay < TimeSpan.FromMinutes(1))
        {
            delay = TimeSpan.FromMinutes(1);
        }

        return delay;
    }
}
