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
    ///     Returns the current startup status. Used by the frontend to poll during startup
    ///     and display a loading box until the application is ready.
    /// </summary>
    [HttpGet("status")]
    public ActionResult<StartupStatus> GetStatus()
    {
        return Ok(_startupService.GetStatus());
    }
}
