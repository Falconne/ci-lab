using Mergician.Entities;

namespace Mergician.Services;

/// <summary>
///     Calculates the status of a branch's merge request from GitLab's
///     <c>detailed_merge_status</c> field.
/// </summary>
public static class MRStatusCalculator
{
    /// <summary>
    ///     GitLab <c>detailed_merge_status</c> values that represent transient states where
    ///     GitLab is still computing the merge status. These are treated as Waiting.
    /// </summary>
    private static readonly HashSet<string> _transientMergeStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "checking",
        "unchecked",
        "preparing",
        "approvals_syncing",
    };

    /// <summary>
    ///     Computes the MR status and the list of reasons for non-Ready states from
    ///     GitLab's <c>detailed_merge_status</c>.
    /// </summary>
    /// <param name="hasMergeRequest">Whether the branch has an open merge request.</param>
    /// <param name="detailedMergeStatus">The <c>detailed_merge_status</c> value from GitLab.</param>
    /// <returns>The status value from <see cref="MRStatus" /> and a list of reason strings.</returns>
    public static (int Status, List<string> Reasons) Calculate(
        bool hasMergeRequest,
        string? detailedMergeStatus)
    {
        if (!hasMergeRequest)
        {
            return (MRStatus.Blocked, ["No merge request"]);
        }

        if (detailedMergeStatus == "mergeable")
        {
            return (MRStatus.Ready, []);
        }

        if (detailedMergeStatus == "ci_still_running")
        {
            return (MRStatus.Waiting, ["Build running"]);
        }

        if (detailedMergeStatus != null && _transientMergeStatuses.Contains(detailedMergeStatus))
        {
            return (MRStatus.Waiting, ["GitLab is computing merge status"]);
        }

        var reason = detailedMergeStatus != null
            ? FormatDetailedMergeStatus(detailedMergeStatus)
            : "Merge status unknown";

        return (MRStatus.Blocked, [reason]);
    }

    /// <summary>
    ///     Converts a GitLab <c>detailed_merge_status</c> snake_case value into a human-readable
    ///     blocked reason, e.g. "discussions_not_resolved" → "Discussions not resolved".
    /// </summary>
    public static string FormatDetailedMergeStatus(string status)
    {
        var readable = status.Replace('_', ' ');
        return char.ToUpperInvariant(readable[0]) + readable[1..];
    }
}
