using Mergician.Entities;
using Mergician.Services;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly HealthService _healthService;

    public HealthController(HealthService healthService)
    {
        _healthService = healthService;
    }

    /// <summary>
    ///     Returns the current health status. Existing tabs poll this endpoint to detect a
    ///     restart or GitLab outage, and new tabs use the same response to land directly in
    ///     the correct startup or recovery overlay.
    ///     Returns 503 when the application is not yet ready so clients can distinguish
    ///     an expected startup delay from a misconfigured probe.
    /// </summary>
    [HttpGet]
    public ActionResult<HealthStatus> Get()
    {
        var status = _healthService.GetStatus();
        return status.IsReady ? Ok(status) : StatusCode(503, status);
    }
}