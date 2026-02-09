using Mergician.Services;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ActivityController : ControllerBase
{
    private readonly GitLabOAuthService _oauthService;
    private readonly ILogger<ActivityController> _logger;

    public ActivityController(GitLabOAuthService oauthService, ILogger<ActivityController> logger)
    {
        _oauthService = oauthService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetActivity()
    {
        var accessToken = await GetValidAccessToken();
        if (accessToken == null)
            return Unauthorized();

        var events = await _oauthService.GetUserEvents(accessToken);

        // Enrich events with project names
        var projectCache = new Dictionary<int, string>();
        foreach (var evt in events)
        {
            if (evt.ProjectId > 0 && !projectCache.ContainsKey(evt.ProjectId))
            {
                var project = await _oauthService.GetProject(accessToken, evt.ProjectId);
                projectCache[evt.ProjectId] = project?.NameWithNamespace ?? $"Project #{evt.ProjectId}";
            }
        }

        var enrichedEvents = events.Select(e => new
        {
            e.Id,
            e.ActionName,
            e.TargetType,
            e.TargetTitle,
            e.CreatedAt,
            e.PushData,
            e.ProjectId,
            ProjectName = e.ProjectId > 0 && projectCache.TryGetValue(e.ProjectId, out var name)
                ? name
                : null
        });

        return Ok(enrichedEvents);
    }

    private async Task<string?> GetValidAccessToken()
    {
        var accessToken = Request.Cookies["gl_access_token"];
        if (!string.IsNullOrEmpty(accessToken))
        {
            var user = await _oauthService.GetCurrentUser(accessToken);
            if (user != null) return accessToken;
        }

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
}
