using Mergician.Entities;
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
    ///     Gets or creates a MergeGroup by name. Returns the existing or new group with its associated branches.
    ///     Thread-safe: uses INSERT ON CONFLICT to avoid duplicates from concurrent callers.
    /// </summary>
    MergeGroup GetOrCreateMergeGroup(string name);

    /// <summary>
    ///     Associates a branch with a merge group if not already associated.
    /// </summary>
    void EnsureBranchInMergeGroup(int mergeGroupId, int branchInProjectId);

    /// <summary>
    ///     Associates a user with a merge group if not already associated.
    /// </summary>
    void EnsureUserInMergeGroup(int gitlabUserId, int mergeGroupId);

    /// <summary>
    ///     Updates the last_update_time of a branch record to reflect when it was last pushed.
    /// </summary>
    void UpdateBranchTimestamp(int branchInProjectId, DateTimeOffset lastUpdateTime);

    /// <summary>
    ///     Returns all merge groups that the user is associated with, each containing its branches.
    ///     Ordered by most recently updated merge group first, based on branch timestamps.
    /// </summary>
    List<MergeGroup> GetMergeGroupsForUser(int gitlabUserId);

    /// <summary>
    ///     Returns a specific merge group and its branches, or null when not found.
    /// </summary>
    MergeGroup? GetMergeGroup(int mergeGroupId);

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
    List<MergeGroupBase> GetEmptyMergeGroups();

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

    /// <summary>
    ///     Persists the resolved activity details (MR data, approvals, build jobs) for a branch.
    ///     Replaces any previously stored build jobs for this branch atomically.
    /// </summary>
    void UpdateBranchDetails(
        int branchInProjectId,
        bool hasMergeRequest,
        string? mergeRequestTitle,
        string? mergeRequestUrl,
        string? projectUrl,
        int? approvalsRequired,
        int? approvalsGiven,
        List<BranchBuildJob> buildJobs);
}