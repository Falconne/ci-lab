namespace Mergician.Entities;

/// <summary>
///     Represents a merge group with all its branches.
///     Sent by the API to the frontend for both the dashboard and merge group detail views.
///     LastUpdateTime contains the UTC timestamp of the most recent branch push in this group.
/// </summary>
public record MergeGroup(
    int MergeGroupId,
    string MergeGroupName,
    // TODO: Remove LastUpdateTime property from here and from the merge group table in the database. Only record the last update time against each branch row. The LastUpdateTime for the merge group is the latest LastUpdateTime from the Branches list.
    DateTimeOffset LastUpdateTime,
    List<BranchRecord> Branches);