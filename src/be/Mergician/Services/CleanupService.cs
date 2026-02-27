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
        var mergeGroupRepository = scope.ServiceProvider.GetRequiredService<IMergeGroupRepository>();
        var gitlabService = scope.ServiceProvider.GetRequiredService<GitlabService>();
        var userFactory = scope.ServiceProvider.GetRequiredService<GitlabUserFactory>();

        if (!userFactory.IsServiceTokenConfigured)
        {
            _logger.LogWarning("CleanupService skipping: GitLab service token not configured");
            return;
        }

        var serviceUser = userFactory.GetServiceUser();
        var allBranches = mergeGroupRepository.GetAllBranches();
        _logger.LogInformation("CleanupService checking {Count} tracked branches", allBranches.Count);

        var deletedCount = 0;

        foreach (var branch in allBranches)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var branchLookup = await gitlabService.GetBranchLookupResult(
                serviceUser,
                branch.ProjectId,
                branch.BranchName);

            if (branchLookup.IsMissing)
            {
                _logger.LogInformation(
                    "CleanupService: branch '{BranchName}' in project {ProjectId} no longer exists, removing",
                    branch.BranchName, branch.ProjectId);

                mergeGroupRepository.DeleteBranch(branch.Id);
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
                group.Id, group.Name);
            mergeGroupRepository.DeleteMergeGroup(group.Id);
        }

        _logger.LogInformation(
            "CleanupService completed: removed {DeletedBranches} branches, {EmptyGroups} empty merge groups",
            deletedCount, emptyGroups.Count);
    }

    private static TimeSpan CalculateDelayUntilNext3amNz()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowNz = TimeZoneInfo.ConvertTime(nowUtc, NzTimeZone);

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
