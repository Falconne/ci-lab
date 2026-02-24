using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[Authorize]
[ApiController]
[Route("api/merge-groups")]
public class MergeGroupController : ControllerBase
{
    private readonly GitlabActivityService _activityService;

    private readonly ICoreRepository _coreRepository;

    private readonly GitlabService _gitlabService;

    private readonly ILogger<MergeGroupController> _logger;

    public MergeGroupController(
        GitlabActivityService activityService,
        GitlabService gitlabService,
        ICoreRepository coreRepository,
        ILogger<MergeGroupController> logger)
    {
        _activityService = activityService;
        _gitlabService = gitlabService;
        _coreRepository = coreRepository;
        _logger = logger;
    }

    /// <summary>
    ///     Returns fully resolved details for a single merge group.
    /// </summary>
    [HttpGet("{mergeGroupId:int}")]
    public async Task<IActionResult> GetMergeGroupDetails(
        int mergeGroupId,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        if (!_coreRepository.IsHealthy())
        {
            _logger.LogError(
                "Database is unhealthy, cannot fetch merge group details for merge group {MergeGroupId}",
                mergeGroupId);

            return StatusCode(503, new ErrorResponse("Database is unavailable"));
        }

        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo == null)
        {
            return Unauthorized();
        }

        _logger.LogInformation(
            "Fetching merge group details for user {UserId}, merge group {MergeGroupId}",
            userInfo.Id,
            mergeGroupId);

        var details = await _activityService.GetMergeGroupDetails(
            currentUser,
            userInfo.Id,
            mergeGroupId,
            cancellationToken);

        if (details == null)
        {
            return NotFound(new ErrorResponse("Merge group not found"));
        }

        return Ok(details);
    }
}
