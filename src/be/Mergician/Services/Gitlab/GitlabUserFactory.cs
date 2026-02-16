using System.Net.Http.Headers;
using Serilog;

namespace Mergician.Services.Gitlab;

/// <summary>
///     Factory for creating GitlabAccessUser instances for the current OAuth user
///     or the configured service user. Only controllers should use this factory;
///     services should receive a GitlabAccessUser directly.
/// </summary>
public class GitlabUserFactory
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly GitLabOAuthService _oauthService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiBaseUrl;
    private readonly string? _serviceToken;

    private string? _cachedCurrentUserToken;
    private bool _currentUserTokenResolved;

    public bool IsServiceTokenConfigured => !string.IsNullOrWhiteSpace(_serviceToken);

    public GitlabUserFactory(
        IHttpContextAccessor httpContextAccessor,
        GitLabOAuthService oauthService,
        IHttpClientFactory httpClientFactory,
        string apiBaseUrl,
        string? serviceToken)
    {
        _httpContextAccessor = httpContextAccessor;
        _oauthService = oauthService;
        _httpClientFactory = httpClientFactory;
        _apiBaseUrl = apiBaseUrl;
        _serviceToken = serviceToken;

        if (!IsServiceTokenConfigured)
            Log.Warning("GitLab service token is not configured. Set the Mergician:GitLab:ServiceToken setting");
    }

    /// <summary>
    ///     Returns a GitlabAccessUser for the currently authenticated OAuth user,
    ///     or null if the user is not authenticated or the token cannot be resolved.
    /// </summary>
    public async Task<GitlabAccessUser?> GetCurrentUser()
    {
        var token = await GetCurrentUserToken();
        if (token == null)
        {
            Log.Debug("No valid current user token available");
            return null;
        }

        return new GitlabAccessUser(token, _apiBaseUrl);
    }

    /// <summary>
    ///     Returns a GitlabAccessUser for the configured service account,
    ///     or null if the service token is not configured.
    /// </summary>
    public GitlabAccessUser? GetServiceUser()
    {
        var token = GetServiceUserToken();
        if (token == null)
        {
            Log.Debug("No service user token available");
            return null;
        }

        return new GitlabAccessUser(token, _apiBaseUrl);
    }

    private async Task<string?> GetCurrentUserToken()
    {
        if (_currentUserTokenResolved)
            return _cachedCurrentUserToken;

        _cachedCurrentUserToken = await ResolveCurrentUserToken();
        _currentUserTokenResolved = true;
        return _cachedCurrentUserToken;
    }

    private string? GetServiceUserToken()
    {
        if (!IsServiceTokenConfigured)
        {
            Log.Debug("GitLab service token is not configured — cannot create service user");
            return null;
        }

        return _serviceToken;
    }

    private async Task<string?> ResolveCurrentUserToken()
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
        var url = $"{_apiBaseUrl}/user";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);
        return response.IsSuccessStatusCode;
    }
}
