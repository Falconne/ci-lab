using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.GitLab;
using Util;

namespace Mergician.Services;

/// <summary>
///     Background service that monitors GitLab projects for MRs with the "AutoMerge" label.
///     Runs a loop every 60 seconds. For each monitored project, it:
///     <list type="bullet">
///         <item>Fetches all open MRs with the "AutoMerge" label.</item>
///         <item>For each labeled MR, finds or creates the corresponding merge group and enables auto merge by label.</item>
///         <item>Checks existing merge groups with AutoMergeByLabel=true; if the label has been removed from all
///               monitored-project MRs in the group, disables auto merge.</item>
///     </list>
/// </summary>
public class MonitoredProjectsService : BackgroundService
{
    public const string AutoMergeLabel = "AutoMerge";

    private static readonly TimeSpan _cycleInterval = TimeSpan.FromSeconds(60);

    private readonly GitLabRecoveryService _gitLabRecoveryService;

    private readonly GitLabService _gitLabService;

    private readonly HealthService _healthService;

    private readonly ILogger<MonitoredProjectsService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    private readonly IMonitoredProjectRepository _monitoredProjectRepository;

    private readonly GitLabUserFactory _userFactory;

    public MonitoredProjectsService(
        IMonitoredProjectRepository monitoredProjectRepository,
        IMergeGroupRepository mergeGroupRepository,
        GitLabService gitLabService,
        GitLabUserFactory userFactory,
        GitLabRecoveryService gitLabRecoveryService,
        HealthService healthService,
        ILogger<MonitoredProjectsService> logger)
    {
        _monitoredProjectRepository = monitoredProjectRepository;
        _mergeGroupRepository = mergeGroupRepository;
        _gitLabService = gitLabService;
        _userFactory = userFactory;
        _gitLabRecoveryService = gitLabRecoveryService;
        _healthService = healthService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MonitoredProjectsService starting, waiting for app to be ready");

        while (!_healthService.GetStatus().IsReady && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        _logger.LogInformation("MonitoredProjectsService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStart = DateTime.UtcNow;
            try
            {
                await RunCycle(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (GitLabStartupRequiredException)
            {
                _logger.LogWarning(
                    "MonitoredProjectsService: GitLab is in startup mode, pausing until next cycle");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MonitoredProjectsService: unexpected error during cycle");
            }

            var elapsed = DateTime.UtcNow - cycleStart;
            var remaining = _cycleInterval - elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, stoppingToken);
            }
        }

        _logger.LogInformation("MonitoredProjectsService stopped");
    }

    private async Task RunCycle(CancellationToken cancellationToken)
    {
        if (_gitLabRecoveryService.IsInGitLabRecoveryMode)
        {
            _logger.LogDebug("MonitoredProjectsService: skipping cycle, GitLab recovery mode is active");
            return;
        }

        var monitoredProjectIds = _monitoredProjectRepository.GetAllProjectIds();
        if (monitoredProjectIds.Count == 0)
        {
            _logger.LogDebug("MonitoredProjectsService: no monitored projects configured, skipping cycle");
            return;
        }

        _logger.LogDebug(
            "MonitoredProjectsService: running cycle for {Count} monitored projects",
            monitoredProjectIds.Count);

        var serviceUser = _userFactory.GetServiceUser();

        // Collect all (projectId, branchName) pairs currently having the AutoMerge label
        var labeledBranches = new HashSet<(int ProjectId, string BranchName)>();

        foreach (var projectId in monitoredProjectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessMonitoredProject(serviceUser, projectId, labeledBranches, cancellationToken);
        }

        // Disable auto merge on groups where the label has been removed from all monitored-project MRs
        DisableLabelRemovedGroups(monitoredProjectIds, labeledBranches, cancellationToken);
    }

    private async Task ProcessMonitoredProject(
        AccessDetailsBase serviceUser,
        int projectId,
        HashSet<(int ProjectId, string BranchName)> labeledBranches,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "MonitoredProjectsService: checking project {ProjectId} for MRs with '{Label}' label",
            projectId,
            AutoMergeLabel);

        var openMrs = await _gitLabService.GetOpenMergeRequestsForProject(
            serviceUser,
            projectId,
            labelFilter: AutoMergeLabel,
            cancellationToken);

        _logger.LogDebug(
            "MonitoredProjectsService: found {Count} open MRs with '{Label}' label in project {ProjectId}",
            openMrs.Count,
            AutoMergeLabel,
            projectId);

        foreach (var mr in openMrs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (mr.SourceBranch.IsEmpty())
            {
                _logger.LogWarning(
                    "MonitoredProjectsService: MR !{Iid} in project {ProjectId} has no source branch, skipping",
                    mr.Iid,
                    projectId);
                continue;
            }

            labeledBranches.Add((projectId, mr.SourceBranch));
            await EnsureAutoMergeEnabledForMr(serviceUser, projectId, mr, cancellationToken);
        }
    }

    private async Task EnsureAutoMergeEnabledForMr(
        AccessDetailsBase serviceUser,
        int projectId,
        Entities.GitLabMergeRequest mr,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "MonitoredProjectsService: ensuring auto merge enabled for MR !{Iid} (branch '{BranchName}') in project {ProjectId}",
            mr.Iid,
            mr.SourceBranch,
            projectId);

        var project = await _gitLabService.GetProject(serviceUser, projectId, cancellationToken);
        if (project == null)
        {
            _logger.LogError(
                "MonitoredProjectsService: could not resolve project {ProjectId}, skipping MR !{Iid}",
                projectId,
                mr.Iid);
            return;
        }

        var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(mr.SourceBranch, project);
        var mergeGroup = _mergeGroupRepository.GetOrCreateMergeGroup(mr.SourceBranch);
        _mergeGroupRepository.EnsureBranchInMergeGroup(mergeGroup.Id, branchRecord.Id);

        if (!mergeGroup.AutoMerge || !mergeGroup.AutoMergeByLabel)
        {
            _logger.LogInformation(
                "MonitoredProjectsService: enabling auto merge by label for merge group {MergeGroupId} '{MergeGroupName}' (triggered by MR !{Iid} in project {ProjectId})",
                mergeGroup.Id,
                mergeGroup.Name,
                mr.Iid,
                projectId);

            _mergeGroupRepository.EnableAutoMergeByLabel(mergeGroup.Id);
        }
        else
        {
            _logger.LogDebug(
                "MonitoredProjectsService: merge group {MergeGroupId} already has auto merge by label enabled",
                mergeGroup.Id);
        }
    }

    private void DisableLabelRemovedGroups(
        List<int> monitoredProjectIds,
        HashSet<(int ProjectId, string BranchName)> labeledBranches,
        CancellationToken cancellationToken)
    {
        var labeledGroups = _mergeGroupRepository.GetMergeGroupsWithAutoMergeByLabel();

        if (labeledGroups.Count == 0)
        {
            return;
        }

        var monitoredProjectSet = monitoredProjectIds.ToHashSet();

        foreach (var group in labeledGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Find branches in this group that belong to monitored projects
            var monitoredBranches = group.Branches
                .Where(b => monitoredProjectSet.Contains(b.ProjectId))
                .ToList();

            if (monitoredBranches.Count == 0)
            {
                _logger.LogDebug(
                    "MonitoredProjectsService: merge group {MergeGroupId} '{MergeGroupName}' has no branches in monitored projects, skipping label check",
                    group.Id,
                    group.Name);
                continue;
            }

            var stillLabeled = monitoredBranches.Any(b => labeledBranches.Contains((b.ProjectId, b.BranchName)));

            if (!stillLabeled)
            {
                _logger.LogInformation(
                    "MonitoredProjectsService: '{Label}' label removed from all monitored-project MRs in merge group {MergeGroupId} '{MergeGroupName}', disabling auto merge",
                    AutoMergeLabel,
                    group.Id,
                    group.Name);

                _mergeGroupRepository.DisableAutoMergeByLabel(group.Id);
            }
            else
            {
                _logger.LogDebug(
                    "MonitoredProjectsService: merge group {MergeGroupId} '{MergeGroupName}' still has '{Label}' label on at least one monitored-project MR",
                    group.Id,
                    group.Name,
                    AutoMergeLabel);
            }
        }
    }
}
