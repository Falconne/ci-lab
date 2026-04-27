using Mergician.Entities;
using Mergician.Entities.Database;

namespace Mergician.Services.Database;

/// <summary>
///     Encapsulates all activity data to persist for a branch in one call.
///     Passed to <see cref="IMergeGroupRepository.UpdateBranchDetails" />.
/// </summary>
public record BranchDetailsUpdate(
    bool HasMergeRequest,
    string? MergeRequestTitle,
    string? MergeRequestUrl,
    string? ProjectUrl,
    int? ApprovalsRequired,
    int? ApprovalsGiven,
    List<BranchBuildJob> BuildJobs,
    bool? NeedsRebase,
    DateTimeOffset? LastCommitTime,
    string? LastCommitMessage,
    int MrStatus,
    string? MrStatusReasons);

/// <summary>
///     Repository interface for merge-group and branch database operations.
/// </summary>
public interface IMergeGroupRepository
{
    /// <summary>
    ///     Gets or creates a BranchInProject record for the given branch in the given project.
    ///     Returns the existing or new record.
    ///     Thread-safe: uses INSERT ON CONFLICT to avoid duplicates from concurrent callers.
    /// </summary>
    BranchInProject GetOrCreateBranchRecord(string branchName, GitLabProject project);

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
    ///     Returns all merge groups that the user is associated with, each containing its branches.
    ///     Ordered by most recently updated merge group first, based on branch timestamps.
    /// </summary>
    List<MergeGroup> GetMergeGroupsForUser(int gitlabUserId);

    /// <summary>
    ///     Returns a specific merge group and its branches, or null when not found.
    /// </summary>
    MergeGroup? GetMergeGroup(int mergeGroupId);

    /// <summary>
    ///     Removes a branch record and its references in branches_in_merge_group.
    /// </summary>
    void RemoveBranch(int branchInProjectId);

    /// <summary>
    ///     Removes a merge group and all its references (branches_in_merge_group and users_in_merge_groups).
    /// </summary>
    void RemoveMergeGroup(int mergeGroupId);

    /// <summary>
    ///     Removes all merge groups that have no branches. More efficient than calling
    ///     <see cref="GetEmptyMergeGroups" /> and <see cref="RemoveMergeGroup" /> in a loop.
    /// </summary>
    void CleanupEmptyMergeGroups();

    /// <summary>
    ///     Returns merge groups that have no branches left.
    /// </summary>
    List<MergeGroupBase> GetEmptyMergeGroups();

    /// <summary>
    ///     Returns all tracked branches across all merge groups.
    ///     Used by the cleanup service.
    /// </summary>
    List<BranchInProject> GetAllBranches();

    /// <summary>
    ///     Returns the merge group IDs that a branch belongs to.
    /// </summary>
    List<int> GetMergeGroupIdsForBranch(int branchInProjectId);

    /// <summary>
    ///     Persists the resolved activity details (MR data, approvals, build jobs) for a branch.
    ///     Replaces any previously stored build jobs for this branch atomically.
    /// </summary>
    void UpdateBranchDetails(int branchInProjectId, BranchDetailsUpdate update);

    /// <summary>
    ///     Updates the auto merge and auto rebase settings for a merge group.
    /// </summary>
    int UpdateAutoMergeSettings(int mergeGroupId, bool autoMerge, bool autoRebase);

    /// <summary>
    ///     Returns all merge groups that have auto_merge or auto_rebase enabled,
    ///     each containing its branches. Used by the AutoMergeService.
    /// </summary>
    List<MergeGroup> GetMergeGroupsWithAutoSettings();

    /// <summary>
    ///     Sets or clears the auto merge warning text for a merge group.
    /// </summary>
    void UpdateAutoMergeWarning(int mergeGroupId, string? warning);

    /// <summary>
    ///     Returns true if the user is associated with the specified merge group.
    /// </summary>
    bool IsUserInMergeGroup(int gitlabUserId, int mergeGroupId);

    /// <summary>
    ///     Removes the association between a user and a merge group.
    /// </summary>
    void RemoveUserFromMergeGroup(int gitlabUserId, int mergeGroupId);

    /// <summary>
    ///     Finds a merge group that contains the given branch name in the given project.
    ///     Returns null if no merge group tracks that branch in that project.
    /// </summary>
    MergeGroup? FindMergeGroupByBranch(string branchName, int projectId);
}