using Mergician.Entities;
using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly GitlabServiceUser _serviceUser;

    public HealthController(GitlabServiceUser serviceUser)
    {
        _serviceUser = serviceUser;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var errors = new List<string>();

        if (!_serviceUser.IsConfigured)
            errors.Add("GitLab service token is not configured. Set the Mergician:GitLab:ServiceToken setting.");

        return Ok(new HealthStatus
        {
            Status = errors.Count > 0 ? "degraded" : "healthy",
            ConfigurationErrors = errors,
            Timestamp = DateTime.UtcNow
        });
    }
}
