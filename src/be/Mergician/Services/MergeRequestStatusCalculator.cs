using Mergician.Entities;

namespace Mergician.Services;

/// <summary>
///     Calculates the status of a branch's merge request from GitLab's
///     <c>detailed_merge_status</c> field.
/// </summary>
public static class MergeRequestStatusCalculator
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
        "approvals_syncing"
    };

    /// <summary>
    ///     GitLab <c>detailed_merge_status</c> values that allow a merge group to be placed in
    ///     a merge queue.  <c>mergeable</c> and <c>need_rebase</c> are fully ready;
    ///     <c>ci_still_running</c> is permitted so the group enters the queue while CI finishes.
    /// </summary>
    private static readonly HashSet<string> _mergeableStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ci_still_running",
        "mergeable",
        "need_rebase"
    };

    public static bool IsTransientStatus(string status)
    {
        return _transientMergeStatuses.Contains(status);
    }

    /// <summary>
    ///     Returns true if the MR's <c>detailed_merge_status</c> allows the merge group to enter
    ///     the merge queue (i.e. no hard blockers remain).
    /// </summary>
    public static bool IsMergeableStatus(string status)
    {
        return _mergeableStatuses.Contains(status);
    }

    /// <summary>
    ///     Computes the MR status and the top reason for any non-Ready state from
    ///     GitLab's <c>detailed_merge_status</c>.
    /// </summary>
    /// <param name="detailedMergeStatus">The <c>detailed_merge_status</c> value from GitLab.</param>
    /// <returns>The status value from <see cref="MRStatus" /> and associated reason.</returns>
    public static (int Status, string? Reasons) Calculate(string? detailedMergeStatus)
    {
        if (detailedMergeStatus == null)
        {
            return (MRStatus.Blocked, "No merge request");
        }

        if (detailedMergeStatus == "mergeable")
        {
            return (MRStatus.Ready, null);
        }

        if (detailedMergeStatus == "ci_still_running")
        {
            return (MRStatus.Waiting, "Build running");
        }

        if (_transientMergeStatuses.Contains(detailedMergeStatus))
        {
            return (MRStatus.Waiting, "GitLab is computing merge status");
        }

        var reason = FormatDetailedMergeStatus(detailedMergeStatus);

        return (MRStatus.Blocked, reason);
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