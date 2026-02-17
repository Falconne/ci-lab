using Mergician.Entities;
using Mergician.Services.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly GitlabUserFactory _userFactory;

    public HealthController(GitlabUserFactory userFactory)
    {
        _userFactory = userFactory;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var errors = new List<string>();

        if (!_userFactory.IsServiceTokenConfigured)
            errors.Add("GitLab service token is not configured. Set the Mergician:GitLab:ServiceToken setting.");

        return Ok(new HealthStatus
        {
            Status = errors.Count > 0 ? "degraded" : "healthy",
            ConfigurationErrors = errors,
            Timestamp = DateTime.UtcNow
        });
    }
}
