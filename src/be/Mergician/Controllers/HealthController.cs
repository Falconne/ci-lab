using Mergician.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new HealthStatus
        {
            Status = "healthy",
            Timestamp = DateTimeOffset.UtcNow
        });
    }
}
