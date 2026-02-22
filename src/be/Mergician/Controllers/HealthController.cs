using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly GitlabUserFactory _userFactory;
    private readonly ICoreRepository _coreRepository;

    public HealthController(GitlabUserFactory userFactory, ICoreRepository coreRepository)
    {
        _userFactory = userFactory;
        _coreRepository = coreRepository;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var errors = new List<string>();

        if (!_userFactory.IsServiceTokenConfigured)
            errors.Add("GitLab service token is not configured. Set the Mergician:GitLab:ServiceToken setting.");

        if (!_coreRepository.IsHealthy())
            errors.Add("Database is not reachable. Check the Mergician:Database settings.");

        return Ok(new HealthStatus
        {
            Status = errors.Count > 0 ? "degraded" : "healthy",
            ConfigurationErrors = errors,
            Timestamp = DateTimeOffset.UtcNow
        });
    }
}
