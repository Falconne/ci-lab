namespace Mergician.Entities;

/// <summary>
///     Represents a merge group with all its branches.
///     Sent by the API to the frontend for both the dashboard and merge group detail views.
/// </summary>
public record MergeGroup(
    int MergeGroupId,
    string MergeGroupName,
    List<BranchRecord> Branches);
