using System.Text.Json;
using Mergician.Entities;

namespace Mergician.Services.Authentication;

public class GitLabOAuthService
{
    private readonly IHttpClientFactory _httpClientFactory;

    private readonly ILogger<GitLabOAuthService> _logger;

    private readonly MergicianSettings _settings;

    public GitLabOAuthService(
        MergicianSettings settings,
        IHttpClientFactory httpClientFactory,
        ILogger<GitLabOAuthService> logger)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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
        var gitlabUrl = _settings.GitLab.ServerUrl.TrimEnd('/');

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
            _logger.LogError("GitLab token exchange failed: {StatusCode} {Body} (redirect_uri={RedirectUri})",
                (int)response.StatusCode, errorBody, redirectUri);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabOAuthTokenResponse>(json);
    }

    public async Task<GitLabOAuthTokenResponse?> RefreshToken(string refreshToken)
    {
        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var gitlabUrl = _settings.GitLab.ServerUrl.TrimEnd('/');

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
            _logger.LogError("GitLab token refresh failed: {StatusCode} {Body}",
                (int)response.StatusCode, errorBody);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabOAuthTokenResponse>(json);
    }
}
