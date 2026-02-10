using System.Net.Http.Headers;
using System.Text.Json;
using Mergician.Entities;
using Serilog;

namespace Mergician.Services;

public class GitLabOAuthService
{
    private readonly MergicianSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;

    public GitLabOAuthService(MergicianSettings settings, IHttpClientFactory httpClientFactory)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
    }

    public string GetAuthorizationUrl(string redirectUri, string state)
    {
        var gitlabUrl = _settings.GitLab.Url.TrimEnd('/');
        return $"{gitlabUrl}/oauth/authorize" +
               $"?client_id={Uri.EscapeDataString(_settings.GitLab.OAuth.ClientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&response_type=code" +
               $"&state={Uri.EscapeDataString(state)}" +
               $"&scope=read_user+read_api";
    }

    public async Task<GitLabOAuthTokenResponse?> ExchangeCodeForToken(string code, string redirectUri)
    {
        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var gitlabUrl = _settings.GitLab.Url.TrimEnd('/');

        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _settings.GitLab.OAuth.ClientId,
            ["client_secret"] = _settings.GitLab.OAuth.ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        });

        var response = await client.PostAsync($"{gitlabUrl}/oauth/token", requestBody);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            Log.Error("GitLab token exchange failed: {StatusCode} {Body} (redirect_uri={RedirectUri})",
                (int)response.StatusCode, errorBody, redirectUri);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabOAuthTokenResponse>(json);
    }

    public async Task<GitLabOAuthTokenResponse?> RefreshToken(string refreshToken)
    {
        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var gitlabUrl = _settings.GitLab.Url.TrimEnd('/');

        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _settings.GitLab.OAuth.ClientId,
            ["client_secret"] = _settings.GitLab.OAuth.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        });

        var response = await client.PostAsync($"{gitlabUrl}/oauth/token", requestBody);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            Log.Error("GitLab token refresh failed: {StatusCode} {Body}",
                (int)response.StatusCode, errorBody);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabOAuthTokenResponse>(json);
    }

    public async Task<GitLabUserInfo?> GetCurrentUser(string accessToken)
    {
        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var gitlabUrl = _settings.GitLab.Url.TrimEnd('/');

        var request = new HttpRequestMessage(HttpMethod.Get, $"{gitlabUrl}/api/v4/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabUserInfo>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    public async Task<List<GitLabEvent>> GetUserEvents(string accessToken, int days = 7)
    {
        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var gitlabUrl = _settings.GitLab.Url.TrimEnd('/');
        var after = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{gitlabUrl}/api/v4/events?after={after}&per_page=100");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) return [];

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<GitLabEvent>>(json) ?? [];
    }

    public async Task<GitLabProject?> GetProject(string accessToken, int projectId)
    {
        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var gitlabUrl = _settings.GitLab.Url.TrimEnd('/');

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{gitlabUrl}/api/v4/projects/{projectId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabProject>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }
}
