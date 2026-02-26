using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ActivityController : SseControllerBase
{
    private readonly GitlabActivityService _activityService;

    private readonly ICoreRepository _coreRepository;

    private readonly GitlabService _gitlabService;

    private readonly ILogger<ActivityController> _logger;

    private readonly UserActivitySyncService _syncService;

    public ActivityController(
        GitlabActivityService activityService,
        GitlabService gitlabService,
        ICoreRepository coreRepository,
        UserActivitySyncService syncService,
        ILogger<ActivityController> logger)
    {
        _activityService = activityService;
        _gitlabService = gitlabService;
        _coreRepository = coreRepository;
        _syncService = syncService;
        _logger = logger;
    }

    [HttpPost("poll")]
    public async Task<IActionResult> PollDashboard(
        [FromBody] DashboardPollRequest request,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo == null)
        {
            return Unauthorized();
        }

        // Ensure the background sync thread is running (also records that user is still active)
        _syncService.EnsureSyncRunning(userInfo.Id, currentUser);

        _logger.LogDebug(
            "Dashboard poll for user {UserId} with {Count} known branches",
            userInfo.Id,
            request.KnownBranches.Count);

        var result = _activityService.GetDashboardDiff(userInfo.Id, request.KnownBranches);

        _logger.LogDebug(
            "Returning {Added} added, {Removed} removed branches for user {UserId}",
            result.Added.Count,
            result.Removed.Count,
            userInfo.Id);

        return Ok(result);
    }

    [HttpPost("refresh")]
    public async Task RefreshActivity(
        [FromBody] List<BranchRefreshRequest> branches,
        CancellationToken cancellationToken)
    {
        var currentUser = HttpContext.GetGitlabUser();

        _logger.LogInformation("Starting SSE refresh stream for {Count} branches", branches.Count);

        // Also keep the background sync thread alive during refresh
        var userInfo = await _gitlabService.GetCurrentUser(currentUser);
        if (userInfo != null)
        {
            _syncService.EnsureSyncRunning(userInfo.Id, currentUser);
        }

        await StreamSse(
            "refresh",
            async (streamToken, writeLock) =>
            {
                await foreach (var item in _activityService.StreamRefreshBranchStatus(
                                   currentUser,
                                   branches,
                                   streamToken))
                {
                    if (item is BranchDeletedNotification deleted)
                    {
                        await WriteSseEvent(deleted, streamToken, writeLock, "deleted");
                    }
                    else
                    {
                        await WriteSseEvent(item, streamToken, writeLock);
                    }
                }
            },
            cancellationToken,
            _logger);
    }
}