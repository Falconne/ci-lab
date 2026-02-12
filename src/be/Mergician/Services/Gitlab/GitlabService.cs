using Mergician.Entities;
using Serilog;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Mergician.Services.Gitlab;

public class GitlabService
{
    private readonly IHttpClientFactory _httpClientFactory;

    private readonly MergicianSettings _settings;

    public GitlabService(MergicianSettings settings, IHttpClientFactory httpClientFactory)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<GitLabUserInfo?> GetCurrentUser(IGitlabAccessUser accessUser)
    {
        var accessToken = await accessUser.GetValidAccessToken();
        if (accessToken == null)
        {
            Log.Debug("No valid access token available for GetCurrentUser");
            return null;
        }

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var gitlabUrl = _settings.GitLab.ServerUrl.TrimEnd('/');

        var request = new HttpRequestMessage(HttpMethod.Get, $"{gitlabUrl}/api/v4/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

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

    public async Task<List<GitLabEvent>> GetUserEvents(IGitlabAccessUser accessUser, int days = 7)
    {
        var accessToken = await accessUser.GetValidAccessToken();
        if (accessToken == null)
        {
            Log.Debug("No valid access token available for GetUserEvents");
            return [];
        }

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var gitlabUrl = _settings.GitLab.ServerUrl.TrimEnd('/');
        var after = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{gitlabUrl}/api/v4/events?after={after}&per_page=100");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("GetUserEvents failed with status {StatusCode}", (int)response.StatusCode);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<GitLabEvent>>(json) ?? [];
    }

    public async Task<GitLabProject?> GetProject(IGitlabAccessUser accessUser, int projectId)
    {
        var accessToken = await accessUser.GetValidAccessToken();
        if (accessToken == null)
        {
            Log.Debug("No valid access token available for GetProject");
            return null;
        }

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var gitlabUrl = _settings.GitLab.ServerUrl.TrimEnd('/');

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{gitlabUrl}/api/v4/projects/{projectId}");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

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