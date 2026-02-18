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
    private readonly IMergicianRepository _repository;

    public HealthController(GitlabUserFactory userFactory, IMergicianRepository repository)
    {
        _userFactory = userFactory;
        _repository = repository;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var errors = new List<string>();

        if (!_userFactory.IsServiceTokenConfigured)
            errors.Add("GitLab service token is not configured. Set the Mergician:GitLab:ServiceToken setting.");

        if (!_repository.IsHealthy())
            errors.Add("Database is not reachable. Check the Mergician:Database settings.");

        return Ok(new HealthStatus
        {
            Status = errors.Count > 0 ? "degraded" : "healthy",
            ConfigurationErrors = errors,
            Timestamp = DateTime.UtcNow
        });
    }
}
