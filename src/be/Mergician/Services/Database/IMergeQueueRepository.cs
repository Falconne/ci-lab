using Mergician.Entities;
using Mergician.Entities.Database;

namespace Mergician.Services.Database;

/// <summary>
///     Summary data for a single entry in a merge queue, returned by <see cref="IMergeQueueRepository" />.
/// </summary>
public record MergeQueueEntryInfo(int QueueId, int MergeGroupId, int Position);

/// <summary>
///     Summary of a merge queue returned by <see cref="IMergeQueueRepository.GetAllQueues" />.
/// </summary>
public record MergeQueueInfo(int QueueId, IReadOnlyList<int> ProjectIds, IReadOnlyList<MergeQueueEntryInfo> Entries);

/// <summary>
///     Repository interface for merge-queue management.
///     Queues are identified by the set of GitLab project IDs they cover.
///     Two merge groups that share at least one project must be on the same queue to avoid
///     redundant CI builds when merges trigger rebases across the queue.
/// </summary>
public interface IMergeQueueRepository
{
    /// <summary>
    ///     Returns the queue entry for a merge group, or null if the group is not in any queue.
    /// </summary>
    MergeQueueEntryInfo? GetQueueEntry(int mergeGroupId);

    /// <summary>
    ///     Returns all active queues with their project keys and ordered entries.
    /// </summary>
    IReadOnlyList<MergeQueueInfo> GetAllQueues();

    /// <summary>
    ///     Adds a merge group to an appropriate queue based on its project IDs:
    ///     <list type="bullet">
    ///         <item>If no queue shares any project → creates a new queue.</item>
    ///         <item>If one queue shares projects → appends to that queue and unions project keys.</item>
    ///         <item>If multiple queues share projects → interleaves (zips) all of them into one,
    ///         appends the new group, and deletes the old queues.</item>
    ///     </list>
    ///     No-ops if the merge group is already in a queue.
    /// </summary>
    void AddMergeGroupToQueue(int mergeGroupId, IReadOnlyCollection<int> projectIds);

    /// <summary>
    ///     Removes a merge group from its queue (if any), resequences remaining positions, and
    ///     splits the queue into independent queues if the remaining entries no longer share
    ///     common projects.
    /// </summary>
    void RemoveMergeGroupFromQueue(int mergeGroupId);

    /// <summary>
    ///     Checks whether the given queue can be split (e.g. after a merge group was removed)
    ///     and performs the split if so.  This is a no-op if the queue does not exist or cannot
    ///     be split.
    /// </summary>
    void CheckAndSplitQueue(int queueId);

    /// <summary>
    ///     Reorders entries within a queue.  Any merge group IDs in the list that are not
    ///     currently in the queue are ignored.  Groups in the queue but not in the list keep
    ///     their relative order after the listed groups.
    /// </summary>
    void ReorderQueue(int queueId, IReadOnlyList<int> orderedMergeGroupIds);

    /// <summary>
    ///     Returns summary info for all queues, including project display names, entry count,
    ///     and whether the specified user has any tracked merge groups in each queue.
    /// </summary>
    IReadOnlyList<MergeQueueSummary> GetAllQueueSummaries(int userId);
}
