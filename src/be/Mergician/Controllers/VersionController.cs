using Mergician.Entities;
using Mergician.Services;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VersionController : ControllerBase
{
    private readonly VersionService _versionService;

    public VersionController(VersionService versionService)
    {
        _versionService = versionService;
    }

    [HttpGet]
    public IActionResult GetVersion()
    {
        return Ok(new VersionResponse(_versionService.GetVersion()));
    }
}
