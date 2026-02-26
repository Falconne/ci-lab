namespace Mergician.Entities;

/// <summary>
/// Represents a branch that the user has recently pushed to,
/// along with its merge request and approval status.
/// When streamed progressively, HasMergeRequest may be null
/// to indicate that MR status has not yet been resolved.
/// LastUpdated contains the UTC timestamp of the most recent push to the branch.
/// MergeGroupId identifies the merge group this branch belongs to in the database.
/// BranchInProjectId is the primary key of the branch record in the database.
/// </summary>
public record BranchActivity(
    string BranchName,
    int ProjectId,
    string ProjectName,
    string ProjectNameWithNamespace,
    bool? HasMergeRequest,
    int? ApprovalsRequired,
    int? ApprovalsGiven,
    DateTimeOffset? LastUpdated = null,
    int? MergeGroupId = null,
    string? MergeRequestTitle = null,
    string? MergeRequestUrl = null,
    string? ProjectUrl = null,
    List<BranchBuildJob>? BuildJobs = null,
    int? BranchInProjectId = null);
