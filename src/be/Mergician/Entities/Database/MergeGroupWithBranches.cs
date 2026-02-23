namespace Mergician.Entities.Database;

public class MergeGroupWithBranches
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public DateTimeOffset LastUpdateTime { get; init; }
    public List<BranchWithMergeGroupInfo> Branches { get; init; } = [];
}
