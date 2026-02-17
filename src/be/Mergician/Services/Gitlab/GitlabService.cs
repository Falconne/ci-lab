using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services;
using Serilog;
using System.Text.Json;

namespace Mergician.Services.Gitlab;

public class GitlabService
{
    private static readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CacheService<int, GitLabProject> _projectCache;

    public GitlabService(IHttpClientFactory httpClientFactory, CacheService<int, GitLabProject> projectCache)
    {
        _httpClientFactory = httpClientFactory;
        _projectCache = projectCache;
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
            Log.Warning("GetCurrentUser failed with status {StatusCode}", (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabUserInfo>(json, _jsonOptions);
    }

    /// <summary>
    ///     Fetches user events created at or after the given timestamp.
    ///     Uses the date portion of <paramref name="since" /> for the GitLab API query
    ///     (which only supports date-level granularity), then filters results
    ///     to only include events with CreatedAt >= <paramref name="since" />.
    /// </summary>
    public async Task<List<GitLabEvent>> GetUserEventsSince(GitlabAccessUser user, DateTime since)
    {
        // GitLab events API 'after' param is date-only, so use the day before to avoid
        // missing events near midnight boundaries
        var afterDate = since.AddDays(-1).ToString("yyyy-MM-dd");
        var events = await FetchEvents(user, afterDate);

        var filtered = events.Where(e => e.CreatedAt >= since).ToList();
        Log.Debug(
            "Filtered {Total} events to {Filtered} events since {Since}",
            events.Count,
            filtered.Count,
            since);

        return filtered;
    }

    private async Task<List<GitLabEvent>> FetchEvents(GitlabAccessUser user, string afterDate)
    {
        var request = user.CreateRequest(
            HttpMethod.Get,
            $"events?after={afterDate}&per_page=100");

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("FetchEvents failed with status {StatusCode}", (int)response.StatusCode);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<GitLabEvent>>(json, _jsonOptions) ?? [];
    }

    public async Task<GitLabProject?> GetProject(GitlabAccessUser user, int projectId)
    {
        if (_projectCache.TryGet(projectId, out var cached))
        {
            Log.Debug("Returning cached project info for project {ProjectId}", projectId);
            return cached;
        }

        var request = user.CreateRequest(
            HttpMethod.Get,
            $"projects/{projectId}");

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning(
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
            Log.Debug("Cached project info for project {ProjectId}", projectId);
        }

        return project;
    }

    /// <summary>
    ///     Checks whether a branch exists in the given project.
    /// </summary>
    public async Task<bool> BranchExists(GitlabAccessUser user, int projectId, string branchName)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        var request = user.CreateRequest(
            HttpMethod.Get,
            $"projects/{projectId}/repository/branches/{encodedBranch}");

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);

        if (response.IsSuccessStatusCode)
        {
            Log.Debug("Branch '{BranchName}' exists in project {ProjectId}", branchName, projectId);
            return true;
        }

        Log.Debug(
            "Branch '{BranchName}' does not exist in project {ProjectId} (status {StatusCode})",
            branchName,
            projectId,
            (int)response.StatusCode);

        return false;
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
            Log.Warning(
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
            Log.Warning("GetMergeRequestApprovals failed with status {StatusCode}", (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabApprovalState>(json, _jsonOptions);
    }
}