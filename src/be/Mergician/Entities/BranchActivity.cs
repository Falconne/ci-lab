namespace Mergician.Entities;

/// <summary>
/// Represents a branch that the user has recently pushed to,
/// along with its merge request and approval status.
/// </summary>
public record BranchActivity(
    string BranchName,
    int ProjectId,
    string ProjectName,
    bool HasMergeRequest,
    int? ApprovalsRequired,
    int? ApprovalsGiven);
