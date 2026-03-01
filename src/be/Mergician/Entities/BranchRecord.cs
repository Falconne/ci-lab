namespace Mergician.Entities;

/// <summary>
///     Represents a branch that the user has recently pushed to,
///     along with its merge request and approval status.
///     HasMergeRequest may be null to indicate that MR status has not yet been resolved.
///     LastUpdated contains the UTC timestamp of the most recent push to the branch.
///     BranchInProjectId is the primary key of the branch record in the database.
/// </summary>
// TODO: make this class be based off `BranchInProjectRecord` and consolidate the equivalent properties.
// Also, move `ProjectNameWithNamespace` up to the base class and rename this to `BranchWithActivity` and
// update any frontend references. Remove `BranchInProjectId` and change the references to use the
// Id property from the base class.
public record BranchRecord(
    string BranchName,
    int ProjectId,
    string ProjectName,
    string ProjectNameWithNamespace,
    bool? HasMergeRequest,
    int? ApprovalsRequired,
    int? ApprovalsGiven,
    DateTimeOffset? LastUpdated = null,
    string? MergeRequestTitle = null,
    string? MergeRequestUrl = null,
    string? ProjectUrl = null,
    List<BranchBuildJob>? BuildJobs = null,
    int? BranchInProjectId = null);