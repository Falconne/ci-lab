namespace Mergician.Entities;

/// <summary>
///     Pairs a branch that is being tracked for auto merge/rebase with its open merge request detail.
/// </summary>
public record BranchWithMergeRequest(BranchWithActivity Branch, GitLabDetailedMergeRequest MergeRequest);
