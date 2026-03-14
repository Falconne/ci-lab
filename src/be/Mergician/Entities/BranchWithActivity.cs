using Mergician.Entities.Database;

namespace Mergician.Entities;

/// <summary>
///     Represents a branch that the user has recently pushed to,
///     along with its merge request and approval status.
///     HasMergeRequest may be null to indicate that MR status has not yet been resolved.
///     LastUpdated contains the UTC timestamp of the most recent push to the branch.
///     Inherits Id, BranchName, ProjectId, ProjectName, and ProjectNameWithNamespace from
///     <see cref="BranchInProject" />.
/// </summary>
public record BranchWithActivity : BranchInProject
{
    // Populated via Dapper and serialized to the Vue frontend.
    // ReSharper disable UnusedMember.Global
    public bool? HasMergeRequest { get; init; }

    public int? ApprovalsRequired { get; init; }

    public int? ApprovalsGiven { get; init; }

    public DateTimeOffset? LastUpdated { get; init; }

    public string? MergeRequestTitle { get; init; }

    public string? MergeRequestUrl { get; init; }

    public string? ProjectUrl { get; init; }

    public List<BranchBuildJob>? BuildJobs { get; init; }
    // ReSharper restore UnusedMember.Global
}