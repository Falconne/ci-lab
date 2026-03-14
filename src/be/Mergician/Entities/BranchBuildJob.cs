namespace Mergician.Entities;

/// <summary>
///     Represents one build job status for a branch.
/// </summary>
public record BranchBuildJob(
    string Name,
    string Status,
    string? Url = null);