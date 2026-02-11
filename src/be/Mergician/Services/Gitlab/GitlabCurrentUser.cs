using System.Net.Http.Headers;
using Mergician.Entities;
using Serilog;

namespace Mergician.Services.Gitlab;

public class GitlabCurrentUser : IGitlabAccessUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly GitLabOAuthService _oauthService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MergicianSettings _settings;

    private string? _cachedAccessToken;
    private bool _resolved;

    public GitlabCurrentUser(
        IHttpContextAccessor httpContextAccessor,
        GitLabOAuthService oauthService,
        IHttpClientFactory httpClientFactory,
        MergicianSettings settings)
    {
        _httpContextAccessor = httpContextAccessor;
        _oauthService = oauthService;
        _httpClientFactory = httpClientFactory;
        _settings = settings;
    }

    public async Task<string?> GetValidAccessToken()
    {
        if (_resolved) return _cachedAccessToken;

        _resolved = true;
        _cachedAccessToken = await ResolveAccessToken();
        return _cachedAccessToken;
    }

    private async Task<string?> ResolveAccessToken()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null)
        {
            Log.Warning("No HttpContext available for resolving access token");
            return null;
        }

        var accessToken = context.Request.Cookies["gl_access_token"];
        if (!string.IsNullOrEmpty(accessToken))
        {
            if (await ValidateToken(accessToken))
            {
                Log.Debug("Access token from cookie is valid");
                return accessToken;
            }

            Log.Debug("Access token from cookie is invalid, attempting refresh");
        }

        var refreshToken = context.Request.Cookies["gl_refresh_token"];
        if (string.IsNullOrEmpty(refreshToken))
        {
            Log.Debug("No refresh token available");
            return null;
        }

        var tokenResponse = await _oauthService.RefreshToken(refreshToken);
        if (tokenResponse == null)
        {
            Log.Warning("Token refresh failed");
            return null;
        }

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(30),
            Path = "/"
        };

        context.Response.Cookies.Append("gl_access_token", tokenResponse.AccessToken, cookieOptions);
        context.Response.Cookies.Append("gl_refresh_token", tokenResponse.RefreshToken, cookieOptions);

        Log.Debug("Token refreshed successfully");
        return tokenResponse.AccessToken;
    }

    private async Task<bool> ValidateToken(string accessToken)
    {
        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var gitlabUrl = _settings.GitLab.ServerUrl.TrimEnd('/');

        var request = new HttpRequestMessage(HttpMethod.Get, $"{gitlabUrl}/api/v4/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request);
        return response.IsSuccessStatusCode;
    }
}
