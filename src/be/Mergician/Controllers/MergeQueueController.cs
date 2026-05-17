using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

/// <summary>
///     API for viewing and managing merge queues.
/// </summary>
[Authorize]
[ApiController]
[Route("api/merge-queues")]
public class MergeQueueController : ControllerBase
{
    private readonly ILogger<MergeQueueController> _logger;

    private readonly IMergeGroupRepository _mergeGroupRepository;

    private readonly IMergeQueueRepository _mergeQueueRepository;

    public MergeQueueController(
        IMergeQueueRepository mergeQueueRepository,
        IMergeGroupRepository mergeGroupRepository,
        ILogger<MergeQueueController> logger)
    {
        _mergeQueueRepository = mergeQueueRepository;
        _mergeGroupRepository = mergeGroupRepository;
        _logger = logger;
    }

    /// <summary>
    ///     Returns summary information for all active queues, including whether the current user
    ///     has any tracked merge groups in each queue.
    /// </summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<MergeQueueSummary>> GetAllQueues()
    {
        var currentUser = HttpContext.GetGitLabUser();
        var summaries = _mergeQueueRepository.GetAllQueueSummaries(currentUser.UserId);

        _logger.LogDebug(
            "MergeQueueController: returning {Count} queue summaries for user {UserId}",
            summaries.Count,
            currentUser.UserId);

        return Ok(summaries);
    }

    /// <summary>
    ///     Returns the ordered list of merge groups in a specific queue.
    ///     Returns 404 if the queue does not exist.
    /// </summary>
    [HttpGet("{queueId:int}")]
    public ActionResult<IReadOnlyList<MergeGroup>> GetQueue(int queueId)
    {
        var groups = _mergeGroupRepository.GetMergeGroupsForQueue(queueId);

        if (groups.Count == 0)
        {
            // Distinguish between an empty queue and a non-existent one.
            var allQueues = _mergeQueueRepository.GetAllQueues();
            if (allQueues.All(q => q.QueueId != queueId))
            {
                _logger.LogDebug("MergeQueueController: queue {QueueId} not found", queueId);
                return NotFound(new ErrorResponse($"Queue {queueId} not found"));
            }
        }

        _logger.LogDebug(
            "MergeQueueController: returning {Count} merge groups for queue {QueueId}",
            groups.Count,
            queueId);

        return Ok(groups);
    }

    /// <summary>
    ///     Reorders the merge groups within a queue.
    ///     The request body is the desired ordered list of merge group IDs.
    ///     IDs not in the queue are silently ignored; groups not in the list keep their relative order.
    /// </summary>
    [HttpPut("{queueId:int}/reorder")]
    public ActionResult Reorder(int queueId, [FromBody] ReorderQueueRequest request)
    {
        _logger.LogInformation(
            "MergeQueueController: reordering queue {QueueId}: [{Order}]",
            queueId,
            string.Join(", ", request.MergeGroupIds));

        _mergeQueueRepository.ReorderQueue(queueId, request.MergeGroupIds);
        return NoContent();
    }
}

/// <summary>
///     Request body for the reorder endpoint.
/// </summary>
public record ReorderQueueRequest(IReadOnlyList<int> MergeGroupIds);
