using Mergician.Entities;
using Mergician.Services.Authentication;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Mergician.Services.Gitlab;

public class GitlabService
{
    private static readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly ILogger<GitlabService> _logger;

    private readonly CacheService<int, GitLabProject> _projectCache;

    public GitlabService(
        IHttpClientFactory httpClientFactory,
        CacheService<int, GitLabProject> projectCache,
        ILogger<GitlabService> logger)
    {
        _httpClientFactory = httpClientFactory;
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

    public async Task<GitLabUserInfo?> GetCurrentUser(GitlabAccessUser user)
    {
        var request = user.CreateRequest(HttpMethod.Get, "user");

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GetCurrentUser failed with status {StatusCode}", (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabUserInfo>(json, _jsonOptions);
    }

    /// <summary>
    ///     Streams unique branch push events at or after the given timestamp.
    ///     Uses date-level filtering in the GitLab API call and applies timestamp-level
    ///     filtering in-process to preserve sub-day precision.
    /// </summary>
    public async IAsyncEnumerable<(string BranchName, int ProjectId, DateTimeOffset CreatedAt)>
        StreamPushEventsSince(
            GitlabAccessUser user,
            DateTimeOffset since,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sinceUtc = since.ToUniversalTime();

        // GitLab events API 'after' is date-only, so query from the previous day
        // and enforce timestamp-level filtering locally.
        var afterDate = sinceUtc.AddDays(-1).ToString("yyyy-MM-dd");

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var page = 1;
        var yieldedCount = 0;
        var emittedBranchProjects = new HashSet<string>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = user.CreateRequest(
                HttpMethod.Get,
                $"events?after={afterDate}&action=pushed&sort=desc&per_page=100&page={page}");

            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "StreamPushEventsSince failed on page {Page} with status {StatusCode}",
                    page,
                    (int)response.StatusCode);

                yield break;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var pageEvents = JsonSerializer.Deserialize<List<GitLabPushEvent>>(json, _jsonOptions) ?? [];

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

            if (!response.Headers.TryGetValues("X-Next-Page", out var nextPageValues))
            {
                break;
            }

            var nextPage = nextPageValues.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(nextPage))
            {
                break;
            }

            if (int.TryParse(nextPage, out page) && page > 0)
            {
                continue;
            }

            _logger.LogWarning(
                "Unexpected X-Next-Page header value '{NextPage}' when fetching GitLab push events",
                nextPage);

            break;
        }

        _logger.LogInformation(
            "Streamed {Count} unique branch push events since {Since}",
            yieldedCount,
            sinceUtc);
    }

    public async Task<GitLabProject?> GetProject(GitlabAccessUser user, int projectId)
    {
        if (_projectCache.TryGet(projectId, out var cached))
        {
            _logger.LogDebug("Returning cached project info for project {ProjectId}", projectId);
            return cached;
        }

        var request = user.CreateRequest(
            HttpMethod.Get,
            $"projects/{projectId}");

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "GetProject({ProjectId}) failed with status {StatusCode}",
                projectId,
                (int)response.StatusCode);

            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var project = JsonSerializer.Deserialize<GitLabProject>(json, _jsonOptions);

        if (project != null)
        {
            _projectCache.Set(projectId, project);
            _logger.LogDebug("Cached project info for project {ProjectId}", projectId);
        }

        return project;
    }

    /// <summary>
    ///     Checks branch lookup status in the given project.
    ///     Returns Missing only for 404 responses; all other failures are Unavailable.
    /// </summary>
    public async Task<GitLabBranchLookupResult> GetBranchLookupResult(
        GitlabAccessUser user,
        int projectId,
        string branchName)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        var request = user.CreateRequest(
            HttpMethod.Get,
            $"projects/{projectId}/repository/branches/{encodedBranch}");

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Branch lookup failed for branch '{BranchName}' in project {ProjectId} due to request error",
                branchName,
                projectId);

            return new GitLabBranchLookupResult(
                GitLabBranchLookupStatus.Unavailable,
                null,
                ex.Message);
        }

        if (response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Branch '{BranchName}' exists in project {ProjectId}", branchName, projectId);
            return new GitLabBranchLookupResult(GitLabBranchLookupStatus.Exists, (int)response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug(
                "Branch '{BranchName}' does not exist in project {ProjectId} (status {StatusCode})",
                branchName,
                projectId,
                (int)response.StatusCode);

            return new GitLabBranchLookupResult(GitLabBranchLookupStatus.Missing, (int)response.StatusCode);
        }

        _logger.LogWarning(
            "Branch lookup unavailable for '{BranchName}' in project {ProjectId} (status {StatusCode})",
            branchName,
            projectId,
            (int)response.StatusCode);

        return new GitLabBranchLookupResult(
            GitLabBranchLookupStatus.Unavailable,
            (int)response.StatusCode);
    }

    /// <summary>
    ///     Finds open merge requests for a given source branch in a project.
    /// </summary>
    public async Task<List<GitLabMergeRequest>> GetMergeRequests(
        GitlabAccessUser user,
        int projectId,
        string sourceBranch)
    {
        var encodedBranch = Uri.EscapeDataString(sourceBranch);
        var request = user.CreateRequest(
            HttpMethod.Get,
            $"projects/{projectId}/merge_requests?source_branch={encodedBranch}&state=opened");

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "GetMergeRequests failed with status {StatusCode} for project {ProjectId}, branch '{BranchName}'",
                (int)response.StatusCode,
                projectId,
                sourceBranch);

            return [];
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<GitLabMergeRequest>>(json, _jsonOptions) ?? [];
    }

    /// <summary>
    ///     Gets the approval state for a merge request.
    /// </summary>
    public async Task<GitLabApprovalState?> GetMergeRequestApprovals(
        GitlabAccessUser user,
        int projectId,
        int mergeRequestIid)
    {
        var request = user.CreateRequest(
            HttpMethod.Get,
            $"projects/{projectId}/merge_requests/{mergeRequestIid}/approvals");

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation(
                    "GetMergeRequestApprovals is not available for project {ProjectId}, MR {MergeRequestIid} (status {StatusCode}); assuming 0 approvals required",
                    projectId,
                    mergeRequestIid,
                    (int)response.StatusCode);

                return new GitLabApprovalState { ApprovalsRequired = 0, ApprovedBy = [] };
            }

            _logger.LogWarning(
                "GetMergeRequestApprovals failed with status {StatusCode}",
                (int)response.StatusCode);

            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabApprovalState>(json, _jsonOptions);
    }
}