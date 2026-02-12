using Mergician.Entities;
using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ActivityController : ControllerBase
{
    private readonly GitlabCurrentUser _currentUser;
    private readonly GitlabService _gitlabService;
    private readonly ILogger<ActivityController> _logger;

    public ActivityController(
        GitlabCurrentUser currentUser,
        GitlabService gitlabService,
        ILogger<ActivityController> logger)
    {
        _currentUser = currentUser;
        _gitlabService = gitlabService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetPushActivity()
    {
        var accessToken = await _currentUser.GetValidAccessToken();
        if (accessToken == null)
            return Unauthorized();

        _logger.LogInformation("Fetching push activity for last 14 days");
        var events = await _gitlabService.GetUserEvents(_currentUser, days: 14);

        // Filter to push events with branch refs, excluding default branches
        var pushEvents = events
            .Where(e => e.PushData?.RefType == "branch" && e.PushData.Ref != null)
            .Where(e => !GitlabService.IsPossibleDefaultBranch(e.PushData!.Ref!))
            .ToList();

        _logger.LogInformation("Found {Count} push events after filtering default branches", pushEvents.Count);

        // Deduplicate by (branchName, projectId)
        var uniqueBranches = pushEvents
            .Select(e => new { BranchName = e.PushData!.Ref!, e.ProjectId })
            .Distinct()
            .ToList();

        _logger.LogInformation("Found {Count} unique branch/project combinations", uniqueBranches.Count);

        // Resolve project names
        var projectCache = new Dictionary<int, string>();
        foreach (var entry in uniqueBranches)
        {
            if (entry.ProjectId > 0 && !projectCache.ContainsKey(entry.ProjectId))
            {
                var project = await _gitlabService.GetProject(_currentUser, entry.ProjectId);
                projectCache[entry.ProjectId] = project?.NameWithNamespace ?? $"Project #{entry.ProjectId}";
            }
        }

        var results = new List<BranchActivity>();

        foreach (var entry in uniqueBranches)
        {
            // Check if the branch still exists
            var exists = await _gitlabService.BranchExists(_currentUser, entry.ProjectId, entry.BranchName);
            if (!exists)
            {
                _logger.LogDebug("Skipping branch '{BranchName}' in project {ProjectId} - no longer exists",
                    entry.BranchName, entry.ProjectId);
                continue;
            }

            // Look for open MRs from this branch
            var mergeRequests = await _gitlabService.GetMergeRequests(
                _currentUser, entry.ProjectId, entry.BranchName);

            var hasMr = mergeRequests.Count > 0;
            int? approvalsRequired = null;
            int? approvalsGiven = null;

            if (hasMr)
            {
                _logger.LogDebug("Branch '{BranchName}' has {MrCount} open MR(s) in project {ProjectId}",
                    entry.BranchName, mergeRequests.Count, entry.ProjectId);

                // Use the first open MR for approval info
                var approval = await _gitlabService.GetMergeRequestApprovals(
                    _currentUser, entry.ProjectId, mergeRequests[0].Iid);

                if (approval != null)
                {
                    approvalsGiven = approval.ApprovedBy.Count;
                    // GitLab CE doesn't expose approvals_required via this API,
                    // so we use the project's configured approvals_before_merge setting.
                    // For now, report what we know: how many approvals were given.
                    approvalsRequired = approvalsGiven > 0 ? approvalsGiven : null;
                }
            }

            var projectName = projectCache.GetValueOrDefault(entry.ProjectId, $"Project #{entry.ProjectId}");

            results.Add(new BranchActivity(
                entry.BranchName,
                entry.ProjectId,
                projectName,
                hasMr,
                approvalsRequired,
                approvalsGiven));
        }

        _logger.LogInformation("Returning {Count} branch activity records", results.Count);
        return Ok(results);
    }
}
