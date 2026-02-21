using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using System.Runtime.CompilerServices;

namespace Mergician.Services.Gitlab;

/// <summary>
///     Provides activity-related operations for the current user,
///     streaming branch activity data as it is discovered from GitLab.
///     Uses the database to cache results and track merge groups.
/// </summary>
public class GitlabActivityService
{
    private readonly GitlabService _gitlabService;

    private readonly ILogger<GitlabActivityService> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    private readonly IUserRepository _userRepository;

    public GitlabActivityService(
        GitlabService gitlabService,
        IMergeGroupRepository mergeGroupRepository,
        IUserRepository userRepository,
        ILogger<GitlabActivityService> logger)
    {
        _gitlabService = gitlabService;
        _mergeGroupRepository = mergeGroupRepository;
        _userRepository = userRepository;
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var since = DateTime.UtcNow.AddDays(-14);

        // 1. Return cached data from DB first
        _logger.LogInformation("Fetching cached branches for user {UserId} from database", gitlabUserId);
        var cachedBranches = _mergeGroupRepository.GetUserBranches(gitlabUserId, since);
        _logger.LogInformation(
            "Found {Count} cached branches for user {UserId}",
            cachedBranches.Count,
            gitlabUserId);

        // Track returned branches to avoid sending duplicates to the UI when checking through DB and activity events.
        var returnedKeys = new HashSet<string>();

        foreach (var cached in cachedBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check if branch still exists
            var branchLookup = await _gitlabService.GetBranchLookupResult(
                currentUser,
                cached.ProjectId,
                cached.BranchName);

            if (branchLookup.IsMissing)
            {
                _logger.LogInformation(
                    "Cached branch '{BranchName}' in project {ProjectId} no longer exists, removing from DB",
                    cached.BranchName,
                    cached.ProjectId);

                RemoveBranchAndCleanup(cached.BranchInProjectId);
                continue;
            }

            if (branchLookup.IsUnavailable)
            {
                _logger.LogWarning(
                    "Branch lookup unavailable for cached branch '{BranchName}' in project {ProjectId}; skipping deletion and continuing",
                    cached.BranchName,
                    cached.ProjectId);

                continue;
            }

            if (GitlabService.IsScheduledForDeletion(cached.ProjectName))
            {
                _logger.LogInformation(
                    "Cached branch '{BranchName}' in project {ProjectId} belongs to a project/group scheduled for deletion ('{ProjectName}'); removing from DB",
                    cached.BranchName,
                    cached.ProjectId,
                    cached.ProjectName);

                RemoveBranchAndCleanup(cached.BranchInProjectId);
                continue;
            }

            var key = $"{cached.BranchName}:{cached.ProjectId}";
            returnedKeys.Add(key);

            // Yield initial record with unknown MR status
            var cachedActivity = new BranchActivity(
                cached.BranchName,
                cached.ProjectId,
                cached.ProjectName,
                null,
                null,
                null,
                new DateTimeOffset(cached.LastUpdateTime, TimeSpan.Zero),
                cached.MergeGroupId);

            yield return cachedActivity;

            // Yield resolved MR/approval data
            yield return await ResolveBranchActivityIn(currentUser, cachedActivity);
        }

        // 2. Fetch new data from GitLab
        var lastPoll = _userRepository.GetLastPollTimestamp(gitlabUserId);
        var fetchSince = lastPoll.HasValue && lastPoll.Value > since
            ? lastPoll.Value
            : since;

        _logger.LogInformation(
            "Fetching GitLab events for user {UserId} since {Since}",
            gitlabUserId,
            fetchSince);

        var pushEvents = _gitlabService.StreamPushEventsSince(
            currentUser,
            AsUtcOffset(fetchSince),
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
            yield return await ResolveBranchActivityIn(currentUser, branch);
        }

        // Update last poll timestamp
        _userRepository.UpsertLastPollTimestamp(gitlabUserId, DateTime.UtcNow);

        _logger.LogInformation("Finished streaming branch activity for user {UserId}", gitlabUserId);
    }

    /// <summary>
    ///     Returns branch activity records for events that occurred since the given time.
    ///     Used for polling to detect new pushes without re-fetching the full history.
    ///     Returns fully resolved records (MR and approval data included).
    /// </summary>
    public async Task<ActivityPollResponse> GetActivitySince(
        GitlabAccessUser currentUser,
        int gitlabUserId,
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Polling for activity for user {UserId} since {Since}", gitlabUserId, since);
        var pushEvents = _gitlabService.StreamPushEventsSince(
            currentUser,
            AsUtcOffset(since),
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
            var activity = await ResolveBranchActivityIn(currentUser, branch);

            results.Add(activity);
        }

        // Update poll timestamp
        _userRepository.UpsertLastPollTimestamp(gitlabUserId, DateTime.UtcNow);

        _logger.LogInformation("Returning {Count} branch activity records from poll", results.Count);
        return new ActivityPollResponse(results, deletedBranches);
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

            var projectName = project.NameWithNamespace;

            var pendingActivity = new BranchActivity(
                branch.BranchName,
                branch.ProjectId,
                projectName,
                null,
                null,
                null,
                branch.LastUpdated,
                branch.MergeGroupId);

            var activity = await ResolveBranchActivityIn(user, pendingActivity);

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

            var branchLookup = await _gitlabService.GetBranchLookupResult(
                user,
                pushEvent.ProjectId,
                pushEvent.BranchName);

            if (branchLookup.IsMissing)
            {
                _logger.LogDebug(
                    "Skipping branch '{BranchName}' in project {ProjectId} - no longer exists",
                    pushEvent.BranchName,
                    pushEvent.ProjectId);

                continue;
            }

            if (branchLookup.IsUnavailable)
            {
                _logger.LogWarning(
                    "Skipping branch '{BranchName}' in project {ProjectId} because branch lookup is unavailable",
                    pushEvent.BranchName,
                    pushEvent.ProjectId);

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

            if (GitlabService.IsScheduledForDeletion(project.NameWithNamespace))
            {
                _logger.LogInformation(
                    "Skipping branch '{BranchName}' in project {ProjectId} ('{ProjectName}'): project/group is scheduled for deletion",
                    pushEvent.BranchName,
                    pushEvent.ProjectId,
                    project.NameWithNamespace);

                continue;
            }

            var projectName = project.NameWithNamespace;

            // Store in database
            var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(
                pushEvent.BranchName,
                pushEvent.ProjectId,
                projectName);

            var mergeGroup = _mergeGroupRepository.GetOrCreateMergeGroup(pushEvent.BranchName);
            _mergeGroupRepository.EnsureBranchInMergeGroup(mergeGroup.Id, branchRecord.Id);
            _mergeGroupRepository.EnsureUserInMergeGroup(gitlabUserId, mergeGroup.Id);

            var lastUpdated = pushEvent.CreatedAt.UtcDateTime;
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
                projectName,
                null,
                null,
                null,
                pushEvent.CreatedAt,
                mergeGroup.Id);
        }
    }

    private static DateTimeOffset AsUtcOffset(DateTime timestamp)
    {
        var utcTimestamp = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };

        return new DateTimeOffset(utcTimestamp);
    }

    /// <summary>
    ///     Resolves a branch's MR and approval status into a fully populated BranchActivity record.
    /// </summary>
    private async Task<BranchActivity> ResolveBranchActivityIn(
        GitlabAccessUser user,
        BranchActivity activity)
    {
        var mergeRequests = await _gitlabService.GetMergeRequests(
            user,
            activity.ProjectId,
            activity.BranchName);

        var hasMr = mergeRequests.Count > 0;
        int? approvalsRequired = null;
        int? approvalsGiven = null;

        if (hasMr)
        {
            _logger.LogDebug(
                "Branch '{BranchName}' has {MrCount} open MR(s) in project {ProjectId}",
                activity.BranchName,
                mergeRequests.Count,
                activity.ProjectId);

            var approval = await _gitlabService.GetMergeRequestApprovals(
                user,
                activity.ProjectId,
                mergeRequests[0].Iid);

            if (approval != null)
            {
                approvalsGiven = approval.ApprovedBy.Count;
                approvalsRequired = Math.Max(approval.ApprovalsRequired ?? 0, 0);
            }
        }

        return activity with
        {
            HasMergeRequest = hasMr,
            ApprovalsRequired = approvalsRequired,
            ApprovalsGiven = approvalsGiven
        };
    }

    /// <summary>
    ///     Removes a branch from the DB and cleans up any empty merge groups.
    /// </summary>
    private void RemoveBranchAndCleanup(int branchInProjectId)
    {
        _mergeGroupRepository.DeleteBranch(branchInProjectId);

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