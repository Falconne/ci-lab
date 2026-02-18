using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.Gitlab;

namespace Mergician.Services;

/// <summary>
/// Background service that periodically checks for deleted branches
/// and removes them from the database. Runs once daily at 3am NZST (New Zealand Standard Time).
/// Uses the GitLab service user token for API access.
/// </summary>
public class CleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CleanupService> _logger;

    // New Zealand Standard Time is UTC+12, NZDT is UTC+13
    // TimeZoneInfo handles DST automatically
    private static readonly TimeZoneInfo NzTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

    public CleanupService(IServiceProvider serviceProvider, ILogger<CleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CleanupService started. Will run daily at 3am NZST");

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = CalculateDelayUntilNext3amNz();
            _logger.LogInformation("CleanupService sleeping for {Delay} until next 3am NZST run", delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("CleanupService stopping due to cancellation");
                break;
            }

            try
            {
                await RunCleanup(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CleanupService encountered an error during cleanup");
            }
        }

        _logger.LogInformation("CleanupService stopped");
    }

    private async Task RunCleanup(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CleanupService starting cleanup run");

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMergicianRepository>();
        var gitlabService = scope.ServiceProvider.GetRequiredService<GitlabService>();
        var userFactory = scope.ServiceProvider.GetRequiredService<GitlabUserFactory>();

        if (!userFactory.IsServiceTokenConfigured)
        {
            _logger.LogWarning("CleanupService skipping: GitLab service token not configured");
            return;
        }

        if (!repository.IsHealthy())
        {
            _logger.LogWarning("CleanupService skipping: database is not healthy");
            return;
        }

        var serviceUser = userFactory.GetServiceUser();
        var allBranches = repository.GetAllBranches();
        _logger.LogInformation("CleanupService checking {Count} tracked branches", allBranches.Count);

        var deletedCount = 0;

        foreach (var branch in allBranches)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var exists = await gitlabService.BranchExists(serviceUser, branch.ProjectId, branch.BranchName);
            if (!exists)
            {
                _logger.LogInformation(
                    "CleanupService: branch '{BranchName}' in project {ProjectId} no longer exists, removing",
                    branch.BranchName, branch.ProjectId);

                repository.DeleteBranch(branch.Id);
                deletedCount++;
            }
        }

        // Clean up any empty merge groups
        var emptyGroups = repository.GetEmptyMergeGroups();
        foreach (var group in emptyGroups)
        {
            _logger.LogInformation(
                "CleanupService: removing empty merge group {MergeGroupId} '{Name}'",
                group.Id, group.Name);
            repository.DeleteMergeGroup(group.Id);
        }

        _logger.LogInformation(
            "CleanupService completed: removed {DeletedBranches} branches, {EmptyGroups} empty merge groups",
            deletedCount, emptyGroups.Count);
    }

    private static TimeSpan CalculateDelayUntilNext3amNz()
    {
        var nowUtc = DateTime.UtcNow;
        var nowNz = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, NzTimeZone);

        // Target 3am today or tomorrow
        var targetNz = nowNz.Date.AddHours(3);
        if (nowNz >= targetNz)
        {
            targetNz = targetNz.AddDays(1);
        }

        var targetUtc = TimeZoneInfo.ConvertTimeToUtc(targetNz, NzTimeZone);
        var delay = targetUtc - nowUtc;

        // Safety: ensure we always wait at least 1 minute
        if (delay < TimeSpan.FromMinutes(1))
        {
            delay = TimeSpan.FromMinutes(1);
        }

        return delay;
    }
}
