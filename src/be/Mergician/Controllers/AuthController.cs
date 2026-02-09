using Mergician.Services;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly GitLabOAuthService _oauthService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(GitLabOAuthService oauthService, ILogger<AuthController> logger)
    {
        _oauthService = oauthService;
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
        var tokenResponse = await _oauthService.ExchangeCodeForToken(code, redirectUri);
        if (tokenResponse == null)
        {
            _logger.LogError("Failed to exchange code for token");
            return BadRequest("Failed to authenticate with GitLab");
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
        var accessToken = await GetValidAccessToken();
        if (accessToken == null)
            return Unauthorized();

        var user = await _oauthService.GetCurrentUser(accessToken);
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

    private async Task<string?> GetValidAccessToken()
    {
        var accessToken = Request.Cookies["gl_access_token"];
        if (!string.IsNullOrEmpty(accessToken))
        {
            // Verify the token still works
            var user = await _oauthService.GetCurrentUser(accessToken);
            if (user != null) return accessToken;
        }

        // Try to refresh
        var refreshToken = Request.Cookies["gl_refresh_token"];
        if (string.IsNullOrEmpty(refreshToken)) return null;

        var tokenResponse = await _oauthService.RefreshToken(refreshToken);
        if (tokenResponse == null) return null;

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(30),
            Path = "/"
        };

        Response.Cookies.Append("gl_access_token", tokenResponse.AccessToken, cookieOptions);
        Response.Cookies.Append("gl_refresh_token", tokenResponse.RefreshToken, cookieOptions);

        return tokenResponse.AccessToken;
    }

    private string GetRedirectUri()
    {
        var scheme = Request.Scheme;
        var host = Request.Host;
        return $"{scheme}://{host}/api/auth/callback";
    }
}
