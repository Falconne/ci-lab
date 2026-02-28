using Mergician.Entities.Database;

namespace Mergician.Entities;

/// <summary>
///     Represents a merge group with all its branches.
///     Sent by the API to the frontend for both the dashboard and merge group detail views.
///     LastUpdateTime contains the UTC timestamp of the most recent branch push in this group,
///     computed as the maximum LastUpdated across all branches.
/// </summary>
public class MergeGroup : MergeGroupBase
{
    public List<BranchRecord> Branches { get; init; }

    public DateTimeOffset? LastUpdateTime => Branches.Count > 0 ? Branches.Max(b => b.LastUpdated) : null;

    public MergeGroup(int id, string name, List<BranchRecord> branches)
    {
        Id = id;
        Name = name;
        Branches = branches;
    }
}
