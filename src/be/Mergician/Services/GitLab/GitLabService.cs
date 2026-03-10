using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Utilities;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Mergician.Services.GitLab;

public class GitLabService
{
    private static readonly JsonSerializerOptions _jsonOptions = JsonOptions.CaseInsensitive;

    private readonly GitLabApiClient _gitLabApiClient;

    private readonly ILogger<GitLabService> _logger;

    private readonly CacheService<int, GitLabProject> _projectCache;

    public GitLabService(
        GitLabApiClient gitLabApiClient,
        CacheService<int, GitLabProject> projectCache,
        ILogger<GitLabService> logger)
    {
        _gitLabApiClient = gitLabApiClient;
        _projectCache = projectCache;
        _logger = logger;
    }

    /// <summary>
    ///     Returns true if the branch name is a common default branch name.
    /// </summary>
    public static bool IsPossibleDefaultBranch(string branchName)
    {
        return branchName is "main" or "master" or "develop";
    }

    /// <summary>
    ///     Returns true if the project or group name indicates it is scheduled for deletion.
    ///     GitLab renames groups and their projects to include "deletion_scheduled" in the
    ///     namespace during its asynchronous deletion process.
    /// </summary>
    public static bool IsScheduledForDeletion(string nameWithNamespace)
    {
        return nameWithNamespace.Contains("deletion_scheduled", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<GitLabUserInfo?> GetCurrentUser(AccessDetailsBase accessDetails)
    {
        try
        {
            return await _gitLabApiClient.ExecuteAsync<GitLabUserInfo>(
                () => accessDetails.CreateRequest(HttpMethod.Get, "user"),
                _jsonOptions,
                "GetCurrentUser",
                GitLabApiFailureBehavior.EnterStartupMode);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError("GetCurrentUser failed with status {StatusCode}", (int)ex.StatusCode);
            return null;
        }
    }

    /// <summary>
    ///     Streams unique branch push events at or after the given timestamp.
    ///     Uses date-level filtering in the GitLab API call and applies timestamp-level
    ///     filtering in-process to preserve sub-day precision.
    /// </summary>
    public async IAsyncEnumerable<(string BranchName, int ProjectId, DateTimeOffset CreatedAt)>
        GetPushEventsForUserSince(
            AccessDetailsBase accessDetails,
            DateTimeOffset since,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sinceUtc = since.ToUniversalTime();

        // GitLab events API 'after' is date-only and interprets the date in the server's
        // configured timezone. Convert our UTC timestamp to GitLab's local time, then
        // query from the previous day to avoid missing events near the date boundary.
        var sinceInGitLabLocal = _gitLabApiClient.AdjustToGitLabLocal(sinceUtc);
        var afterDate = sinceInGitLabLocal.AddDays(-1).ToString("yyyy-MM-dd");

        _logger.LogDebug(
            "GetPushEventsForUserSince: sinceUtc={SinceUtc}, gitLabLocal={GitLabLocal}, afterDate={AfterDate}, gitLabOffset={Offset}",
            sinceUtc,
            sinceInGitLabLocal,
            afterDate,
            _gitLabApiClient.GitLabUtcOffset);

        var page = 1;
        var yieldedCount = 0;
        var emittedBranchProjects = new HashSet<string>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var operationName = $"GetPushEventsForUserSince(page={page})";
            List<GitLabPushEvent> pageEvents;
            string? nextPage;

            try
            {
                (pageEvents, nextPage) = await _gitLabApiClient.ExecutePagedAsync<List<GitLabPushEvent>>(
                    () => accessDetails.CreateRequest(
                        HttpMethod.Get,
                        $"events?after={afterDate}&action=pushed&sort=desc&per_page=100&page={page}"),
                    _jsonOptions,
                    operationName,
                    GitLabApiFailureBehavior.Throw,
                    cancellationToken);
            }
            catch (GitLabUnexpectedResponseException ex)
            {
                _logger.LogError(
                    "GetPushEventsForUserSince failed on page {Page} with status {StatusCode}",
                    page,
                    (int)ex.StatusCode);

                yield break;
            }

            _logger.LogDebug(
                "Fetched {Count} GitLab push events from page {Page} (after={AfterDate})",
                pageEvents.Count,
                page,
                afterDate);

            foreach (var pushEvent in pageEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (pushEvent.CreatedAt < sinceUtc)
                {
                    continue;
                }

                if (pushEvent.PushData is not { RefType: "branch", Ref: not null })
                {
                    continue;
                }

                var key = $"{pushEvent.PushData.Ref}:{pushEvent.ProjectId}";
                if (!emittedBranchProjects.Add(key))
                {
                    continue;
                }

                yieldedCount++;
                yield return (pushEvent.PushData.Ref, pushEvent.ProjectId, pushEvent.CreatedAt);
            }

            if (string.IsNullOrWhiteSpace(nextPage))
            {
                break;
            }

            if (int.TryParse(nextPage, out page) && page > 0)
            {
                continue;
            }

            _logger.LogError(
                "Unexpected X-Next-Page header value '{NextPage}' when fetching GitLab push events",
                nextPage);

            break;
        }

        _logger.LogInformation(
            "Streamed {Count} unique branch push events since {Since}",
            yieldedCount,
            sinceUtc);
    }

    public async Task<GitLabProject?> GetProject(AccessDetailsBase accessDetails, int projectId)
    {
        if (_projectCache.TryGet(projectId, out var cached))
        {
            _logger.LogDebug("Returning cached project info for project {ProjectId}", projectId);
            return cached;
        }

        var operationName = $"GetProject({projectId})";
        GitLabProject? project;

        try
        {
            project = await _gitLabApiClient.ExecuteAsync<GitLabProject>(
                () => accessDetails.CreateRequest(HttpMethod.Get, $"projects/{projectId}"),
                _jsonOptions,
                operationName,
                GitLabApiFailureBehavior.Throw);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError(
                "GetProject({ProjectId}) failed with status {StatusCode}",
                projectId,
                (int)ex.StatusCode);

            return null;
        }

        if (project != null)
        {
            if (IsScheduledForDeletion(project.NameWithNamespace))
            {
                _logger.LogDebug(
                    "Not caching project {ProjectId} ('{NameWithNamespace}'): scheduled for deletion",
                    projectId,
                    project.NameWithNamespace);
            }
            else
            {
                _projectCache.Set(projectId, project);
                _logger.LogDebug("Cached project info for project {ProjectId}", projectId);
            }
        }

        return project;
    }

    /// <summary>
    ///     Fetches full branch details including the latest commit information.
    ///     Returns null when the branch does not exist or the request fails.
    /// </summary>
    public async Task<GitLabBranchDetails?> GetBranchDetails(
        AccessDetailsBase accessDetails,
        int projectId,
        string branchName)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        var operationName = $"GetBranchDetails(projectId={projectId}, branchName={branchName})";

        try
        {
            return await _gitLabApiClient.ExecuteAsync<GitLabBranchDetails>(
                () => accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/repository/branches/{encodedBranch}"),
                _jsonOptions,
                operationName,
                GitLabApiFailureBehavior.Throw);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            if (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug(
                    "GetBranchDetails for '{BranchName}' in project {ProjectId} returned {StatusCode}",
                    branchName,
                    projectId,
                    (int)ex.StatusCode);

                return null;
            }

            _logger.LogError(
                "GetBranchDetails for '{BranchName}' in project {ProjectId} returned {StatusCode}",
                branchName,
                projectId,
                (int)ex.StatusCode);

            return null;
        }
    }

    /// <summary>
    ///     Checks branch lookup status in the given project.
    ///     Returns Missing only for 404 responses; all other failures are Unavailable.
    /// </summary>
    public async Task<GitLabBranchLookupResult> GetBranchLookupResult(
        AccessDetailsBase accessDetails,
        int projectId,
        string branchName)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        var operationName = $"GetBranchLookupResult(projectId={projectId}, branchName={branchName})";

        try
        {
            await _gitLabApiClient.ExecuteAsync<GitLabBranchDetails>(
                () => accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/repository/branches/{encodedBranch}"),
                _jsonOptions,
                operationName,
                GitLabApiFailureBehavior.Throw);

            _logger.LogDebug("Branch '{BranchName}' exists in project {ProjectId}", branchName, projectId);
            return new GitLabBranchLookupResult(GitLabBranchLookupStatus.Exists, 200);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            if (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug(
                    "Branch '{BranchName}' does not exist in project {ProjectId} (status {StatusCode})",
                    branchName,
                    projectId,
                    (int)ex.StatusCode);

                return new GitLabBranchLookupResult(GitLabBranchLookupStatus.Missing, (int)ex.StatusCode);
            }

            _logger.LogError(
                "Branch lookup unavailable for '{BranchName}' in project {ProjectId} (status {StatusCode})",
                branchName,
                projectId,
                (int)ex.StatusCode);

            return new GitLabBranchLookupResult(GitLabBranchLookupStatus.Unavailable, (int)ex.StatusCode);
        }
    }

    /// <summary>
    ///     Finds open merge requests for a given source branch in a project.
    /// </summary>
    public async Task<List<GitLabMergeRequest>> GetMergeRequests(
        AccessDetailsBase accessDetails,
        int projectId,
        string sourceBranch)
    {
        var encodedBranch = Uri.EscapeDataString(sourceBranch);
        var operationName = $"GetMergeRequests(projectId={projectId}, sourceBranch={sourceBranch})";

        try
        {
            return await _gitLabApiClient.ExecuteAsync<List<GitLabMergeRequest>>(
                () => accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/merge_requests?source_branch={encodedBranch}&state=opened"),
                _jsonOptions,
                operationName,
                GitLabApiFailureBehavior.Throw);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError(
                "GetMergeRequests failed with status {StatusCode} for project {ProjectId}, branch '{BranchName}'",
                (int)ex.StatusCode,
                projectId,
                sourceBranch);

            return [];
        }
    }

    /// <summary>
    ///     Gets the approval state for a merge request.
    /// </summary>
    public async Task<GitLabApprovalState?> GetMergeRequestApprovals(
        AccessDetailsBase accessDetails,
        int projectId,
        int mergeRequestIid)
    {
        var operationName =
            $"GetMergeRequestApprovals(projectId={projectId}, mergeRequestIid={mergeRequestIid})";

        try
        {
            return await _gitLabApiClient.ExecuteAsync<GitLabApprovalState>(
                () => accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/merge_requests/{mergeRequestIid}/approvals"),
                _jsonOptions,
                operationName,
                GitLabApiFailureBehavior.Throw);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            if (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation(
                    "GetMergeRequestApprovals is not available for project {ProjectId}, MR {MergeRequestIid} (status {StatusCode}); assuming 0 approvals required",
                    projectId,
                    mergeRequestIid,
                    (int)ex.StatusCode);

                return new GitLabApprovalState { ApprovalsRequired = 0, ApprovedBy = [] };
            }

            _logger.LogError(
                "GetMergeRequestApprovals failed with status {StatusCode}",
                (int)ex.StatusCode);

            return null;
        }
    }
}