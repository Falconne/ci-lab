using Mergician.Entities;

namespace Mergician.Services;

/// <summary>
///     Calculates the status of a branch's merge request from its activity data.
///     Used by the sync service to store status in the database and by the auto-merge service
///     to determine readiness for merging.
/// </summary>
public static class MrStatusCalculator
{
    /// <summary>
    ///     Computes the MR status and the list of reasons for non-Ready states.
    /// </summary>
    /// <param name="hasMergeRequest">Whether the branch has an open merge request.</param>
    /// <param name="approvalsRequired">Number of required approvals, or null if unknown.</param>
    /// <param name="approvalsGiven">Number of approvals given, or null if unknown.</param>
    /// <param name="buildJobs">Build jobs from the latest pipeline.</param>
    /// <param name="needsRebase">Whether the branch needs to be rebased.</param>
    /// <param name="rebaseInProgress">Whether a rebase is currently in progress.</param>
    /// <returns>The status value from <see cref="MrStatus" /> and a list of reason strings.</returns>
    public static (int Status, List<string> Reasons) Calculate(
        bool hasMergeRequest,
        int? approvalsRequired,
        int? approvalsGiven,
        IReadOnlyList<BranchBuildJob> buildJobs,
        bool? needsRebase,
        bool? rebaseInProgress)
    {
        if (!hasMergeRequest)
        {
            return (MrStatus.Blocked, ["No merge request"]);
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

        if (approvalsRequired > 0)
        {
            var given = approvalsGiven ?? 0;
            if (given < approvalsRequired.Value)
            {
                var needed = approvalsRequired.Value - given;
                blockedReasons.Add($"Needs {needed} more approval{(needed == 1 ? "" : "s")}");
            }
        }

        if (needsRebase == true)
        {
            blockedReasons.Add("Needs rebase");
        }

        if (rebaseInProgress == true)
        {
            waitingReasons.Add("Rebase in progress");
        }

        if (blockedReasons.Count > 0)
        {
            return (MrStatus.Blocked, blockedReasons);
        }

        if (waitingReasons.Count > 0)
        {
            return (MrStatus.Waiting, waitingReasons);
        }

        return (MrStatus.Ready, []);
    }
}
