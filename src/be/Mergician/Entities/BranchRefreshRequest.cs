namespace Mergician.Entities;

/// <summary>
///     Request to refresh the MR/approval status for a specific branch in a project.
/// </summary>
public record BranchRefreshRequest(string BranchName, int ProjectId, DateTimeOffset? LastUpdated = null);
