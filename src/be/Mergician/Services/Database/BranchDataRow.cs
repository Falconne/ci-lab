using Mergician.Entities;

namespace Mergician.Services.Database;

/// <summary>
///     Internal Dapper mapping class for SQL queries that join branch_in_project
///     with merge_group. Not exposed outside the repository layer.
/// </summary>
internal sealed class BranchDataRow
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
