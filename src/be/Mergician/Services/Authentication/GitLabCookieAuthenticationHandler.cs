using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Mergician.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Mergician.Services.Authentication;

/// <summary>
///     Custom ASP.NET Core authentication handler that validates GitLab OAuth tokens
///     stored in cookies. Handles token refresh transparently when the access token
///     has expired but a valid refresh token is available.
///     On successful authentication, stores a GitlabAccessDetailsForUser in HttpContext.Items
///     for controllers to use.
/// </summary>
public class GitLabCookieAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "GitLabCookie";
    public const string GitlabAccessUserKey = "GitlabAccessDetailsForUser";

    private readonly GitLabOAuthService _oauthService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiBaseUrl;

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
            if (await ValidateToken(accessToken))
            {
                Logger.LogDebug("Access token from cookie is valid");
                return SuccessResult(accessToken);
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

        Logger.LogDebug("Token refreshed successfully via authentication handler");
        return SuccessResult(tokenResponse.AccessToken);
    }

    private AuthenticateResult SuccessResult(string accessToken)
    {
        var user = new GitlabAccessDetailsForUser(accessToken, _apiBaseUrl);
        Context.Items[GitlabAccessUserKey] = user;

        var claims = new[] { new Claim(ClaimTypes.Authentication, "gitlab-oauth") };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
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
