using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.Time;
using System.Runtime.CompilerServices;

namespace Mergician.Services.Gitlab;

/// <summary>
///     Provides activity-related operations for the current user,
///     streaming branch activity data as it is discovered from GitLab.
///     Uses the database to cache results and track merge groups.
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
    ///     Streams branch activity records. First returns cached data from the database,
    ///     then fetches new data from GitLab, stores it in the database, and streams those too.
    ///     MR and approval data is resolved live (not cached).
    /// </summary>
    public async IAsyncEnumerable<BranchActivity> StreamBranchActivity(
        GitlabAccessUser currentUser,
        int gitlabUserId,
        DateTimeOffset? lastPollTime,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 1. Return cached data from DB first
        _logger.LogInformation("Fetching cached branches for user {UserId} from database", gitlabUserId);

        var cachedBranches = _mergeGroupRepository.GetUserBranches(gitlabUserId);
        _logger.LogInformation(
            "Found {Count} cached branches for user {UserId}",
            cachedBranches.Count,
            gitlabUserId);

        DateTimeOffset? latestCachedActivity = null;

        // Track returned branches to avoid sending duplicates to the UI when checking through DB and activity events.
        var returnedKeys = new HashSet<string>();

        foreach (var cached in cachedBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ShouldSkipBranchByLookup(
                    currentUser,
                    cached.BranchName,
                    cached.ProjectId,
                    cached.BranchInProjectId,
                    "cached activity stream",
                    cancellationToken))
            {
                continue;
            }

            var key = $"{cached.BranchName}:{cached.ProjectId}";
            returnedKeys.Add(key);

            var cachedLastUpdatedUtc = UtcTimestamp.EnsureUtc(
                cached.LastUpdateTime,
                () =>
                    $"GitlabActivityService.StreamBranchActivity cached branch '{cached.BranchName}'/{cached.ProjectId}",
                _logger);

            if (!latestCachedActivity.HasValue || cachedLastUpdatedUtc > latestCachedActivity.Value)
            {
                latestCachedActivity = cachedLastUpdatedUtc;
            }

            var projectNameWithNamespace = cached.ProjectName;
            var projectName = GetProjectDisplayName(projectNameWithNamespace, cached.ProjectId);

            // Yield initial record with unknown MR status
            var cachedActivity = new BranchActivity(
                cached.BranchName,
                cached.ProjectId,
                projectName,
                projectNameWithNamespace,
                null,
                null,
                null,
                cachedLastUpdatedUtc,
                cached.MergeGroupId);

            yield return cachedActivity;

            // Yield resolved MR/approval data
            yield return await ResolveBranchActivityIn(currentUser, cachedActivity, cancellationToken);
        }

        // 2. Fetch new data from GitLab
        var fetchSince = DetermineFetchSince(lastPollTime, latestCachedActivity);

        _logger.LogInformation(
            "Fetching GitLab events for user {UserId} since {Since}",
            gitlabUserId,
            fetchSince);

        var pushEvents = _gitlabService.GetPushEventsSince(
            currentUser,
            fetchSince,
            cancellationToken);

        var records = FetchAndStoreBranchActivityRecords(
            currentUser,
            gitlabUserId,
            pushEvents,
            returnedKeys,
            cancellationToken);

        await foreach (var branch in records)
        {
            // Yield skeleton record to the UI immediately.
            yield return branch;

            // Resolve MR/approval data
            yield return await ResolveBranchActivityIn(currentUser, branch, cancellationToken);
        }

        _logger.LogInformation("Finished streaming branch activity for user {UserId}", gitlabUserId);
    }

    /// <summary>
    ///     Returns branch activity records for events that occurred since the given time.
    ///     Used for polling to detect new pushes without re-fetching the full history.
    ///     Returns fully resolved records (MR and approval data included).
    /// </summary>
    public async Task<ActivityPollResponse> GetPolledActivitySince(
        GitlabAccessUser currentUser,
        int gitlabUserId,
        DateTimeOffset lastPollTime,
        CancellationToken cancellationToken = default)
    {
        var fetchSince = DetermineFetchSince(lastPollTime, null);

        _logger.LogDebug(
            "Polling for activity for user {UserId} since {SinceUtc}",
            gitlabUserId,
            fetchSince);

        var pushEvents = _gitlabService.GetPushEventsSince(
            currentUser,
            fetchSince,
            cancellationToken);

        var results = new List<BranchActivity>();
        var deletedBranches = new List<BranchDeletedNotification>();
        var existingKeys = new HashSet<string>();

        var records = FetchAndStoreBranchActivityRecords(
            currentUser,
            gitlabUserId,
            pushEvents,
            existingKeys,
            cancellationToken);

        await foreach (var branch in records)
        {
            var activity = await ResolveBranchActivityIn(currentUser, branch, cancellationToken);

            results.Add(activity);
        }

        _logger.LogInformation("Returning {Count} branch activity records from poll", results.Count);
        return new ActivityPollResponse(results, deletedBranches, DateTimeOffset.UtcNow);
    }

    /// <summary>
    ///     Returns fully resolved details for a single merge group.
    /// </summary>
    public async Task<MergeGroupDetailsResponse?> GetMergeGroupDetails(
        GitlabAccessUser currentUser,
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
                    currentUser,
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
                branch.MergeGroupId);

            var resolved = await ResolveBranchActivityIn(currentUser, pending, cancellationToken);
            resolvedBranches.Add(resolved);
        }

        return new MergeGroupDetailsResponse(mergeGroupId, mergeGroup.Name, resolvedBranches);
    }

    /// <summary>
    ///     Streams refreshed MR and approval status for specific branch-project pairs.
    ///     When a branch no longer exists, yields a deleted notification instead.
    /// </summary>
    public async IAsyncEnumerable<object> StreamRefreshBranchStatus(
        GitlabAccessUser user,
        List<BranchRefreshRequest> branches,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Streaming refresh for {Count} branch-project pairs", branches.Count);

        foreach (var branch in branches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check if branch still exists
            var branchLookup = await _gitlabService.GetBranchLookupResult(
                user,
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
                    branch.MergeGroupId);

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

            var project = await _gitlabService.GetProject(user, branch.ProjectId);
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
                    branch.MergeGroupId);

                continue;
            }

            var projectNameWithNamespace = project.NameWithNamespace;
            var projectName = string.IsNullOrWhiteSpace(project.Name)
                ? GetProjectDisplayName(projectNameWithNamespace, branch.ProjectId)
                : project.Name;

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
                project.WebUrl);

            var activity = await ResolveBranchActivityIn(user, pendingActivity, cancellationToken);

            yield return activity;
        }

        _logger.LogInformation("Finished streaming refresh for {Count} branch-project pairs", branches.Count);
    }

    /// <summary>
    ///     Discovers branches from push events, stores them in the DB, and yields BranchActivity records
    ///     for branches not already in the returnedKeys set. Updates the set as discoveries are made.
    /// </summary>
    private async IAsyncEnumerable<BranchActivity> FetchAndStoreBranchActivityRecords(
        GitlabAccessUser user,
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
                    user,
                    pushEvent.BranchName,
                    pushEvent.ProjectId,
                    null,
                    "push-event processing",
                    cancellationToken))
            {
                continue;
            }

            var project = await _gitlabService.GetProject(user, pushEvent.ProjectId);
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
                project.WebUrl);
        }
    }

    private DateTimeOffset DetermineFetchSince(
        DateTimeOffset? requestLastPollTime,
        DateTimeOffset? latestCachedActivity)
    {
        var sinceLimit = DateTimeOffset.UtcNow.Subtract(_maxActivityLookback);

        if (requestLastPollTime.HasValue)
        {
            var requested = UtcTimestamp.EnsureUtc(
                requestLastPollTime.Value,
                () => "GitlabActivityService.DetermineFetchSince requestLastPollTime",
                _logger);

            if (requested < sinceLimit)
            {
                _logger.LogInformation(
                    "Requested last poll time {Requested} is older than {Limit}; clamping to lookback limit",
                    requested,
                    sinceLimit);

                return sinceLimit;
            }

            _logger.LogDebug(
                "Using frontend-provided last poll time {Requested} as fetch since",
                requested);

            return requested;
        }

        if (latestCachedActivity.HasValue && latestCachedActivity.Value > sinceLimit)
        {
            _logger.LogDebug(
                "Using latest cached activity timestamp {LatestCached} as fetch since",
                latestCachedActivity.Value);

            return latestCachedActivity.Value;
        }

        _logger.LogDebug(
            "No valid frontend cursor or recent cached activity; using lookback limit {Limit}",
            sinceLimit);

        return sinceLimit;
    }

    private async Task<bool> ShouldSkipBranchByLookup(
        GitlabAccessUser user,
        string branchName,
        int projectId,
        int? trackedBranchInProjectId,
        string operationName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var branchLookup = await _gitlabService.GetBranchLookupResult(
            user,
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
        GitlabAccessUser user,
        BranchActivity activity,
        CancellationToken cancellationToken = default)
    {
        var mergeRequests = await _gitlabService.GetMergeRequests(
            user,
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
                user,
                activity.ProjectId,
                first.Iid);

            if (approval != null)
            {
                approvalsGiven = approval.ApprovedBy.Count;
                approvalsRequired = Math.Max(approval.ApprovalsRequired ?? 0, 0);
            }
        }

        var project = await _gitlabService.GetProject(user, activity.ProjectId);
        var projectUrl = !string.IsNullOrWhiteSpace(project?.WebUrl)
            ? project.WebUrl
            : activity.ProjectUrl;

        var buildJobs = await _gitlabPipelineService.GetLatestExternalJobsForBranch(
            user,
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