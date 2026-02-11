using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ActivityController : ControllerBase
{
    private readonly GitlabCurrentUser _currentUser;
    private readonly GitlabService _gitlabService;
    private readonly ILogger<ActivityController> _logger;

    public ActivityController(
        GitlabCurrentUser currentUser,
        GitlabService gitlabService,
        ILogger<ActivityController> logger)
    {
        _currentUser = currentUser;
        _gitlabService = gitlabService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetActivity()
    {
        var accessToken = await _currentUser.GetValidAccessToken();
        if (accessToken == null)
            return Unauthorized();

        var events = await _gitlabService.GetUserEvents(_currentUser);

        // Enrich events with project names
        var projectCache = new Dictionary<int, string>();
        foreach (var evt in events)
        {
            if (evt.ProjectId > 0 && !projectCache.ContainsKey(evt.ProjectId))
            {
                var project = await _gitlabService.GetProject(_currentUser, evt.ProjectId);
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
}
