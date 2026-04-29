using Mergician.Entities;

namespace Mergician.Services;

/// <summary>
///     Calculates the status of a branch's merge request from its activity data.
///     Used by the sync service to store status in the database and by the auto-merge service
///     to determine readiness for merging.
/// </summary>
public static class MRStatusCalculator
{
    /// <summary>
    ///     GitLab <c>detailed_merge_status</c> values that we already handle through other parameters,
    ///     or that represent transient states where GitLab is still computing the merge status.
    ///     Any value outside this set triggers a "Blocked for unknown reason" result.
    /// </summary>
    private static readonly HashSet<string> _handledOrTransientMergeStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        // GitLab says it is mergeable — all good.
        "mergeable",
        // GitLab is still computing — do not treat as blocked.
        "checking",
        "unchecked",
        "preparing",
        "approvals_syncing",
        // Handled by the needsRebase parameter.
        "need_rebase",
        // Handled by the hasConflicts parameter.
        "conflict",
        // Handled by the isDraft parameter (or via explicit check in auto-merge).
        "draft_status",
        // Handled by the approvalsRequired/approvalsGiven parameters.
        "not_approved",
        // Handled by the buildJobs parameter (failed/running jobs are already surfaced).
        "ci_must_pass",
        "ci_still_running",
        "pipeline_must_succeed",
    };

    /// <summary>
    ///     Computes the MR status and the list of reasons for non-Ready states.
    /// </summary>
    /// <param name="hasMergeRequest">Whether the branch has an open merge request.</param>
    /// <param name="isDraft">Whether the merge request is a draft/WIP (not ready to merge).</param>
    /// <param name="approvalsRequired">Number of required approvals, or null if unknown.</param>
    /// <param name="approvalsGiven">Number of approvals given, or null if unknown.</param>
    /// <param name="buildJobs">Build jobs from the latest pipeline.</param>
    /// <param name="needsRebase">Whether the branch needs to be rebased.</param>
    /// <param name="rebaseInProgress">Whether a rebase is currently in progress.</param>
    /// <param name="hasConflicts">Whether the branch has merge conflicts.</param>
    /// <param name="detailedMergeStatus">
    ///     The <c>detailed_merge_status</c> value from GitLab. When set to a value that is not
    ///     already handled by the other parameters (and is not a transient state), the MR is
    ///     marked Blocked and the GitLab status value is surfaced as the reason.
    /// </param>
    /// <returns>The status value from <see cref="MRStatus" /> and a list of reason strings.</returns>
    public static (int Status, List<string> Reasons) Calculate(
        bool hasMergeRequest,
        bool isDraft,
        int? approvalsRequired,
        int? approvalsGiven,
        IReadOnlyList<BranchBuildJob> buildJobs,
        bool? needsRebase,
        bool? rebaseInProgress,
        bool hasConflicts = false,
        string? detailedMergeStatus = null)
    {
        if (!hasMergeRequest)
        {
            return (MRStatus.Blocked, ["No merge request"]);
        }

        var blockedReasons = new List<string>();
        var waitingReasons = new List<string>();

        foreach (var job in buildJobs)
        {
            var status = job.Status.ToLowerInvariant();
            if (status is "failed" or "failure")
            {
                blockedReasons.Add($"Build failed: {job.Name}");
            }
            else if (status is "running" or "pending")
            {
                waitingReasons.Add($"Build running: {job.Name}");
            }
        }

        if (hasConflicts)
        {
            blockedReasons.Add("Has merge conflicts");
        }

        if (isDraft)
        {
            waitingReasons.Add("Draft merge request");
        }

        if (approvalsRequired > 0)
        {
            var given = approvalsGiven ?? 0;
            if (given < approvalsRequired.Value)
            {
                var needed = approvalsRequired.Value - given;
                waitingReasons.Add($"Needs {needed} more approval{(needed == 1 ? "" : "s")}");
            }
        }

        if (needsRebase == true)
        {
            waitingReasons.Add("Needs rebase");
        }

        if (rebaseInProgress == true)
        {
            waitingReasons.Add("Rebase in progress");
        }

        // If GitLab reports a blocking state that our other checks do not already cover,
        // surface the GitLab status as the reason so the user knows what to fix.
        if (!string.IsNullOrEmpty(detailedMergeStatus)
            && !_handledOrTransientMergeStatuses.Contains(detailedMergeStatus)
            && blockedReasons.Count == 0)
        {
            blockedReasons.Add(FormatDetailedMergeStatus(detailedMergeStatus));
        }

        if (blockedReasons.Count > 0)
        {
            return (MRStatus.Blocked, blockedReasons);
        }

        if (waitingReasons.Count > 0)
        {
            return (MRStatus.Waiting, waitingReasons);
        }

        return (MRStatus.Ready, []);
    }

    /// <summary>
    ///     Converts a GitLab <c>detailed_merge_status</c> snake_case value into a human-readable
    ///     blocked reason, e.g. "discussions_not_resolved" → "Discussions not resolved".
    /// </summary>
    private static string FormatDetailedMergeStatus(string status)
    {
        var readable = status.Replace('_', ' ');
        return char.ToUpperInvariant(readable[0]) + readable[1..];
    }
}
