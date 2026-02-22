namespace Mergician.Entities;

/// <summary>
///     Details payload for one merge group.
/// </summary>
public record MergeGroupDetailsResponse(
    int MergeGroupId,
    string MergeGroupName,
    List<BranchActivity> Activities);
