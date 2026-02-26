using Mergician.Entities;
using Mergician.Entities.Database;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.Time;
using System.Runtime.CompilerServices;

namespace Mergician.Services.Gitlab;

/// <summary>
///     Provides activity-related operations for the current user.
///     Dashboard data is served from the database; background sync threads
///     (managed by <see cref="UserActivitySyncService" />) keep the database current.
///     MR and approval resolution is handled by the refresh endpoint.
/// </summary>
public class GitlabActivityService
{
    private static readonly TimeSpan _maxActivityLookback = TimeSpan.FromDays(14);

    private readonly GitlabPipelineService _gitlabPipelineService;

    private readonly GitlabService _gitlabService;

    private readonly ILogger<GitlabActivityService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    public GitlabActivityService(
        GitlabService gitlabService,
        GitlabPipelineService gitlabPipelineService,
        IMergeGroupRepository mergeGroupRepository,
        ILogger<GitlabActivityService> logger)
    {
        _gitlabService = gitlabService;
        _gitlabPipelineService = gitlabPipelineService;
        _mergeGroupRepository = mergeGroupRepository;
        _logger = logger;
    }

    /// <summary>
    ///     Returns a diff between the frontend's known branches and the current database state.
    ///     Added branches include full structural data (no MR/approval resolution).
    ///     Removed branches identify entries the frontend should remove by their database ID.
    /// </summary>
    public DashboardPollResponse GetDashboardDiff(
        int gitlabUserId,
        List<KnownBranch> knownBranches)
    {
        var dbBranches = _mergeGroupRepository.GetUserBranches(gitlabUserId);

        // Index DB branches by their primary key
        var dbById = new Dictionary<int, BranchWithMergeGroupInfo>();
        foreach (var b in dbBranches)
        {
            dbById[b.BranchInProjectId] = b;
        }

        // Collect known IDs sent by the frontend
        var knownIds = new HashSet<int>();
        foreach (var k in knownBranches)
        {
            knownIds.Add(k.BranchInProjectId);
        }

        // Added: in DB but not known to frontend
        var added = new List<BranchActivity>();
        foreach (var (id, branch) in dbById)
        {
            if (knownIds.Contains(id))
            {
                continue;
            }

            var projectName = GetProjectDisplayName(branch.ProjectName, branch.ProjectId);
            var lastUpdated = UtcTimestamp.EnsureUtc(
                branch.LastUpdateTime,
                () =>
                    $"GitlabActivityService.GetDashboardDiff branch '{branch.BranchName}'/{branch.ProjectId}",
                _logger);

            added.Add(
                new BranchActivity(
                    branch.BranchName,
                    branch.ProjectId,
                    projectName,
                    branch.ProjectName,
                    null,
                    null,
                    null,
                    lastUpdated,
                    branch.MergeGroupId,
                    BranchInProjectId: branch.BranchInProjectId));
        }

        // Removed: known to frontend but not in DB
        var removed = new List<int>();
        foreach (var k in knownBranches)
        {
            if (!dbById.ContainsKey(k.BranchInProjectId))
            {
                removed.Add(k.BranchInProjectId);
            }
        }

        _logger.LogDebug(
            "Dashboard diff for user {UserId}: {Added} added, {Removed} removed",
            gitlabUserId,
            added.Count,
            removed.Count);

        return new DashboardPollResponse(added, removed);
    }

    /// <summary>
    ///     Fetches push events from GitLab since the given time and stores discovered
    ///     branches in the database. Called by the background sync thread.
    /// </summary>
    public async Task SyncUserActivityFromGitLab(
        GitlabAccessDetailsForUser accessDetailsForUser,
        int gitlabUserId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Syncing GitLab activity for user {UserId} since {Since}",
            gitlabUserId,
            since);

        var pushEvents = _gitlabService.GetPushEventsSince(accessDetailsForUser, since, cancellationToken);
        var returnedKeys = new HashSet<string>();

        await foreach (var _ in FetchAndStoreBranchActivityRecords(
                           accessDetailsForUser,
                           gitlabUserId,
                           pushEvents,
                           returnedKeys,
                           cancellationToken))
        {
            // Consume the enumerable to trigger DB storage; results are not needed
        }
    }

    /// <summary>
    ///     Determines the start time for backfilling a user's activity.
    ///     Uses the latest branch record timestamp or 14 days ago, whichever is more recent.
    /// </summary>
    public DateTimeOffset GetBackfillSince(int gitlabUserId)
    {
        var sinceLimit = DateTimeOffset.UtcNow.Subtract(_maxActivityLookback);

        var userBranches = _mergeGroupRepository.GetUserBranches(gitlabUserId);
        if (userBranches.Count == 0)
        {
            _logger.LogDebug(
                "No existing branches for user {UserId}; backfilling from lookback limit {Limit}",
                gitlabUserId,
                sinceLimit);

            return sinceLimit;
        }

        var latestRecord = DateTimeOffset.MinValue;
        foreach (var branch in userBranches)
        {
            var ts = UtcTimestamp.EnsureUtc(
                branch.LastUpdateTime,
                () =>
                    $"GitlabActivityService.GetBackfillSince branch '{branch.BranchName}'/{branch.ProjectId}",
                _logger);

            if (ts > latestRecord)
            {
                latestRecord = ts;
            }
        }

        var result = latestRecord > sinceLimit ? latestRecord : sinceLimit;
        _logger.LogDebug(
            "Backfill for user {UserId}: latest record at {LatestRecord}, using {Result}",
            gitlabUserId,
            latestRecord,
            result);

        return result;
    }

    /// <summary>
    ///     Checks all tracked branches for a user and removes any that have been deleted from GitLab.
    ///     Called by the background sync thread during each poll cycle.
    /// </summary>
    public async Task CleanupDeletedBranches(
        GitlabAccessDetailsForUser accessDetailsForUser,
        int gitlabUserId,
        CancellationToken cancellationToken)
    {
        var userBranches = _mergeGroupRepository.GetUserBranches(gitlabUserId);
        _logger.LogDebug(
            "Checking {Count} tracked branches for user {UserId} for deletion",
            userBranches.Count,
            gitlabUserId);

        foreach (var branch in userBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lookup = await _gitlabService.GetBranchLookupResult(
                accessDetailsForUser,
                branch.ProjectId,
                branch.BranchName);

            if (lookup.IsMissing)
            {
                _logger.LogInformation(
                    "Background sync: branch '{BranchName}' in project {ProjectId} no longer exists, removing",
                    branch.BranchName,
                    branch.ProjectId);

                RemoveBranchAndCleanup(branch.BranchInProjectId);
            }
            else if (lookup.IsUnavailable)
            {
                _logger.LogDebug(
                    "Background sync: branch lookup unavailable for '{BranchName}' in project {ProjectId}; skipping",
                    branch.BranchName,
                    branch.ProjectId);
            }
        }
    }

    /// <summary>
    ///     Returns a diff between the frontend's known branches and the current database state
    ///     for a specific merge group. Similar to <see cref="GetDashboardDiff" /> but scoped
    ///     to a single merge group. Returns null if the merge group does not exist.
    /// </summary>
    public MergeGroupPollResponse? GetMergeGroupDiff(
        int gitlabUserId,
        int mergeGroupId,
        List<KnownBranch> knownBranches)
    {
        var mergeGroup = _mergeGroupRepository.GetMergeGroup(gitlabUserId, mergeGroupId);
        if (mergeGroup == null)
        {
            _logger.LogInformation(
                "No merge group found for user {UserId} and merge group {MergeGroupId} during poll",
                gitlabUserId,
                mergeGroupId);

            return null;
        }

        var knownIds = new HashSet<int>();
        foreach (var k in knownBranches)
        {
            knownIds.Add(k.BranchInProjectId);
        }

        // Added: in merge group but not known to frontend
        var added = new List<BranchActivity>();
        foreach (var branch in mergeGroup.Branches)
        {
            if (knownIds.Contains(branch.BranchInProjectId))
            {
                continue;
            }

            var projectName = GetProjectDisplayName(branch.ProjectName, branch.ProjectId);
            var lastUpdated = UtcTimestamp.EnsureUtc(
                branch.LastUpdateTime,
                () =>
                    $"GitlabActivityService.GetMergeGroupDiff branch '{branch.BranchName}'/{branch.ProjectId}",
                _logger);

            added.Add(
                new BranchActivity(
                    branch.BranchName,
                    branch.ProjectId,
                    projectName,
                    branch.ProjectName,
                    null,
                    null,
                    null,
                    lastUpdated,
                    branch.MergeGroupId,
                    BranchInProjectId: branch.BranchInProjectId));
        }

        // Removed: known to frontend but not in merge group
        var dbIds = new HashSet<int>(mergeGroup.Branches.Select(b => b.BranchInProjectId));
        var removed = new List<int>();
        foreach (var k in knownBranches)
        {
            if (!dbIds.Contains(k.BranchInProjectId))
            {
                removed.Add(k.BranchInProjectId);
            }
        }

        _logger.LogDebug(
            "Merge group {MergeGroupId} diff for user {UserId}: {Added} added, {Removed} removed",
            mergeGroupId,
            gitlabUserId,
            added.Count,
            removed.Count);

        return new MergeGroupPollResponse(mergeGroup.Id, mergeGroup.Name, added, removed);
    }

    /// <summary>
    ///     Returns fully resolved details for a single merge group.
    /// </summary>
    public async Task<MergeGroupDetailsResponse?> GetMergeGroupDetails(
        GitlabAccessDetailsForUser accessDetailsForCurrentUser,
        int gitlabUserId,
        int mergeGroupId,
        CancellationToken cancellationToken = default)
    {
        var mergeGroup = _mergeGroupRepository.GetMergeGroup(gitlabUserId, mergeGroupId);
        if (mergeGroup == null)
        {
            _logger.LogInformation(
                "No merge group details found for user {UserId} and merge group {MergeGroupId}",
                gitlabUserId,
                mergeGroupId);

            return null;
        }

        var resolvedBranches = new List<BranchActivity>();
        foreach (var branch in mergeGroup.Branches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ShouldSkipBranchByLookup(
                    accessDetailsForCurrentUser,
                    branch.BranchName,
                    branch.ProjectId,
                    branch.BranchInProjectId,
                    $"merge group {mergeGroupId} details load",
                    cancellationToken))
            {
                continue;
            }

            if (ShouldSkipScheduledForDeletion(
                    branch.BranchName,
                    branch.ProjectId,
                    branch.ProjectName,
                    branch.BranchInProjectId,
                    $"merge group {mergeGroupId} details load"))
            {
                continue;
            }

            var projectNameWithNamespace = branch.ProjectName;
            var projectName = GetProjectDisplayName(projectNameWithNamespace, branch.ProjectId);
            var lastUpdated = UtcTimestamp.EnsureUtc(
                branch.LastUpdateTime,
                () =>
                    $"GitlabActivityService.GetMergeGroupDetails branch '{branch.BranchName}'/{branch.ProjectId}",
                _logger);

            var pending = new BranchActivity(
                branch.BranchName,
                branch.ProjectId,
                projectName,
                projectNameWithNamespace,
                null,
                null,
                null,
                lastUpdated,
                branch.MergeGroupId,
                BranchInProjectId: branch.BranchInProjectId);

            var resolved = await ResolveBranchActivityIn(accessDetailsForCurrentUser, pending, cancellationToken);
            resolvedBranches.Add(resolved);
        }

        return new MergeGroupDetailsResponse(mergeGroupId, mergeGroup.Name, resolvedBranches);
    }

    /// <summary>
    ///     Streams refreshed MR and approval status for specific branch-project pairs.
    ///     When a branch no longer exists, yields a deleted notification instead.
    /// </summary>
    public async IAsyncEnumerable<object> StreamRefreshBranchStatus(
        GitlabAccessDetailsForUser accessDetailsForUser,
        List<BranchRefreshRequest> branches,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Streaming refresh for {Count} branch-project pairs", branches.Count);

        foreach (var branch in branches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check if branch still exists
            var branchLookup = await _gitlabService.GetBranchLookupResult(
                accessDetailsForUser,
                branch.ProjectId,
                branch.BranchName);

            if (branchLookup.IsMissing)
            {
                _logger.LogInformation(
                    "Branch '{BranchName}' in project {ProjectId} no longer exists during refresh",
                    branch.BranchName,
                    branch.ProjectId);

                // Find and remove from DB
                var branchRecord = _mergeGroupRepository.GetBranchRecord(branch.BranchName, branch.ProjectId);

                if (branchRecord != null)
                {
                    RemoveBranchAndCleanup(branchRecord.Id);
                }

                yield return new BranchDeletedNotification(
                    branch.BranchName,
                    branch.ProjectId,
                    branch.MergeGroupId,
                    branchRecord?.Id);

                continue;
            }

            if (branchLookup.IsUnavailable)
            {
                _logger.LogWarning(
                    "Branch lookup unavailable for branch '{BranchName}' in project {ProjectId} during refresh; skipping this branch update",
                    branch.BranchName,
                    branch.ProjectId);

                continue;
            }

            var project = await _gitlabService.GetProject(accessDetailsForUser, branch.ProjectId);
            if (project == null || GitlabService.IsScheduledForDeletion(project.NameWithNamespace))
            {
                var reason = project == null
                    ? "project not found"
                    : $"project/group is scheduled for deletion ('{project.NameWithNamespace}')";

                _logger.LogInformation(
                    "Branch '{BranchName}' in project {ProjectId} treated as deleted during refresh: {Reason}",
                    branch.BranchName,
                    branch.ProjectId,
                    reason);

                var branchRecord = _mergeGroupRepository.GetBranchRecord(branch.BranchName, branch.ProjectId);
                if (branchRecord != null)
                {
                    RemoveBranchAndCleanup(branchRecord.Id);
                }

                yield return new BranchDeletedNotification(
                    branch.BranchName,
                    branch.ProjectId,
                    branch.MergeGroupId,
                    branchRecord?.Id);

                continue;
            }

            var projectNameWithNamespace = project.NameWithNamespace;
            var projectName = string.IsNullOrWhiteSpace(project.Name)
                ? GetProjectDisplayName(projectNameWithNamespace, branch.ProjectId)
                : project.Name;

            var existingRecord = _mergeGroupRepository.GetBranchRecord(branch.BranchName, branch.ProjectId);

            var pendingActivity = new BranchActivity(
                branch.BranchName,
                branch.ProjectId,
                projectName,
                projectNameWithNamespace,
                null,
                null,
                null,
                branch.LastUpdated,
                branch.MergeGroupId,
                null,
                null,
                project.WebUrl,
                BranchInProjectId: existingRecord?.Id);

            var activity = await ResolveBranchActivityIn(accessDetailsForUser, pendingActivity, cancellationToken);

            yield return activity;
        }

        _logger.LogInformation("Finished streaming refresh for {Count} branch-project pairs", branches.Count);
    }

    /// <summary>
    ///     Discovers branches from push events, stores them in the DB, and yields BranchActivity records
    ///     for branches not already in the returnedKeys set. Updates the set as discoveries are made.
    /// </summary>
    private async IAsyncEnumerable<BranchActivity> FetchAndStoreBranchActivityRecords(
        GitlabAccessDetailsForUser accessDetailsForUser,
        int gitlabUserId,
        IAsyncEnumerable<(string BranchName, int ProjectId, DateTimeOffset CreatedAt)> pushEvents,
        HashSet<string> returnedKeys,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var pushEvent in pushEvents.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (GitlabService.IsPossibleDefaultBranch(pushEvent.BranchName))
            {
                _logger.LogDebug(
                    "Skipping default branch '{BranchName}' in project {ProjectId}",
                    pushEvent.BranchName,
                    pushEvent.ProjectId);

                continue;
            }

            var key = $"{pushEvent.BranchName}:{pushEvent.ProjectId}";

            if (await ShouldSkipBranchByLookup(
                    accessDetailsForUser,
                    pushEvent.BranchName,
                    pushEvent.ProjectId,
                    null,
                    "push-event processing",
                    cancellationToken))
            {
                continue;
            }

            var project = await _gitlabService.GetProject(accessDetailsForUser, pushEvent.ProjectId);
            if (project == null)
            {
                _logger.LogInformation(
                    "Project {ProjectId} not found while processing push event for branch '{BranchName}'; skipping",
                    pushEvent.ProjectId,
                    pushEvent.BranchName);

                continue;
            }

            if (ShouldSkipScheduledForDeletion(
                    pushEvent.BranchName,
                    pushEvent.ProjectId,
                    project.NameWithNamespace,
                    null,
                    "push-event processing"))
            {
                continue;
            }

            var projectNameWithNamespace = project.NameWithNamespace;
            if (string.IsNullOrWhiteSpace(project.Name))
            {
                _logger.LogError("Invalid empty project name in project id {id}", pushEvent.ProjectId);
                continue;
            }

            // Store in database
            var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(
                pushEvent.BranchName,
                pushEvent.ProjectId,
                projectNameWithNamespace);

            var mergeGroup = _mergeGroupRepository.GetOrCreateMergeGroup(pushEvent.BranchName);
            _mergeGroupRepository.EnsureBranchInMergeGroup(mergeGroup.Id, branchRecord.Id);
            _mergeGroupRepository.EnsureUserInMergeGroup(gitlabUserId, mergeGroup.Id);

            var lastUpdated = pushEvent.CreatedAt.ToUniversalTime();
            if (lastUpdated > mergeGroup.LastUpdateTime)
            {
                _mergeGroupRepository.UpdateMergeGroupTimestamp(mergeGroup.Id, lastUpdated);
            }

            if (!returnedKeys.Add(key))
            {
                _logger.LogDebug(
                    "Branch '{BranchName}' in project {ProjectId} already returned, updating DB only",
                    pushEvent.BranchName,
                    pushEvent.ProjectId);

                continue;
            }

            yield return new BranchActivity(
                pushEvent.BranchName,
                pushEvent.ProjectId,
                project.Name,
                projectNameWithNamespace,
                null,
                null,
                null,
                pushEvent.CreatedAt,
                mergeGroup.Id,
                null,
                null,
                project.WebUrl,
                BranchInProjectId: branchRecord.Id);
        }
    }

    private async Task<bool> ShouldSkipBranchByLookup(
        GitlabAccessDetailsForUser accessDetailsForUser,
        string branchName,
        int projectId,
        int? trackedBranchInProjectId,
        string operationName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var branchLookup = await _gitlabService.GetBranchLookupResult(
            accessDetailsForUser,
            projectId,
            branchName);

        if (branchLookup.IsMissing)
        {
            _logger.LogInformation(
                "Skipping branch '{BranchName}' in project {ProjectId} during {OperationName}: branch no longer exists",
                branchName,
                projectId,
                operationName);

            if (trackedBranchInProjectId.HasValue)
            {
                _logger.LogInformation(
                    "Removing tracked branch record {BranchRecordId} for '{BranchName}' in project {ProjectId} during {OperationName}",
                    trackedBranchInProjectId.Value,
                    branchName,
                    projectId,
                    operationName);

                RemoveBranchAndCleanup(trackedBranchInProjectId.Value);
            }

            return true;
        }

        if (branchLookup.IsUnavailable)
        {
            _logger.LogWarning(
                "Skipping branch '{BranchName}' in project {ProjectId} during {OperationName}: branch lookup unavailable",
                branchName,
                projectId,
                operationName);

            return true;
        }

        return false;
    }

    private bool ShouldSkipScheduledForDeletion(
        string branchName,
        int projectId,
        string projectNameWithNamespace,
        int? trackedBranchInMergeGroupId,
        string operationName)
    {
        if (!GitlabService.IsScheduledForDeletion(projectNameWithNamespace))
        {
            return false;
        }

        _logger.LogInformation(
            "Skipping branch '{BranchName}' in project {ProjectId} during {OperationName}: project/group is scheduled for deletion ('{ProjectNameWithNamespace}')",
            branchName,
            projectId,
            operationName,
            projectNameWithNamespace);

        if (trackedBranchInMergeGroupId.HasValue)
        {
            _logger.LogInformation(
                "Removing tracked branch record {BranchRecordId} for '{BranchName}' in project {ProjectId} during {OperationName} because project is scheduled for deletion",
                trackedBranchInMergeGroupId.Value,
                branchName,
                projectId,
                operationName);

            RemoveBranchAndCleanup(trackedBranchInMergeGroupId.Value);
        }

        return true;
    }

    private string GetProjectDisplayName(string projectNameWithNamespace, int projectId)
    {
        var trimmed = projectNameWithNamespace.Trim();
        if (trimmed.Length == 0)
        {
            _logger.LogWarning(
                "Project {ProjectId} has empty NameWithNamespace; using empty display name",
                projectId);

            return trimmed;
        }

        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash >= trimmed.Length - 1)
        {
            _logger.LogDebug(
                "Project {ProjectId} NameWithNamespace '{ProjectNameWithNamespace}' has no namespace separator; using full value as display name",
                projectId,
                trimmed);

            return trimmed;
        }

        return trimmed[(lastSlash + 1)..].Trim();
    }

    /// <summary>
    ///     Resolves a branch's MR and approval status into a fully populated BranchActivity record.
    /// </summary>
    private async Task<BranchActivity> ResolveBranchActivityIn(
        GitlabAccessDetailsForUser accessDetailsForUser,
        BranchActivity activity,
        CancellationToken cancellationToken = default)
    {
        var mergeRequests = await _gitlabService.GetMergeRequests(
            accessDetailsForUser,
            activity.ProjectId,
            activity.BranchName);

        var hasMr = mergeRequests.Count > 0;
        int? approvalsRequired = null;
        int? approvalsGiven = null;
        string? mrTitle = null;
        string? mrUrl = null;

        if (hasMr)
        {
            _logger.LogDebug(
                "Branch '{BranchName}' has {MrCount} open MR(s) in project {ProjectId}",
                activity.BranchName,
                mergeRequests.Count,
                activity.ProjectId);

            var first = mergeRequests[0];
            mrTitle = first.Title;
            mrUrl = first.WebUrl;

            var approval = await _gitlabService.GetMergeRequestApprovals(
                accessDetailsForUser,
                activity.ProjectId,
                first.Iid);

            if (approval != null)
            {
                approvalsGiven = approval.ApprovedBy.Count;
                approvalsRequired = Math.Max(approval.ApprovalsRequired ?? 0, 0);
            }
        }

        var project = await _gitlabService.GetProject(accessDetailsForUser, activity.ProjectId);
        var projectUrl = !string.IsNullOrWhiteSpace(project?.WebUrl)
            ? project.WebUrl
            : activity.ProjectUrl;

        var buildJobs = await _gitlabPipelineService.GetLatestExternalJobsForBranch(
            accessDetailsForUser,
            activity.ProjectId,
            activity.BranchName,
            cancellationToken);

        return activity with
        {
            HasMergeRequest = hasMr,
            ApprovalsRequired = approvalsRequired,
            ApprovalsGiven = approvalsGiven,
            MergeRequestTitle = mrTitle,
            MergeRequestUrl = mrUrl,
            ProjectUrl = projectUrl,
            BuildJobs = buildJobs
        };
    }

    /// <summary>
    ///     Removes a branch from the DB and cleans up any empty merge groups.
    /// </summary>
    private void RemoveBranchAndCleanup(int branchInMergeGroupId)
    {
        _mergeGroupRepository.DeleteBranch(branchInMergeGroupId);

        // Clean up any merge groups that are now empty
        var emptyGroups = _mergeGroupRepository.GetEmptyMergeGroups();
        foreach (var group in emptyGroups)
        {
            _logger.LogInformation(
                "Removing empty merge group {MergeGroupId} '{Name}'",
                group.Id,
                group.Name);

            _mergeGroupRepository.DeleteMergeGroup(group.Id);
        }
    }
}