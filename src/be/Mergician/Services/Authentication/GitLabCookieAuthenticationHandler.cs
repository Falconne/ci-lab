using Mergician.Entities;
using Mergician.Utilities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mergician.Services.Authentication;

/// <summary>
///     Custom ASP.NET Core authentication handler that validates GitLab OAuth tokens
///     stored in cookies. Handles token refresh transparently when the access token
///     has expired but a valid refresh token is available.
///     On successful authentication, stores a AccessDetailsForUser in HttpContext.Items
///     for controllers to use.
/// </summary>
public class GitLabCookieAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "GitLabCookie";

    public const string GitlabAccessUserKey = "AccessDetailsForUser";

    private readonly string _apiBaseUrl;

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly GitLabOAuthService _oauthService;

    public GitLabCookieAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        GitLabOAuthService oauthService,
        IHttpClientFactory httpClientFactory,
        GitLabAuthSettings authSettings)
        : base(options, logger, encoder)
    {
        _oauthService = oauthService;
        _httpClientFactory = httpClientFactory;
        _apiBaseUrl = authSettings.ApiBaseUrl;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var accessToken = Request.Cookies["gl_access_token"];
        if (!string.IsNullOrEmpty(accessToken))
        {
            var userInfo = await ValidateToken(accessToken);
            if (userInfo != null)
            {
                Logger.LogDebug("Access token from cookie is valid for user {UserId}", userInfo.Id);
                return CreateSuccessResult(accessToken, userInfo.Id);
            }

            Logger.LogDebug("Access token from cookie is invalid, attempting refresh");
        }

        var refreshToken = Request.Cookies["gl_refresh_token"];
        if (string.IsNullOrEmpty(refreshToken))
        {
            Logger.LogDebug("No valid access or refresh token available");
            return AuthenticateResult.NoResult();
        }

        var tokenResponse = await _oauthService.RefreshToken(refreshToken);
        if (tokenResponse == null)
        {
            Logger.LogWarning("Token refresh failed");
            return AuthenticateResult.Fail("Token refresh failed");
        }

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(30),
            Path = "/",
            Secure = CookieSecurity.ShouldUseSecureCookies(Request)
        };

        Response.Cookies.Append("gl_access_token", tokenResponse.AccessToken, cookieOptions);
        Response.Cookies.Append("gl_refresh_token", tokenResponse.RefreshToken, cookieOptions);

        // Re-use the persisted user ID — the user identity doesn't change during token refresh
        var existingUserIdStr = Request.Cookies["gl_user_id"];
        if (!int.TryParse(existingUserIdStr, out var userId))
        {
            Logger.LogDebug("gl_user_id cookie missing after token refresh; fetching user ID from GitLab");
            var refreshedUserInfo = await ValidateToken(tokenResponse.AccessToken);
            if (refreshedUserInfo == null)
            {
                Logger.LogWarning("Could not retrieve user ID after token refresh; authentication failed");
                return AuthenticateResult.Fail("Could not retrieve user ID after token refresh");
            }

            userId = refreshedUserInfo.Id;
            Response.Cookies.Append("gl_user_id", userId.ToString(), cookieOptions);
            Logger.LogDebug("Stored user ID {UserId} in cookie after token refresh", userId);
        }

        Logger.LogDebug("Token refreshed successfully via authentication handler");
        return CreateSuccessResult(tokenResponse.AccessToken, userId);
    }

    private AuthenticateResult CreateSuccessResult(string accessToken, int userId)
    {
        var user = new AccessDetailsForUser(accessToken, _apiBaseUrl, userId);
        Context.Items[GitlabAccessUserKey] = user;

        var claims = new[] { new Claim(ClaimTypes.Authentication, "gitlab-oauth") };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    private static readonly JsonSerializerOptions _jsonOptions = JsonOptions.SnakeCaseLower;

    private async Task<GitLabUserInfo?> ValidateToken(string accessToken)
    {
        var url = $"{_apiBaseUrl}/user";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var client = _httpClientFactory.CreateClient("GitLabOAuth");
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitLabUserInfo>(json, _jsonOptions);
    }
}