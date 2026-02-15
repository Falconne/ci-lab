using Mergician.Entities;
using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly GitLabOAuthService _oauthService;
    private readonly GitlabCurrentUser _currentUser;
    private readonly GitlabService _gitlabService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        GitLabOAuthService oauthService,
        GitlabCurrentUser currentUser,
        GitlabService gitlabService,
        ILogger<AuthController> logger)
    {
        _oauthService = oauthService;
        _currentUser = currentUser;
        _gitlabService = gitlabService;
        _logger = logger;
    }

    [HttpGet("login")]
    public IActionResult Login()
    {
        var redirectUri = GetRedirectUri();
        var state = Guid.NewGuid().ToString("N");

        Response.Cookies.Append("oauth_state", state, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10)
        });

        var authUrl = _oauthService.GetAuthorizationUrl(redirectUri, state);
        return Redirect(authUrl);
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string state)
    {
        // Validate state parameter
        var savedState = Request.Cookies["oauth_state"];
        if (string.IsNullOrEmpty(savedState) || savedState != state)
        {
            _logger.LogWarning("OAuth state mismatch");
            return BadRequest("Invalid OAuth state");
        }

        Response.Cookies.Delete("oauth_state");

        var redirectUri = GetRedirectUri();

        GitLabOAuthTokenResponse? tokenResponse;
        try
        {
            tokenResponse = await _oauthService.ExchangeCodeForToken(code, redirectUri);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to GitLab for token exchange (redirect_uri={RedirectUri}). " +
                "Check that the GitLab InternalUrl is reachable from the Mergician container", redirectUri);
            return Redirect($"/?error=connection&message={Uri.EscapeDataString("Unable to reach GitLab for authentication")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error during token exchange (redirect_uri={RedirectUri})", redirectUri);
            return Redirect($"/?error=server&message={Uri.EscapeDataString("An unexpected error occurred during authentication")}");
        }

        if (tokenResponse == null)
        {
            _logger.LogError("Failed to exchange code for token - GitLab returned an error response");
            return Redirect($"/?error=auth&message={Uri.EscapeDataString("Authentication with GitLab failed. Please try again.")}");
        }

        // Store tokens in secure cookies
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(30),
            Path = "/"
        };

        Response.Cookies.Append("gl_access_token", tokenResponse.AccessToken, cookieOptions);
        Response.Cookies.Append("gl_refresh_token", tokenResponse.RefreshToken, cookieOptions);

        // Redirect to frontend home
        return Redirect("/");
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var user = await _gitlabService.GetCurrentUser(_currentUser);
        if (user == null)
            return Unauthorized();

        return Ok(user);
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("gl_access_token", new CookieOptions { Path = "/" });
        Response.Cookies.Delete("gl_refresh_token", new CookieOptions { Path = "/" });
        return Ok();
    }

    private string GetRedirectUri()
    {
        var scheme = Request.Scheme;
        var host = Request.Host;
        return $"{scheme}://{host}/api/auth/callback";
    }
}
