namespace Mergician.Entities;

/// <summary>
///     Represents a merge group with all its branches.
///     Sent by the API to the frontend for both the dashboard and merge group detail views.
///     LastUpdateTime contains the UTC timestamp of the most recent branch push in this group.
/// </summary>
public record MergeGroup(
    int MergeGroupId,
    string MergeGroupName,
    DateTimeOffset LastUpdateTime,
    List<BranchRecord> Branches);
