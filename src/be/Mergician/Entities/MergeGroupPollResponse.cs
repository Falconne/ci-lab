namespace Mergician.Entities;

/// <summary>
///     Response from the merge group poll endpoint containing the diff between
///     the frontend's currently displayed branches and the database state for this merge group.
/// </summary>
public record MergeGroupPollResponse(
    int MergeGroupId,
    string MergeGroupName,
    List<BranchActivity> Added,
    List<int> Removed);
