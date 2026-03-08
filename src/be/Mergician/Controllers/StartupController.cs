using Mergician.Entities;
using Mergician.Services;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[ApiController]
[Route("api/startup")]
public class StartupController : ControllerBase
{
    private readonly StartupService _startupService;

    public StartupController(StartupService startupService)
    {
        _startupService = startupService;
    }

    /// <summary>
    ///     Returns the current startup status. Existing tabs poll this endpoint to detect a
    ///     restart or GitLab outage, and new tabs use the same response to land directly in
    ///     the correct startup or recovery overlay.
    /// </summary>
    [HttpGet("status")]
    public ActionResult<StartupStatus> GetStatus()
    {
        return Ok(_startupService.GetStatus());
    }
}
