using Mergician.Entities;
using Mergician.Services.GitLab;
using Mergician.Utilities;
using System.Text.Json;

namespace Mergician.Services.Authentication;

public class GitLabOAuthService
{
    private static readonly JsonSerializerOptions _jsonOptions = JsonOptions.CaseInsensitive;

    private readonly GitLabApiClient _gitLabApiClient;

    private readonly ILogger<GitLabOAuthService> _logger;

    private readonly MergicianSettings _settings;

    public GitLabOAuthService(
        MergicianSettings settings,
        GitLabApiClient gitLabApiClient,
        ILogger<GitLabOAuthService> logger)
    {
        _settings = settings;
        _gitLabApiClient = gitLabApiClient;
        _logger = logger;
    }

    public string GetAuthorizationUrl(string redirectUri, string state)
    {
        var gitlabUrl = _settings.GitLab.Url.TrimEnd('/');
        return $"{gitlabUrl}/oauth/authorize"
               + $"?client_id={Uri.EscapeDataString(_settings.GitLab.OAuth.ClientId)}"
               + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
               + $"&response_type=code"
               + $"&state={Uri.EscapeDataString(state)}"
               + $"&scope=read_user+read_api";
    }

    public async Task<GitLabOAuthTokenResponse?> ExchangeCodeForToken(string code, string redirectUri)
    {
        var gitlabUrl = _settings.GitLab.ServerUrl.TrimEnd('/');

        try
        {
            return await _gitLabApiClient.ExecuteAsync<GitLabOAuthTokenResponse>(
                () => new HttpRequestMessage(HttpMethod.Post, $"{gitlabUrl}/oauth/token")
                {
                    Content = new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["client_id"] = _settings.GitLab.OAuth.ClientId,
                            ["client_secret"] = _settings.GitLab.OAuth.ClientSecret,
                            ["code"] = code,
                            ["grant_type"] = "authorization_code",
                            ["redirect_uri"] = redirectUri
                        })
                },
                _jsonOptions,
                "ExchangeCodeForToken",
                GitLabApiFailureBehavior.EnterStartupMode);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError(
                "GitLab token exchange failed: {StatusCode} {Body} (redirect_uri={RedirectUri})",
                (int)ex.StatusCode,
                ex.ResponseBody,
                redirectUri);

            return null;
        }
    }

    public async Task<GitLabOAuthTokenResponse?> RefreshToken(string refreshToken)
    {
        var gitlabUrl = _settings.GitLab.ServerUrl.TrimEnd('/');

        try
        {
            return await _gitLabApiClient.ExecuteAsync<GitLabOAuthTokenResponse>(
                () => new HttpRequestMessage(HttpMethod.Post, $"{gitlabUrl}/oauth/token")
                {
                    Content = new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["client_id"] = _settings.GitLab.OAuth.ClientId,
                            ["client_secret"] = _settings.GitLab.OAuth.ClientSecret,
                            ["refresh_token"] = refreshToken,
                            ["grant_type"] = "refresh_token"
                        })
                },
                _jsonOptions,
                "RefreshToken",
                GitLabApiFailureBehavior.EnterStartupMode);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError(
                "GitLab token refresh failed: {StatusCode} {Body}",
                (int)ex.StatusCode,
                ex.ResponseBody);

            return null;
        }
    }
}