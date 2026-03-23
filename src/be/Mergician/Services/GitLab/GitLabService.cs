using Mergician.Entities;
using Mergician.Services.Authentication;
using System.Net;
using System.Runtime.CompilerServices;
using Util;

namespace Mergician.Services.GitLab;

public class GitLabService
{
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

    public async Task<GitLabUserInfo?> GetCurrentUser(
        AccessDetailsBase accessDetails,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _gitLabApiClient.Execute<GitLabUserInfo>(
                () =>
                    accessDetails.CreateRequest(HttpMethod.Get, "user"),
                cancellationToken);
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
            [EnumeratorCancellation] CancellationToken cancellationToken)
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

            List<GitLabPushEvent> pageEvents;
            string? nextPage;

            try
            {
                (pageEvents, nextPage) = await _gitLabApiClient.ExecutePaged<List<GitLabPushEvent>>(
                    () => accessDetails.CreateRequest(
                        HttpMethod.Get,
                        $"events?after={afterDate}&action=pushed&sort=desc&per_page=100&page={page}"),
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

            if (nextPage.IsEmpty())
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

    public async Task<GitLabProject?> GetProject(
        AccessDetailsBase accessDetails,
        int projectId,
        CancellationToken cancellationToken = default)
    {
        if (_projectCache.TryGet(projectId, out var cached))
        {
            _logger.LogDebug("Returning cached project info for project {ProjectId}", projectId);
            return cached;
        }

        GitLabProject? project;

        try
        {
            project = await _gitLabApiClient.Execute<GitLabProject>(
                () =>
                    accessDetails.CreateRequest(HttpMethod.Get, $"projects/{projectId}"),
                cancellationToken);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            if (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation(
                    "GetProject({ProjectId}) returned 404; project may have been deleted",
                    projectId);

                return null;
            }

            _logger.LogError(
                "GetProject({ProjectId}) failed with unexpected status {StatusCode}",
                projectId,
                (int)ex.StatusCode);

            throw;
        }

        if (project.Name.IsEmpty() || project.NameWithNamespace.IsEmpty())
        {
            _logger.LogError(
                "GetProject({ProjectId}) returned a project with missing Name or NameWithNamespace; skipping",
                projectId);

            return null;
        }

        _projectCache.Set(projectId, project);
        _logger.LogDebug("Cached project info for project {ProjectId}", projectId);
        return project;
    }

    /// <summary>
    ///     Fetches full branch details including the latest commit information.
    ///     Returns null when the branch does not exist or the request fails.
    /// </summary>
    public async Task<GitLabBranchDetails?> GetBranchDetails(
        AccessDetailsBase accessDetails,
        int projectId,
        string branchName,
        CancellationToken cancellationToken = default)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);

        try
        {
            return await _gitLabApiClient.Execute<GitLabBranchDetails>(
                () => accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/repository/branches/{encodedBranch}"),
                cancellationToken);
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
        string branchName,
        CancellationToken cancellationToken = default)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);

        try
        {
            await _gitLabApiClient.Execute<GitLabBranchDetails>(
                () => accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/repository/branches/{encodedBranch}"),
                cancellationToken);

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
        string sourceBranch,
        CancellationToken cancellationToken = default)
    {
        var encodedBranch = Uri.EscapeDataString(sourceBranch);

        try
        {
            return await _gitLabApiClient.Execute<List<GitLabMergeRequest>>(
                () =>
                    accessDetails.CreateRequest(
                        HttpMethod.Get,
                        $"projects/{projectId}/merge_requests?source_branch={encodedBranch}&state=opened"),
                cancellationToken);
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
        int mergeRequestIid,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _gitLabApiClient.Execute<GitLabApprovalState>(
                () => accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/merge_requests/{mergeRequestIid}/approvals"),
                cancellationToken);
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

    /// <summary>
    ///     Looks up a GitLab project by its URL-encoded path (e.g. "group/subgroup/project").
    ///     Returns null if the project is not found or the request fails.
    /// </summary>
    public async Task<GitLabProject?> GetProjectByPath(
        AccessDetailsBase accessDetails,
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var encodedPath = Uri.EscapeDataString(projectPath);

        try
        {
            var project = await _gitLabApiClient.Execute<GitLabProject>(
                () =>
                    accessDetails.CreateRequest(HttpMethod.Get, $"projects/{encodedPath}"),
                cancellationToken);

            if (project.Name.IsEmpty() || project.NameWithNamespace.IsEmpty())
            {
                _logger.LogError(
                    "GetProjectByPath('{ProjectPath}') returned a project with missing Name or NameWithNamespace",
                    projectPath);

                return null;
            }

            _projectCache.Set(project.Id, project);
            _logger.LogDebug(
                "Resolved project path '{ProjectPath}' to project {ProjectId}",
                projectPath,
                project.Id);

            return project;
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            if (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("GetProjectByPath('{ProjectPath}') returned 404", projectPath);
                return null;
            }

            _logger.LogError(
                "GetProjectByPath('{ProjectPath}') failed with status {StatusCode}",
                projectPath,
                (int)ex.StatusCode);

            throw;
        }
    }

    /// <summary>
    ///     Fetches merge requests by IID in a project. Returns all states (opened, merged, closed).
    ///     Used by MR URL lookup to find the source branch regardless of MR state.
    /// </summary>
    public async Task<List<GitLabMergeRequest>> GetMergeRequestsByIid(
        AccessDetailsBase accessDetails,
        int projectId,
        int mrIid,
        CancellationToken cancellationToken = default)
    {
        // TODO: Only return open merge requests.
        try
        {
            return await _gitLabApiClient.Execute<List<GitLabMergeRequest>>(
                () =>
                    accessDetails.CreateRequest(
                        HttpMethod.Get,
                        $"projects/{projectId}/merge_requests?iids[]={mrIid}"),
                cancellationToken);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError(
                "GetMergeRequestsByIid failed with status {StatusCode} for project {ProjectId}, MR IID {MrIid}",
                (int)ex.StatusCode,
                projectId,
                mrIid);

            return [];
        }
    }
}