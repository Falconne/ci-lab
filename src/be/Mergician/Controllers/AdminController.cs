using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.GitLab;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[Authorize]
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly GitLabService _gitLabService;

    private readonly ILogger<AdminController> _logger;

    private readonly IMonitoredProjectRepository _monitoredProjectRepository;

    public AdminController(
        GitLabService gitLabService,
        IMonitoredProjectRepository monitoredProjectRepository,
        ILogger<AdminController> logger)
    {
        _gitLabService = gitLabService;
        _monitoredProjectRepository = monitoredProjectRepository;
        _logger = logger;
    }

    [HttpGet("monitored-projects")]
    public IActionResult GetMonitoredProjects()
    {
        var projects = _monitoredProjectRepository.GetAll();

        _logger.LogDebug("AdminController: returning {Count} monitored projects", projects.Count);

        return Ok(projects);
    }

    [HttpPost("monitored-projects")]
    public async Task<IActionResult> AddMonitoredProject(
        [FromBody] AddMonitoredProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ProjectId <= 0)
        {
            _logger.LogWarning(
                "AdminController: invalid project ID {ProjectId} in add request",
                request.ProjectId);

            return BadRequest("ProjectId must be a positive integer.");
        }

        _logger.LogInformation(
            "AdminController: resolving project {ProjectId} from GitLab",
            request.ProjectId);

        var accessDetails = HttpContext.GetGitLabUser();
        var project = await _gitLabService.GetProject(accessDetails, request.ProjectId, cancellationToken);

        if (project == null)
        {
            _logger.LogWarning(
                "AdminController: project {ProjectId} not found in GitLab",
                request.ProjectId);

            return BadRequest($"Project with ID {request.ProjectId} was not found in GitLab.");
        }

        _logger.LogInformation(
            "AdminController: adding monitored project {ProjectId} '{ProjectName}'",
            request.ProjectId,
            project.Name);

        _monitoredProjectRepository.Upsert(project.Id, project.Name);

        return Ok(new { project.Id, project.Name });
    }

    [HttpDelete("monitored-projects/{projectId:int}")]
    public IActionResult RemoveMonitoredProject(int projectId)
    {
        if (!_monitoredProjectRepository.IsMonitoredProject(projectId))
        {
            _logger.LogWarning(
                "AdminController: project {ProjectId} is not in the monitored list",
                projectId);

            return NotFound($"Project with ID {projectId} is not currently monitored.");
        }

        _logger.LogInformation("AdminController: removing monitored project {ProjectId}", projectId);

        _monitoredProjectRepository.Remove(projectId);

        return NoContent();
    }
}

public record AddMonitoredProjectRequest(int ProjectId);