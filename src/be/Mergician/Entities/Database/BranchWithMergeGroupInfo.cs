namespace Mergician.Entities.Database;

/// <summary>
/// Represents a branch along with its merge group information,
/// returned from joined queries.
/// </summary>
public class BranchWithMergeGroupInfo
{
    public int BranchInProjectId { get; set; }
    public string BranchName { get; set; } = "";
    public int ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public int MergeGroupId { get; set; }
    public string MergeGroupName { get; set; } = "";
    public DateTimeOffset LastUpdateTime { get; set; }
}
