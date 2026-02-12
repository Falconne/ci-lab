using Mergician.Entities;
using Serilog;
using System.Text.Json;

namespace Mergician.Services.Gitlab;

public class GitlabService
{
    private static readonly JsonSerializerOptions CaseInsensitiveOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;

    public GitlabService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Returns true if the branch name is a common default branch name.
    /// </summary>
    public static bool IsPossibleDefaultBranch(string branchName)
    {
        return branchName is "main" or "master" or "develop";
    }

    public async Task<GitLabUserInfo?> GetCurrentUser(GitlabAccessUserBase user)
    {
        var request = await user.CreateRequest(HttpMethod.Get, "user");
        if (request == null)
        {
            Log.Debug("No valid access token available for GetCurrentUser");
            return null;
        }

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("GetCurrentUser failed with status {StatusCode}", (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabUserInfo>(json, CaseInsensitiveOptions);
    }

    public async Task<List<GitLabEvent>> GetUserEvents(GitlabAccessUserBase user, int days = 7)
    {
        var after = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");
        var request = await user.CreateRequest(
            HttpMethod.Get,
            $"events?after={after}&per_page=100");

        if (request == null)
        {
            Log.Debug("No valid access token available for GetUserEvents");
            return [];
        }

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("GetUserEvents failed with status {StatusCode}", (int)response.StatusCode);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<GitLabEvent>>(json, CaseInsensitiveOptions) ?? [];
    }

    public async Task<GitLabProject?> GetProject(GitlabAccessUserBase user, int projectId)
    {
        var request = await user.CreateRequest(
            HttpMethod.Get,
            $"projects/{projectId}");

        if (request == null)
        {
            Log.Debug("No valid access token available for GetProject");
            return null;
        }

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
        return JsonSerializer.Deserialize<GitLabProject>(json, CaseInsensitiveOptions);
    }

    /// <summary>
    /// Checks whether a branch exists in the given project.
    /// </summary>
    public async Task<bool> BranchExists(GitlabAccessUserBase user, int projectId, string branchName)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        var request = await user.CreateRequest(
            HttpMethod.Get,
            $"projects/{projectId}/repository/branches/{encodedBranch}");

        if (request == null)
        {
            Log.Debug("No valid access token available for BranchExists");
            return false;
        }

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);

        if (response.IsSuccessStatusCode)
        {
            Log.Debug("Branch '{BranchName}' exists in project {ProjectId}", branchName, projectId);
            return true;
        }

        Log.Debug("Branch '{BranchName}' does not exist in project {ProjectId} (status {StatusCode})",
            branchName, projectId, (int)response.StatusCode);
        return false;
    }

    /// <summary>
    /// Finds open merge requests for a given source branch in a project.
    /// </summary>
    public async Task<List<GitLabMergeRequest>> GetMergeRequests(
        GitlabAccessUserBase user, int projectId, string sourceBranch)
    {
        var encodedBranch = Uri.EscapeDataString(sourceBranch);
        var request = await user.CreateRequest(
            HttpMethod.Get,
            $"projects/{projectId}/merge_requests?source_branch={encodedBranch}&state=opened");

        if (request == null)
        {
            Log.Debug("No valid access token available for GetMergeRequests");
            return [];
        }

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("GetMergeRequests failed with status {StatusCode} for project {ProjectId}, branch '{BranchName}'",
                (int)response.StatusCode, projectId, sourceBranch);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<GitLabMergeRequest>>(json, CaseInsensitiveOptions) ?? [];
    }

    /// <summary>
    /// Gets the approval state for a merge request.
    /// </summary>
    public async Task<GitLabApprovalState?> GetMergeRequestApprovals(
        GitlabAccessUserBase user, int projectId, int mergeRequestIid)
    {
        var request = await user.CreateRequest(
            HttpMethod.Get,
            $"projects/{projectId}/merge_requests/{mergeRequestIid}/approvals");

        if (request == null)
        {
            Log.Debug("No valid access token available for GetMergeRequestApprovals");
            return null;
        }

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("GetMergeRequestApprovals failed with status {StatusCode}", (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabApprovalState>(json, CaseInsensitiveOptions);
    }
}