using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.GitLab;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Util;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly GitLabAuthSettings _authSettings;

    private readonly GitLabService _gitLabService;

    private readonly ILogger<AuthController> _logger;

    private readonly GitLabOAuthService _oauthService;

    public AuthController(
        GitLabOAuthService oauthService,
        GitLabService gitLabService,
        GitLabAuthSettings authSettings,
        ILogger<AuthController> logger)
    {
        _oauthService = oauthService;
        _gitLabService = gitLabService;
        _authSettings = authSettings;
        _logger = logger;
    }

    [HttpGet("login")]
    public ActionResult Login()
    {
        var redirectUri = GetRedirectUri();
        var state = Guid.NewGuid().ToString("N");
        var useSecureCookies = CookieSecurity.ShouldUseSecureCookies(Request);

        Response.Cookies.Append(
            "oauth_state",
            state,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = "/api/auth/callback",
                Secure = useSecureCookies
            });

        var authUrl = _oauthService.GetAuthorizationUrl(redirectUri, state);
        return Redirect(authUrl);
    }

    [HttpGet("callback")]
    public async Task<ActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error)
    {
        // Handle OAuth denial or other provider-side errors before checking state
        if (!error.IsEmpty())
        {
            _logger.LogWarning("OAuth provider returned error: {Error}", error);
            Response.Cookies.Delete(
                "oauth_state",
                new CookieOptions
                {
                    SameSite = SameSiteMode.Lax,
                    Path = "/api/auth/callback",
                    Secure = CookieSecurity.ShouldUseSecureCookies(Request)
                });

            return Redirect(
                $"/?error=auth_denied&message={Uri.EscapeDataString("Authorization was denied.")}");
        }

        // Validate state parameter
        var savedState = Request.Cookies["oauth_state"];
        if (savedState.IsEmpty() || state.IsEmpty() || savedState != state)
        {
            _logger.LogWarning("OAuth state mismatch");
            return BadRequest("Invalid OAuth state");
        }

        Response.Cookies.Delete(
            "oauth_state",
            new CookieOptions
            {
                SameSite = SameSiteMode.Lax,
                Path = "/api/auth/callback",
                Secure = CookieSecurity.ShouldUseSecureCookies(Request)
            });

        var redirectUri = GetRedirectUri();

        if (code.IsEmpty())
        {
            _logger.LogWarning("OAuth callback received without a code parameter");
            return BadRequest("Missing authorization code");
        }

        GitLabOAuthTokenResponse? tokenResponse;
        try
        {
            tokenResponse = await _oauthService.ExchangeCodeForToken(code, redirectUri);
        }
        catch (GitLabStartupRequiredException ex)
        {
            _logger.LogError(
                ex,
                "GitLab became unavailable during OAuth callback (redirect_uri={RedirectUri}); returning to startup mode",
                redirectUri);

            return Redirect("/");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Unexpected error during token exchange (redirect_uri={RedirectUri})",
                redirectUri);

            return Redirect(
                $"/?error=server&message={Uri.EscapeDataString("An unexpected error occurred during authentication")}");
        }

        if (tokenResponse == null)
        {
            _logger.LogError("Failed to exchange code for token - GitLab returned an error response");
            return Redirect(
                $"/?error=auth&message={Uri.EscapeDataString("Authentication with GitLab failed. Please try again.")}");
        }

        // Store tokens in secure cookies
        var useSecureCookies = CookieSecurity.ShouldUseSecureCookies(Request);
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(30),
            Path = "/",
            Secure = useSecureCookies
        };

        Response.Cookies.Append("gl_access_token", tokenResponse.AccessToken, cookieOptions);
        Response.Cookies.Append("gl_refresh_token", tokenResponse.RefreshToken, cookieOptions);

        // Fetch and persist the user ID so the authentication handler can include it
        // in the AccessDetailsForUser on subsequent requests without an additional API call
        var tempUser = new AccessDetailsBase(tokenResponse.AccessToken, _authSettings.ApiBaseUrl);
        var userInfo = await _gitLabService.GetCurrentUser(tempUser, HttpContext.RequestAborted);
        if (userInfo != null)
        {
            _logger.LogDebug("Storing user ID {UserId} in cookie after login", userInfo.Id);
            Response.Cookies.Append("gl_user_id", userInfo.Id.ToString(), cookieOptions);
        }
        else
        {
            _logger.LogError("Could not retrieve user info after token exchange; login cannot complete");
            return StatusCode(
                StatusCodes.Status502BadGateway,
                "Could not retrieve user information from GitLab after authentication");
        }

        // Redirect to frontend home
        return Redirect("/");
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<GitLabUserInfo>> Me()
    {
        var accessUser = HttpContext.GetGitLabUser();

        var user = await _gitLabService.GetCurrentUser(accessUser, HttpContext.RequestAborted);
        if (user == null)
        {
            return Unauthorized();
        }

        return Ok(user);
    }

    [HttpPost("logout")]
    public ActionResult Logout()
    {
        var useSecureCookies = CookieSecurity.ShouldUseSecureCookies(Request);

        Response.Cookies.Delete(
            "gl_access_token",
            new CookieOptions { Path = "/", Secure = useSecureCookies, SameSite = SameSiteMode.Lax });

        Response.Cookies.Delete(
            "gl_refresh_token",
            new CookieOptions { Path = "/", Secure = useSecureCookies, SameSite = SameSiteMode.Lax });

        Response.Cookies.Delete(
            "gl_user_id",
            new CookieOptions { Path = "/", Secure = useSecureCookies, SameSite = SameSiteMode.Lax });

        return Ok();
    }

    private string GetRedirectUri()
    {
        var scheme = Request.Scheme;
        var host = Request.Host;
        return $"{scheme}://{host}/api/auth/callback";
    }
}