using Mergician.Entities;

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

    // Persisted activity details, null until populated by the background sync thread
    public bool? HasMergeRequest { get; set; }
    public string? MergeRequestTitle { get; set; }
    public string? MergeRequestUrl { get; set; }
    public string? ProjectUrl { get; set; }
    public int? ApprovalsRequired { get; set; }
    public int? ApprovalsGiven { get; set; }

    // Build jobs, loaded separately and attached after the main query
    public List<BranchBuildJob> BuildJobs { get; set; } = [];
}
