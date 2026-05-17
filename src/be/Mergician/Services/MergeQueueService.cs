using Mergician.Entities;
using Mergician.Entities.Database;
using Mergician.Services.Database;

namespace Mergician.Services;

/// <summary>
///     Determines queue eligibility for merge groups and updates queue membership accordingly.
///     A merge group is eligible for a queue only when <c>auto_merge</c> is enabled AND no hard
///     blockers remain (only "needs rebase" or running builds are allowed; draft, conflicts, missing
///     approvals, failed pipelines, and external MR blocks are hard blockers that prevent queuing).
/// </summary>
public class MergeQueueService
{
    private readonly IMergeQueueRepository _queueRepository;
    private readonly ILogger<MergeQueueService> _logger;

    public MergeQueueService(IMergeQueueRepository queueRepository, ILogger<MergeQueueService> logger)
    {
        _queueRepository = queueRepository;
        _logger = logger;
    }

    /// <summary>
    ///     Returns true if the merge group is eligible to be placed in a merge queue.
    ///     Eligibility requires:
    ///     <list type="bullet">
    ///         <item><c>auto_merge</c> is enabled.</item>
    ///         <item>Every branch in the group has an open MR.</item>
    ///         <item>Every MR has <c>detailed_merge_status == "ci_still_running"</c> (CI is the only blocker).</item>
    ///     </list>
    /// </summary>
    public bool IsQueueEligible(
        MergeGroup group,
        IReadOnlyList<BranchWithMergeRequest> branchMRDetails)
    {
        if (!group.AutoMerge)
        {
            _logger.LogDebug(
                "MergeQueueService: group '{GroupName}' ({GroupId}) is not eligible — auto_merge={AutoMerge}",
                group.Name,
                group.Id,
                group.AutoMerge);

            return false;
        }

        if (branchMRDetails.Count != group.Branches.Count)
        {
            _logger.LogDebug(
                "MergeQueueService: group '{GroupName}' ({GroupId}) is not eligible — {MrCount} MRs found for {BranchCount} branches",
                group.Name,
                group.Id,
                branchMRDetails.Count,
                group.Branches.Count);

            return false;
        }

        foreach (var (branch, mr) in branchMRDetails)
        {
            if (mr.DetailedMergeStatus != "ci_still_running")
            {
                _logger.LogDebug(
                    "MergeQueueService: group '{GroupName}' ({GroupId}) is not eligible — branch '{BranchName}' detailed_merge_status={Status}",
                    group.Name,
                    group.Id,
                    branch.BranchName,
                    mr.DetailedMergeStatus);

                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Evaluates whether the merge group should be added to or removed from a queue and
    ///     performs the corresponding queue update.  Should be called on every AutoMerge cycle
    ///     for groups with auto settings enabled, after MR details have been fetched.
    /// </summary>
    public void EvaluateAndUpdateQueueMembership(
        MergeGroup group,
        IReadOnlyList<BranchWithMergeRequest> branchMRDetails)
    {
        var eligible = IsQueueEligible(group, branchMRDetails);
        var currentlyQueued = group.QueueId.HasValue;

        if (eligible && !currentlyQueued)
        {
            var projectIds = group.Branches.Select(b => b.ProjectId).ToHashSet();

            _logger.LogInformation(
                "MergeQueueService: adding group '{GroupName}' ({GroupId}) to queue (projects: [{Projects}])",
                group.Name,
                group.Id,
                string.Join(", ", projectIds));

            _queueRepository.AddMergeGroupToQueue(group.Id, projectIds);
        }
        else if (!eligible && currentlyQueued)
        {
            _logger.LogInformation(
                "MergeQueueService: removing group '{GroupName}' ({GroupId}) from queue {QueueId} — no longer eligible",
                group.Name,
                group.Id,
                group.QueueId);

            _queueRepository.RemoveMergeGroupFromQueue(group.Id);
        }
        else if (eligible)
        {
            _logger.LogDebug(
                "MergeQueueService: group '{GroupName}' ({GroupId}) remains in queue {QueueId} at position {Position}",
                group.Name,
                group.Id,
                group.QueueId,
                group.QueuePosition);
        }
        else
        {
            _logger.LogDebug(
                "MergeQueueService: group '{GroupName}' ({GroupId}) is not eligible and not queued — no action",
                group.Name,
                group.Id);
        }
    }
}
