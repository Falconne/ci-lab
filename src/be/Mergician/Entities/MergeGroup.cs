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
    public MergeGroup(int id, string name, List<BranchRecord> branches)
    {
        Id = id;
        Name = name;
        Branches = branches;
    }

    public List<BranchRecord> Branches { get; init; }

    // TODO: Remove LastUpdateTime and its usages as we don't care about the merge group's last update
    // time in the backend. We only care about it in the frontend for sorting the more recently
    // updated merge groups to the top. Have the frontend calculate this by looking at the branches
    // in the merge group. Also, the frontend need not display a "merge group last updated time". Just
    // showing the branches' last updated times is enough.
    public DateTimeOffset? LastUpdateTime => Branches.Count > 0 ? Branches.Max(b => b.LastUpdated) : null;
}