using Mergician.Entities;
using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

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

    private readonly GitlabService _gitlabService;

    private readonly GitLabOAuthService _oauthService;

    private readonly StartupStateService _startupStateService;

    public GitLabCookieAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        GitLabOAuthService oauthService,
        GitlabService gitlabService,
        GitLabAuthSettings authSettings,
        StartupStateService startupStateService)
        : base(options, logger, encoder)
    {
        _oauthService = oauthService;
        _gitlabService = gitlabService;
        _apiBaseUrl = authSettings.ApiBaseUrl;
        _startupStateService = startupStateService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // When the application is not ready (initial startup or GitLab recovery),
        // skip token validation entirely. GitLab is unreachable so any validation
        // attempt will exhaust retries and throw. Endpoints that don't require
        // [Authorize] (like /api/startup/status) will proceed unauthenticated.
        if (!_startupStateService.GetStatus().IsReady)
        {
            Logger.LogDebug("Skipping authentication: application is not ready");
            return AuthenticateResult.NoResult();
        }

        try
        {
            return await AuthenticateCore();
        }
        catch (GitLabApiFailureException ex)
        {
            Logger.LogWarning(ex, "GitLab unavailable during authentication, skipping token validation");
            return AuthenticateResult.NoResult();
        }
        catch (GitLabStartupRequiredException ex)
        {
            Logger.LogWarning(ex, "GitLab recovery triggered during authentication, skipping token validation");
            return AuthenticateResult.NoResult();
        }
    }

    private async Task<AuthenticateResult> AuthenticateCore()
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

    private async Task<GitLabUserInfo?> ValidateToken(string accessToken)
    {
        var accessDetails = new AccessDetailsBase(accessToken, _apiBaseUrl);
        return await _gitlabService.GetCurrentUser(accessDetails);
    }
}