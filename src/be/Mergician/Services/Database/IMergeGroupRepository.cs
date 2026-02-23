using Mergician.Entities.Database;

namespace Mergician.Services.Database;

/// <summary>
///     Repository interface for merge-group and branch database operations.
/// </summary>
public interface IMergeGroupRepository
{
    /// <summary>
    ///     Gets or creates a BranchInProject record. Returns the existing or new record.
    ///     Thread-safe: uses INSERT ON CONFLICT to avoid duplicates from concurrent callers.
    /// </summary>
    BranchInProjectRecord GetOrCreateBranchRecord(string branchName, int projectId, string projectName);

    /// <summary>
    ///     Gets or creates a MergeGroup by name. Returns the existing or new record.
    ///     Thread-safe: uses INSERT ON CONFLICT to avoid duplicates from concurrent callers.
    /// </summary>
    MergeGroupRecord GetOrCreateMergeGroup(string name);

    /// <summary>
    ///     Associates a branch with a merge group if not already associated.
    /// </summary>
    void EnsureBranchInMergeGroup(int mergeGroupId, int branchInProjectId);

    /// <summary>
    ///     Associates a user with a merge group if not already associated.
    /// </summary>
    void EnsureUserInMergeGroup(int gitlabUserId, int mergeGroupId);

    /// <summary>
    ///     Updates the LastUpdateTime of a merge group.
    /// </summary>
    void UpdateMergeGroupTimestamp(int mergeGroupId, DateTimeOffset lastUpdateTime);

    /// <summary>
    ///     Returns all branches in merge groups that the user is associated with,
    ///     filtered by merge groups whose LastUpdateTime is within the given timespan.
    ///     Results are ordered by merge group LastUpdateTime descending (most recent first).
    /// </summary>
    List<BranchWithMergeGroupInfo> GetUserBranches(int gitlabUserId, DateTimeOffset since);

    /// <summary>
    ///     Returns branches for a specific merge group that the user is associated with.
    /// </summary>
    List<BranchWithMergeGroupInfo> GetMergeGroup(int gitlabUserId, int mergeGroupId);

    /// <summary>
    ///     Deletes a branch record and its references in branches_in_merge_group.
    /// </summary>
    void DeleteBranch(int branchInProjectId);

    /// <summary>
    ///     Deletes a merge group and all its references (branches_in_merge_group and users_in_merge_groups).
    /// </summary>
    void DeleteMergeGroup(int mergeGroupId);

    /// <summary>
    ///     Returns merge groups that have no branches left.
    /// </summary>
    List<MergeGroupRecord> GetEmptyMergeGroups();

    /// <summary>
    ///     Returns all tracked branches across all merge groups.
    ///     Used by the cleanup service.
    /// </summary>
    List<BranchInProjectRecord> GetAllBranches();

    /// <summary>
    ///     Returns a tracked branch record for the branch and project, or null if it is not tracked.
    /// </summary>
    BranchInProjectRecord? GetBranchRecord(string branchName, int projectId);

    /// <summary>
    ///     Returns the merge group IDs that a branch belongs to.
    /// </summary>
    List<int> GetMergeGroupIdsForBranch(int branchInProjectId);
}