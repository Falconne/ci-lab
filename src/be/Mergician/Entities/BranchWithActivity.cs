using System.Text.Json;
using System.Text.Json.Serialization;
using Mergician.Entities.Database;
using Util;

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

    public bool? NeedsRebase { get; init; }

    public List<BranchBuildJob>? BuildJobs { get; init; }

    /// <summary>Backend-computed status code. 0=Loading, 1=Blocked, 2=Waiting, 3=Ready.</summary>
    public int MrStatus { get; init; }

    /// <summary>Raw JSON from the database; deserialized by <see cref="MrStatusReasons" />.</summary>
    [JsonIgnore]
    public string? MrStatusReasonsJson { get; init; }

    /// <summary>
    ///     Reasons the MR is not in Ready state, computed from the stored JSON.
    ///     Null when Ready or when data has not been fetched yet.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    public IReadOnlyList<string>? MrStatusReasons =>
        MrStatusReasonsJson.IsNotEmpty()
            ? JsonSerializer.Deserialize<IReadOnlyList<string>>(MrStatusReasonsJson!)
            : null;
    // ReSharper restore UnusedMember.Global
}