namespace Mergician.Entities;

/// <summary>
///     Response from the merge group poll endpoint containing the full current state
///     of all branches in the given merge group.
///     The frontend reconciles this against its current display state.
/// </summary>
public record MergeGroupResponse(
    int MergeGroupId,
    string MergeGroupName,
    List<BranchActivity> Branches);
