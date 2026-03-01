using Mergician.Entities.Database;

namespace Mergician.Entities;

/// <summary>
///     Represents a merge group with all its branches.
///     Sent by the API to the frontend for both the dashboard and merge group detail views.
/// </summary>
public class MergeGroup : MergeGroupBase
{
    public MergeGroup(int id, string name, List<BranchWithActivity> branches)
    {
        Id = id;
        Name = name;
        Branches = branches;
    }

    public List<BranchWithActivity> Branches { get; init; }
}