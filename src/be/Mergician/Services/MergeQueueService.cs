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
    ///         <item>No MR is a draft.</item>
    ///         <item>No MR has merge conflicts.</item>
    ///         <item>No MR has missing approvals.</item>
    ///         <item>No MR has a failed pipeline.</item>
    ///         <item>No MR has external (non-intra-group) blockers.</item>
    ///     </list>
    ///     "Needs rebase" and running/pending builds do NOT disqualify a group from the queue.
    /// </summary>
    public bool IsQueueEligible(
        MergeGroup group,
        IReadOnlyList<BranchWithMergeRequest> branchMRDetails,
        IReadOnlySet<int> intraGroupBlockedBranchIds)
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
            if (mr.Draft)
            {
                _logger.LogDebug(
                    "MergeQueueService: group '{GroupName}' ({GroupId}) is not eligible — branch '{BranchName}' MR is a draft",
                    group.Name,
                    group.Id,
                    branch.BranchName);

                return false;
            }

            if (mr.HasConflicts)
            {
                _logger.LogDebug(
                    "MergeQueueService: group '{GroupName}' ({GroupId}) is not eligible — branch '{BranchName}' has conflicts",
                    group.Name,
                    group.Id,
                    branch.BranchName);

                return false;
            }

            // Pipeline failure is a hard blocker. Running/pending pipelines are not.
            if (mr.DetailedMergeStatus == "ci_must_pass"
                || mr.DetailedMergeStatus == "pipeline_must_succeed")
            {
                _logger.LogDebug(
                    "MergeQueueService: group '{GroupName}' ({GroupId}) is not eligible — branch '{BranchName}' has a failed pipeline ({Status})",
                    group.Name,
                    group.Id,
                    branch.BranchName,
                    mr.DetailedMergeStatus);

                return false;
            }

            // External MR blockers are a hard blocker; intra-group ones are fine.
            if (mr.DetailedMergeStatus == "blocked_status"
                && !intraGroupBlockedBranchIds.Contains(branch.Id))
            {
                _logger.LogDebug(
                    "MergeQueueService: group '{GroupName}' ({GroupId}) is not eligible — branch '{BranchName}' has external MR blockers",
                    group.Name,
                    group.Id,
                    branch.BranchName);

                return false;
            }
        }

        // Check for missing approvals by examining all MRs.  We cannot easily get the approval
        // counts here without additional API calls, but GitLab surfaced "not_approved" in
        // detailed_merge_status, which we check instead.
        if (branchMRDetails.Any(bm => bm.MergeRequest.DetailedMergeStatus == "not_approved"))
        {
            _logger.LogDebug(
                "MergeQueueService: group '{GroupName}' ({GroupId}) is not eligible — at least one MR is not approved",
                group.Name,
                group.Id);

            return false;
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
        IReadOnlyList<BranchWithMergeRequest> branchMRDetails,
        IReadOnlySet<int> intraGroupBlockedBranchIds)
    {
        var eligible = IsQueueEligible(group, branchMRDetails, intraGroupBlockedBranchIds);
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
