namespace Mergician.Entities;

/// <summary>
///     Represents one build/external job status for a branch.
/// </summary>
public record BranchBuildJob(
    string Name,
    string Status,
    string? Url = null);