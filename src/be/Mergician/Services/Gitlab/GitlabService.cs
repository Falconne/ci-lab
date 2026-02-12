using Mergician.Entities;
using Serilog;
using System.Text.Json;

namespace Mergician.Services.Gitlab;

public class GitlabService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public GitlabService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<GitLabUserInfo?> GetCurrentUser(GitlabAccessUserBase accessUser)
    {
        var request = await accessUser.CreateRequest(HttpMethod.Get, "user");
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
        return JsonSerializer.Deserialize<GitLabUserInfo>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public async Task<List<GitLabEvent>> GetUserEvents(GitlabAccessUserBase accessUser, int days = 7)
    {
        var after = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");
        var request = await accessUser.CreateRequest(
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
        return JsonSerializer.Deserialize<List<GitLabEvent>>(json) ?? [];
    }

    public async Task<GitLabProject?> GetProject(GitlabAccessUserBase accessUser, int projectId)
    {
        var request = await accessUser.CreateRequest(
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
        return JsonSerializer.Deserialize<GitLabProject>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}