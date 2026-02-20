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

        var returnedKeys = new HashSet<string>();

        foreach (var cached in cachedBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check if branch still exists
            var exists = await _gitlabService.BranchExists(currentUser, cached.ProjectId, cached.BranchName);
            if (!exists)
            {
                _logger.LogInformation(
                    "Cached branch '{BranchName}' in project {ProjectId} no longer exists, removing from DB",
                    cached.BranchName,
                    cached.ProjectId);

                RemoveBranchAndCleanup(cached.BranchInProjectId);
                continue;
            }

            var key = $"{cached.BranchName}:{cached.ProjectId}";
            returnedKeys.Add(key);

            // Yield initial record with unknown MR status
            yield return new BranchActivity(
                cached.BranchName,
                cached.ProjectId,
                cached.ProjectName,
                null,
                null,
                null,
                new DateTimeOffset(cached.LastUpdateTime, TimeSpan.Zero),
                cached.MergeGroupId);

            // Yield resolved MR/approval data
            yield return await ResolveBranchActivity(
                currentUser,
                cached.BranchName,
                cached.ProjectId,
                cached.ProjectName,
                new DateTimeOffset(cached.LastUpdateTime, TimeSpan.Zero),
                cached.MergeGroupId);
        }

        // 2. Fetch new data from GitLab
        var lastPoll = _userRepository.GetLastPollTimestamp(gitlabUserId);
        // TODO if `lastPoll` is older than `since`, use `since`.
        var fetchSince = lastPoll ?? since;

        _logger.LogInformation(
            "Fetching GitLab events for user {UserId} since {Since}",
            gitlabUserId,
            fetchSince);

        var events = await _gitlabService.GetUserEventsSince(currentUser, fetchSince);

        var records = FetchAndStoreBranchActivityRecords(
            currentUser,
            gitlabUserId,
            events,
            returnedKeys,
            cancellationToken);

        await foreach (var branch in records)
        {
            // Yield skeleton record to the UI immediately.
            yield return branch;

            // Resolve MR/approval data
            yield return await ResolveBranchActivity(
                currentUser,
                branch.BranchName,
                branch.ProjectId,
                branch.ProjectName,
                branch.LastUpdated,
                branch.MergeGroupId);
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
        var events = await _gitlabService.GetUserEventsSince(currentUser, since);

        var results = new List<BranchActivity>();
        var deletedBranches = new List<BranchDeletedNotification>();
        var existingKeys = new HashSet<string>();

        var records = FetchAndStoreBranchActivityRecords(
            currentUser,
            gitlabUserId,
            events,
            existingKeys,
            cancellationToken);

        await foreach (var branch in records)
        {
            var activity = await ResolveBranchActivity(
                currentUser,
                branch.BranchName,
                branch.ProjectId,
                branch.ProjectName,
                branch.LastUpdated,
                branch.MergeGroupId);

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
            var exists = await _gitlabService.BranchExists(user, branch.ProjectId, branch.BranchName);
            if (!exists)
            {
                _logger.LogInformation(
                    "Branch '{BranchName}' in project {ProjectId} no longer exists during refresh",
                    branch.BranchName,
                    branch.ProjectId);

                // Find and remove from DB
                var allBranches = _mergeGroupRepository.GetAllBranches();
                var branchRecord = allBranches.FirstOrDefault(b =>
                    b.BranchName == branch.BranchName && b.ProjectId == branch.ProjectId);

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

            var project = await _gitlabService.GetProject(user, branch.ProjectId);
            // TODO if project is null, return a 5XX error to the frontend immediately. Remove the
            // null handling below.
            var projectName = project?.NameWithNamespace ?? $"Project #{branch.ProjectId}";

            var activity = await ResolveBranchActivity(
                user,
                branch.BranchName,
                branch.ProjectId,
                projectName,
                branch.LastUpdated,
                branch.MergeGroupId);

            yield return activity;
        }

        _logger.LogInformation("Finished streaming refresh for {Count} branch-project pairs", branches.Count);
    }

    /// <summary>
    ///     Discovers branches from events, stores them in the DB, and yields BranchActivity records
    ///     for branches not already in the returnedKeys set. Updates the set as discoveries are made.
    /// </summary>
    private async IAsyncEnumerable<BranchActivity> FetchAndStoreBranchActivityRecords(
        GitlabAccessUser user,
        int gitlabUserId,
        List<GitLabEvent> events,
        HashSet<string> returnedKeys,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var activeBranches = ExtractBranchesFromActivity(events);
        _logger.LogInformation(
            "Found {Count} unique branch/project combinations from events",
            activeBranches.Count);

        foreach (var entry in activeBranches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = $"{entry.BranchName}:{entry.ProjectId}";

            var exists = await _gitlabService.BranchExists(user, entry.ProjectId, entry.BranchName);
            if (!exists)
            {
                _logger.LogDebug(
                    "Skipping branch '{BranchName}' in project {ProjectId} - no longer exists",
                    entry.BranchName,
                    entry.ProjectId);

                continue;
            }

            var project = await _gitlabService.GetProject(user, entry.ProjectId);
            // TODO if project is null, return a 5XX error to the frontend immediately. Remove the
            // null handling below.
            var projectName = project?.NameWithNamespace ?? $"Project #{entry.ProjectId}";

            // Store in database
            var branchRecord = _mergeGroupRepository.GetOrCreateBranchRecord(
                entry.BranchName,
                entry.ProjectId,
                projectName);

            var mergeGroup = _mergeGroupRepository.GetOrCreateMergeGroup(entry.BranchName);
            _mergeGroupRepository.EnsureBranchInMergeGroup(mergeGroup.Id, branchRecord.Id);
            _mergeGroupRepository.EnsureUserInMergeGroup(gitlabUserId, mergeGroup.Id);

            var lastUpdated = entry.LastUpdated.UtcDateTime;
            if (lastUpdated > mergeGroup.LastUpdateTime)
            {
                _mergeGroupRepository.UpdateMergeGroupTimestamp(mergeGroup.Id, lastUpdated);
            }

            if (!returnedKeys.Add(key))
            {
                _logger.LogDebug(
                    "Branch '{BranchName}' in project {ProjectId} already returned, updating DB only",
                    entry.BranchName,
                    entry.ProjectId);

                continue;
            }

            yield return new BranchActivity(
                entry.BranchName,
                entry.ProjectId,
                projectName,
                null,
                null,
                null,
                entry.LastUpdated,
                mergeGroup.Id);
        }
    }

    /// <summary>
    ///     Extracts distinct branch-project pairs from push events, excluding default branches.
    ///     Returns the latest push timestamp for each branch.
    /// </summary>
    private static List<(string BranchName, int ProjectId, DateTimeOffset LastUpdated)>
        ExtractBranchesFromActivity(
            List<GitLabEvent> events)
    {
        return events
            .Where(e => e.PushData is { RefType: "branch", Ref: not null })
            .Where(e => !GitlabService.IsPossibleDefaultBranch(e.PushData!.Ref!))
            .GroupBy(e => (BranchName: e.PushData!.Ref!, e.ProjectId))
            .Select(g => (g.Key.BranchName, g.Key.ProjectId,
                LastUpdated: (DateTimeOffset)g.Max(e => e.CreatedAt)))
            .ToList();
    }

    /// <summary>
    ///     Resolves a branch's MR and approval status into a fully populated BranchActivity record.
    /// </summary>
    private async Task<BranchActivity> ResolveBranchActivity(
        GitlabAccessUser user,
        string branchName,
        int projectId,
        string projectName,
        DateTimeOffset? lastUpdated,
        int? mergeGroupId)
    {
        // TODO Rename this method to `ResolveBranchActivityIn` and make it take in an actual `BranchActivity` record instead of individual parameters.
        // Change `BranchActivity` into a class so it can be mutated in place and returned here. 
        var mergeRequests = await _gitlabService.GetMergeRequests(user, projectId, branchName);

        var hasMr = mergeRequests.Count > 0;
        int? approvalsRequired = null;
        int? approvalsGiven = null;

        if (hasMr)
        {
            _logger.LogDebug(
                "Branch '{BranchName}' has {MrCount} open MR(s) in project {ProjectId}",
                branchName,
                mergeRequests.Count,
                projectId);

            var approval = await _gitlabService.GetMergeRequestApprovals(
                user,
                projectId,
                mergeRequests[0].Iid);

            if (approval != null)
            {
                approvalsGiven = approval.ApprovedBy.Count;
                // TODO get the actual approvals required from the MR data via appropriate Gitlab API usage. Note that the free tier of
                // Gitlab we use for testing with CI Lab does not support "approvals required", but the Premium tier we will use in production
                // does, so ensure the check handles this. If no approvals are needed or we are in free tier mode, set `approvalsRequired` to 0.
                approvalsRequired = approvalsGiven > 0 ? approvalsGiven : null;
            }
        }

        return new BranchActivity(
            branchName,
            projectId,
            projectName,
            hasMr,
            approvalsRequired,
            approvalsGiven,
            lastUpdated,
            mergeGroupId);
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