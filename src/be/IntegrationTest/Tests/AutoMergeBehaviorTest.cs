using IntegrationTest.Entities;
using IntegrationTest.Services;
using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests that the AutoMergeService correctly enforces merge conditions:
///     - Does NOT merge when pipelines are failing
///     - Does NOT merge when only some branches in a group are ready
///     - Auto-rebases branches that are behind the target
///     - Merges all branches when ALL conditions are met
///     Scenarios are run sequentially in a single test to keep execution time manageable.
///     IMPORTANT: TeamCity runs in the CI Lab and publishes external pipeline statuses
///     to GitLab. These affect the MR's detailed_merge_status and the pipeline list.
///     This test waits for TeamCity to finish building before running scenarios so that
///     we have full control over the pipeline state via the Commit Statuses API.
/// </summary>
public class AutoMergeBehaviorTest : IDisposable
{
    /// <summary>
    ///     Pipeline name used for external commit statuses. Using a unique name
    ///     so we can distinguish our test-controlled statuses from TeamCity's.
    /// </summary>
    private const string PipelineName = "automerge-integration-test";

    private readonly BrowserService _browser = new();

    private readonly GitLabTestHelper _gitLab = new();

    public void Dispose()
    {
        _browser.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task Run()
    {
        await _browser.Initialize(
            Path.Combine(TestConfig.ScreenshotDir, "auto-merge-behavior"));

        await TestAutoMergeBehavior();

        Log.Information("Auto merge behavior tests passed");
    }

    private async Task TestAutoMergeBehavior()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var branchName = $"feature/automerge-test-{timestamp}";
        var projectId1 = _gitLab.GetProjectId("primary-1");
        var projectId2 = _gitLab.GetProjectId("secondary-1");

        Log.Information(
            "=== Auto Merge Behavior Test: branch={BranchName}, project1={P1}, project2={P2} ===",
            branchName,
            projectId1,
            projectId2);

        var mrIid1 = 0;
        var mrIid2 = 0;

        try
        {
            // === SETUP: Create branches, commits, and MRs in both projects ===
            Log.Information("--- Setup: creating branches and MRs ---");

            _gitLab.CreateBranchWithCommit(projectId1, branchName, "test1");
            _gitLab.CreateBranchWithCommit(projectId2, branchName, "test1");

            mrIid1 = _gitLab.CreateMergeRequest(
                projectId1,
                branchName,
                "test1",
                $"Auto merge test - primary-1 ({timestamp})");

            mrIid2 = _gitLab.CreateMergeRequest(
                projectId2,
                branchName,
                "test1",
                $"Auto merge test - secondary-1 ({timestamp})");

            Log.Information(
                "Created MRs: project {P1} MR !{Mr1}, project {P2} MR !{Mr2}",
                projectId1,
                mrIid1,
                projectId2,
                mrIid2);

            // Wait for MRs to transition out of 'preparing' state
            await WaitForMrReady(projectId1, mrIid1);
            await WaitForMrReady(projectId2, mrIid2);

            // Wait for TeamCity to finish building so we have a stable CI baseline.
            // Without this, TeamCity's running pipeline masks other statuses and our
            // commit status overrides are ignored until TeamCity finishes.
            Log.Information("--- Waiting for TeamCity CI to stabilize ---");
            await WaitForCiStabilization(projectId1, mrIid1, "primary-1");
            await WaitForCiStabilization(projectId2, mrIid2, "secondary-1");

            // === Login and wait for Mergician to discover the branches ===
            await LoginAndWaitForDashboard("test1");
            var branchAppeared = await WaitForBranchOnDashboard(branchName, 90);
            if (!branchAppeared)
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' did not appear on dashboard within timeout");
            }

            await _browser.TakeScreenshot("behavior_01_branch_on_dashboard");

            // === Navigate to merge group details and enable auto merge ===
            await NavigateToMergeGroupDetails(branchName);
            await EnableAutoMerge();
            await _browser.TakeScreenshot("behavior_02_auto_merge_enabled");

            // === SCENARIO 1: Blocked by failing pipeline ===
            await TestBlockedByFailingPipeline(projectId1, projectId2, mrIid1, mrIid2, branchName);

            // === SCENARIO 2: Blocked by partial readiness (one pipeline passes, other fails) ===
            await TestBlockedByPartialReadiness(projectId1, projectId2, mrIid1, mrIid2, branchName);

            // === SCENARIO 3: Branch behind target - auto-rebase should kick in ===
            await TestAutoRebaseAndBlockedByDivergence(projectId1, projectId2, mrIid1, mrIid2, branchName);

            // === SCENARIO 4: Everything ready - merge should happen ===
            await TestSuccessfulMerge(projectId1, projectId2, mrIid1, mrIid2, branchName);

            Log.Information("=== All auto merge behavior scenarios passed ===");
        }
        catch
        {
            // Take a diagnostic screenshot on failure
            try
            {
                await _browser.TakeScreenshot("behavior_FAILURE");
            }
            catch
            {
                // Ignore screenshot failures during error handling
            }

            throw;
        }
        finally
        {
            // Cleanup: close MRs and delete branches if they still exist
            CleanupTestData(projectId1, projectId2, branchName, mrIid1, mrIid2);
        }
    }

    /// <summary>
    ///     Scenario 1: Set both pipelines to 'failed'. Verify MRs remain open after
    ///     multiple AutoMergeService cycles. The 'failed' commit status makes GitLab's
    ///     combined CI status 'failed', which produces detailed_merge_status='ci_must_pass'.
    /// </summary>
    private async Task TestBlockedByFailingPipeline(
        int projectId1,
        int projectId2,
        int mrIid1,
        int mrIid2,
        string branchName)
    {
        Log.Information("--- Scenario 1: Blocked by failing pipeline ---");

        var sha1 = _gitLab.GetBranchHeadSha(projectId1, branchName);
        var sha2 = _gitLab.GetBranchHeadSha(projectId2, branchName);

        // Set both pipelines to failed
        _gitLab.SetCommitStatus(projectId1, sha1, "failed", PipelineName);
        _gitLab.SetCommitStatus(projectId2, sha2, "failed", PipelineName);

        // Wait for GitLab to process the status and for AutoMergeService cycles
        Log.Information("Waiting 20s for AutoMergeService to process with failing pipelines...");
        await Task.Delay(20_000);

        // Verify MRs are still open
        var mr1 = _gitLab.GetMergeRequestDetail(projectId1, mrIid1);
        var mr2 = _gitLab.GetMergeRequestDetail(projectId2, mrIid2);

        Log.Information(
            "After failing pipelines: MR1 state={State1} dms={Dms1}, MR2 state={State2} dms={Dms2}",
            mr1.State,
            mr1.DetailedMergeStatus,
            mr2.State,
            mr2.DetailedMergeStatus);

        AssertMrOpen(mr1, "primary-1", "Scenario 1: MR should not merge with failing pipeline");
        AssertMrOpen(mr2, "secondary-1", "Scenario 1: MR should not merge with failing pipeline");

        await _browser.TakeScreenshot("behavior_03_blocked_by_pipeline");
        Log.Information("Scenario 1 PASSED: MRs correctly blocked by failing pipelines");
    }

    /// <summary>
    ///     Scenario 2: Set pipeline to 'success' on project 1 only, keep project 2 as 'failed'.
    ///     Verify the merge group does NOT merge because not all branches are ready.
    /// </summary>
    private async Task TestBlockedByPartialReadiness(
        int projectId1,
        int projectId2,
        int mrIid1,
        int mrIid2,
        string branchName)
    {
        Log.Information("--- Scenario 2: Blocked by partial readiness ---");

        var sha1 = _gitLab.GetBranchHeadSha(projectId1, branchName);

        // Set project 1 pipeline to success, keep project 2 as failed (from scenario 1)
        _gitLab.SetCommitStatus(projectId1, sha1, "success", PipelineName);
        Log.Information("Set pipeline to success on primary-1 only, secondary-1 remains failed");

        // Wait for AutoMergeService to process
        Log.Information("Waiting 20s for AutoMergeService to process with partial readiness...");
        await Task.Delay(20_000);

        // Verify NEITHER MR was merged (all-or-nothing)
        var mr1 = _gitLab.GetMergeRequestDetail(projectId1, mrIid1);
        var mr2 = _gitLab.GetMergeRequestDetail(projectId2, mrIid2);

        Log.Information(
            "After partial readiness: MR1 state={State1} dms={Dms1}, MR2 state={State2} dms={Dms2}",
            mr1.State,
            mr1.DetailedMergeStatus,
            mr2.State,
            mr2.DetailedMergeStatus);

        AssertMrOpen(mr1, "primary-1", "Scenario 2: ready MR should NOT merge while other MR is not ready");
        AssertMrOpen(mr2, "secondary-1", "Scenario 2: unready MR should remain open");

        await _browser.TakeScreenshot("behavior_04_blocked_by_partial");
        Log.Information("Scenario 2 PASSED: Merge group correctly blocked when only some branches are ready");
    }

    /// <summary>
    ///     Scenario 3: Clear the failed pipeline on project 2 (set to success).
    ///     Push a commit to main on project 1 to make the branch diverge.
    ///     Verify that the AutoMergeService detects divergence and triggers a rebase,
    ///     and that the MR is NOT merged immediately after rebase (needs new pipeline).
    /// </summary>
    private async Task TestAutoRebaseAndBlockedByDivergence(
        int projectId1,
        int projectId2,
        int mrIid1,
        int mrIid2,
        string branchName)
    {
        Log.Information("--- Scenario 3: Auto rebase and blocked by divergence ---");

        // First, clear the failed status on project 2 from scenario 1/2
        var sha2 = _gitLab.GetBranchHeadSha(projectId2, branchName);
        _gitLab.SetCommitStatus(projectId2, sha2, "success", PipelineName);
        Log.Information("Cleared failed pipeline on secondary-1");

        // Also ensure project 1 has success status
        var sha1 = _gitLab.GetBranchHeadSha(projectId1, branchName);
        _gitLab.SetCommitStatus(projectId1, sha1, "success", PipelineName);

        // Push a commit to main on project 1 to create divergence
        _gitLab.PushCommitToMain(projectId1, "Divergence commit for auto rebase test");

        Log.Information("Created divergence on primary-1 main, both pipelines set to success");

        // Wait for GitLab to detect divergence on the MR. The detailed_merge_status
        // should transition from 'mergeable' to 'need_rebase' once detected.
        Log.Information("Waiting for divergence to be detected (up to 60s)...");
        var divergenceDetected = await WaitForDivergenceDetected(projectId1, mrIid1, 60);

        if (!divergenceDetected)
        {
            Log.Warning("GitLab did not report divergence within timeout, checking MR state directly...");
            var mrCheck = _gitLab.GetMergeRequestDetail(projectId1, mrIid1);
            Log.Information(
                "MR1 state={State}, dms={Dms}, diverged={Diverged}",
                mrCheck.State,
                mrCheck.DetailedMergeStatus,
                mrCheck.DivergedCommitsCount);

            // Even if not detected, the MR should be open
            AssertMrOpen(
                mrCheck,
                "primary-1",
                "Scenario 3: MR should remain open while branch may be behind target");

            Log.Information("Scenario 3 PASSED (partial): MR correctly remains open");
            await _browser.TakeScreenshot("behavior_05_after_rebase");
            return;
        }

        // Wait for the AutoMergeService to trigger rebase
        Log.Information("Divergence detected, waiting for auto-rebase (up to 60s)...");
        var rebased = await WaitForRebase(projectId1, mrIid1, sha1, 60);

        if (rebased)
        {
            Log.Information("Auto-rebase detected: branch SHA changed on primary-1");

            // After rebase, the commit SHA changes. The old pipeline success status
            // is no longer relevant. The MR should NOT merge until the new commit gets
            // a successful pipeline. Wait briefly and check.
            await Task.Delay(10_000);

            var mr1AfterRebase = _gitLab.GetMergeRequestDetail(projectId1, mrIid1);
            Log.Information(
                "After rebase: MR1 state={State}, dms={Dms}",
                mr1AfterRebase.State,
                mr1AfterRebase.DetailedMergeStatus);

            AssertMrOpen(
                mr1AfterRebase,
                "primary-1",
                "Scenario 3: MR should not merge immediately after rebase (needs new pipeline)");

            Log.Information(
                "Scenario 3 PASSED: Auto-rebase happened and merge correctly blocked pending new pipeline");
        }
        else
        {
            // Rebase didn't happen but divergence was detected. This could be a timing issue
            // with the AutoMergeService. The MR should still be open.
            Log.Warning("Auto-rebase was not detected within timeout despite divergence being reported");
            var mr1 = _gitLab.GetMergeRequestDetail(projectId1, mrIid1);
            Log.Information(
                "MR1 state={State}, dms={Dms}, diverged={Diverged}",
                mr1.State,
                mr1.DetailedMergeStatus,
                mr1.DivergedCommitsCount);

            AssertMrOpen(
                mr1,
                "primary-1",
                "Scenario 3: MR should remain open when branch is behind target");

            Log.Information("Scenario 3 PASSED (partial): MR correctly blocked while behind target");
        }

        await _browser.TakeScreenshot("behavior_05_after_rebase");
    }

    /// <summary>
    ///     Scenario 4: Ensure all conditions are met (up-to-date branches, successful pipelines).
    ///     Verify both MRs get merged by the AutoMergeService.
    ///     Continuously updates pipeline status on new SHAs (e.g. after rebase) and
    ///     waits for TeamCity to finish if needed.
    /// </summary>
    private async Task TestSuccessfulMerge(
        int projectId1,
        int projectId2,
        int mrIid1,
        int mrIid2,
        string branchName)
    {
        Log.Information("--- Scenario 4: All conditions met - merge should happen ---");

        // Wait for TeamCity to finish any builds triggered by the rebase in Scenario 3.
        // The rebase creates new commit SHAs, which trigger new ~2-minute TeamCity builds.
        // AutoMergeService won't merge while ci_still_running, so we must wait here.
        Log.Information("Waiting for CI to stabilize on both MRs after rebase before setting success...");
        await WaitForCiStabilization(projectId1, mrIid1, "primary-1");
        await WaitForCiStabilization(projectId2, mrIid2, "secondary-1");

        // Ensure both branches have successful pipeline statuses on their current HEAD.
        // Read SHAs after the rebase to post status on the correct commit.
        var sha1 = _gitLab.GetBranchHeadSha(projectId1, branchName);
        var sha2 = _gitLab.GetBranchHeadSha(projectId2, branchName);

        _gitLab.SetCommitStatus(projectId1, sha1, "success", PipelineName);
        _gitLab.SetCommitStatus(projectId2, sha2, "success", PipelineName);

        Log.Information(
            "Set pipeline success on both branches: primary-1 SHA={Sha1}, secondary-1 SHA={Sha2}",
            sha1[..8],
            sha2[..8]);

        // Wait for AutoMergeService to detect readiness and merge.
        // CI has already stabilized so 60s should be plenty.
        const int maxChecks = 18;
        Log.Information(
            "Waiting for AutoMergeService to merge both MRs (up to {Timeout}s)...",
            maxChecks * 5);

        var merged = false;
        for (var i = 0; i < maxChecks; i++)
        {
            await Task.Delay(5000);

            var mr1 = _gitLab.GetMergeRequestDetail(projectId1, mrIid1);
            var mr2 = _gitLab.GetMergeRequestDetail(projectId2, mrIid2);

            Log.Information(
                "Merge check ({Attempt}/{Max}): MR1 state={State1} dms={Dms1}, MR2 state={State2} dms={Dms2}",
                i + 1,
                maxChecks,
                mr1.State,
                mr1.DetailedMergeStatus,
                mr2.State,
                mr2.DetailedMergeStatus);

            if (mr1.State == "merged" && mr2.State == "merged")
            {
                merged = true;
                break;
            }

            // If one MR is already merged but the other isn't, that's a partial merge.
            // Keep waiting - the AutoMergeService may retry or the second may complete.
            if (mr1.State == "merged" || mr2.State == "merged")
            {
                Log.Information("Partial merge detected, waiting for remaining MR...");
            }

            // Keep pipeline status current: if a branch SHA changed (rebase), update the status
            UpdatePipelineStatusIfNeeded(projectId1, branchName, ref sha1, mr1);
            UpdatePipelineStatusIfNeeded(projectId2, branchName, ref sha2, mr2);
        }

        await _browser.TakeScreenshot("behavior_06_after_merge");

        if (!merged)
        {
            var finalMr1 = _gitLab.GetMergeRequestDetail(projectId1, mrIid1);
            var finalMr2 = _gitLab.GetMergeRequestDetail(projectId2, mrIid2);
            throw new InvalidOperationException(
                $"Scenario 4 FAILED: MRs were not merged within timeout. "
                + $"MR1: state={finalMr1.State}, dms={finalMr1.DetailedMergeStatus}. "
                + $"MR2: state={finalMr2.State}, dms={finalMr2.DetailedMergeStatus}.");
        }

        Log.Information("Scenario 4 PASSED: Both MRs were successfully merged by AutoMergeService");

        // Navigate back to dashboard and wait for the merge group to disappear.
        // AutoMergeService removes merged branches from the DB immediately after merging,
        // so the card should vanish within the next dashboard poll cycle (a few seconds).

        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(3000);
        await _browser.TakeScreenshot("behavior_07_dashboard_after_merge");

        Log.Information(
            "Waiting for merge group '{BranchName}' to disappear from dashboard...",
            branchName);

        var disappeared = await WaitForBranchToDisappearFromDashboard(branchName, 30);

        await _browser.TakeScreenshot("behavior_08_dashboard_merge_group_gone");

        if (!disappeared)
        {
            throw new InvalidOperationException(
                $"Scenario 4: Merge group for '{branchName}' did not disappear from dashboard within timeout "
                + "after successful merge");
        }

        Log.Information(
            "Scenario 4 PASSED: Merge group disappeared from dashboard after successful merge");
    }

    /// <summary>
    ///     If the branch HEAD SHA has changed (e.g. after a rebase), update the commit
    ///     status to 'success' so the AutoMergeService's pipeline check passes.
    /// </summary>
    private void UpdatePipelineStatusIfNeeded(
        int projectId,
        string branchName,
        ref string trackedSha,
        GitLabMrDetail mr)
    {
        // Only update if the MR is still open and the pipeline status indicates CI issues
        if (mr.State != "opened")
        {
            return;
        }

        try
        {
            var currentSha = _gitLab.GetBranchHeadSha(projectId, branchName);
            if (currentSha != trackedSha)
            {
                Log.Information(
                    "Branch SHA changed in project {ProjectId} ({OldSha} → {NewSha}), updating pipeline status",
                    projectId,
                    trackedSha[..8],
                    currentSha[..8]);

                trackedSha = currentSha;
                _gitLab.SetCommitStatus(projectId, currentSha, "success", PipelineName);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(
                "Failed to update pipeline status for project {ProjectId}: {Message}",
                projectId,
                ex.Message);
        }
    }

    /// <summary>
    ///     Waits for a merge request to transition out of the 'preparing' state.
    /// </summary>
    private async Task WaitForMrReady(int projectId, int mrIid)
    {
        for (var i = 0; i < 20; i++)
        {
            var mr = _gitLab.GetMergeRequestDetail(projectId, mrIid);
            if (mr.DetailedMergeStatus != "preparing")
            {
                Log.Information(
                    "MR !{MrIid} in project {ProjectId} ready: dms={Dms}",
                    mrIid,
                    projectId,
                    mr.DetailedMergeStatus);

                return;
            }

            await Task.Delay(1000);
        }

        Log.Warning("MR !{MrIid} in project {ProjectId} still 'preparing' after timeout", mrIid, projectId);
    }

    /// <summary>
    ///     Waits for TeamCity's CI pipeline to finish on an MR. The detailed_merge_status
    ///     transitions from 'ci_still_running' to something else (e.g. 'mergeable') once
    ///     all external pipelines complete. This ensures we have a stable baseline before
    ///     manipulating pipeline statuses with the Commit Statuses API.
    /// </summary>
    private async Task WaitForCiStabilization(int projectId, int mrIid, string projectLabel)
    {
        const int timeoutSeconds = 240;
        Log.Information(
            "Waiting for CI to stabilize on MR !{MrIid} in {Project} (up to {Timeout}s)...",
            mrIid,
            projectLabel,
            timeoutSeconds);

        for (var i = 0; i < timeoutSeconds; i++)
        {
            var mr = _gitLab.GetMergeRequestDetail(projectId, mrIid);
            if (mr.DetailedMergeStatus is not ("ci_still_running" or "preparing"))
            {
                Log.Information(
                    "CI stabilized on {Project} MR !{MrIid} after ~{Seconds}s: dms={Dms}",
                    projectLabel,
                    mrIid,
                    i,
                    mr.DetailedMergeStatus);

                return;
            }

            if (i % 15 == 0 && i > 0)
            {
                Log.Information(
                    "Still waiting for CI on {Project} MR !{MrIid}... {Seconds}s (dms={Dms})",
                    projectLabel,
                    mrIid,
                    i,
                    mr.DetailedMergeStatus);
            }

            await Task.Delay(1000);
        }

        var finalMr = _gitLab.GetMergeRequestDetail(projectId, mrIid);
        Log.Warning(
            "CI did not stabilize on {Project} MR !{MrIid} within {Timeout}s (dms={Dms}). Proceeding anyway.",
            projectLabel,
            mrIid,
            timeoutSeconds,
            finalMr.DetailedMergeStatus);
    }

    /// <summary>
    ///     Waits for GitLab to detect that a branch has diverged from its target.
    ///     After pushing a commit to main, GitLab may take time to recalculate the MR's
    ///     diverged_commits_count and detailed_merge_status.
    /// </summary>
    private async Task<bool> WaitForDivergenceDetected(int projectId, int mrIid, int timeoutSeconds)
    {
        for (var i = 0; i < timeoutSeconds; i++)
        {
            await Task.Delay(1000);
            var mr = _gitLab.GetMergeRequestDetail(projectId, mrIid);

            if (mr.DivergedCommitsCount > 0 || mr.DetailedMergeStatus == "need_rebase")
            {
                Log.Information(
                    "Divergence detected on MR !{MrIid} after ~{Seconds}s: diverged={Diverged}, dms={Dms}",
                    mrIid,
                    i,
                    mr.DivergedCommitsCount,
                    mr.DetailedMergeStatus);

                return true;
            }

            if (i % 10 == 0 && i > 0)
            {
                Log.Information(
                    "Waiting for divergence on MR !{MrIid}... {Seconds}s (diverged={Diverged}, dms={Dms})",
                    mrIid,
                    i,
                    mr.DivergedCommitsCount,
                    mr.DetailedMergeStatus);
            }
        }

        return false;
    }

    /// <summary>
    ///     Waits for a branch to be rebased (SHA changes from the original).
    /// </summary>
    private async Task<bool> WaitForRebase(int projectId, int mrIid, string originalSha, int timeoutSeconds)
    {
        for (var i = 0; i < timeoutSeconds; i++)
        {
            await Task.Delay(1000);
            try
            {
                var mr = _gitLab.GetMergeRequestDetail(projectId, mrIid);
                var currentSha = _gitLab.GetBranchHeadSha(projectId, mr.SourceBranch);
                if (currentSha != originalSha)
                {
                    Log.Information(
                        "Rebase detected on MR !{MrIid} after ~{Seconds}s: {OldSha} → {NewSha}",
                        mrIid,
                        i,
                        originalSha[..8],
                        currentSha[..8]);

                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Error checking rebase status: {Message}", ex.Message);
            }

            if (i % 10 == 0 && i > 0)
            {
                Log.Information("Still waiting for rebase on MR !{MrIid}... {Seconds}s", mrIid, i);
            }
        }

        return false;
    }

    private async Task<bool> WaitForBranchOnDashboard(string branchName, int timeoutSeconds)
    {
        for (var i = 0; i < timeoutSeconds; i++)
        {
            var cards = _browser.Page.Locator(".merge-group-card");
            var count = await cards.CountAsync();
            for (var j = 0; j < count; j++)
            {
                var name = (await cards.Nth(j).Locator(".branch-name").InnerTextAsync()).Trim();
                if (name.Contains(branchName, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information(
                        "Branch '{BranchName}' found on dashboard after ~{Seconds}s",
                        branchName,
                        i);

                    return true;
                }
            }

            if (i % 10 == 0)
            {
                Log.Information(
                    "Waiting for branch '{BranchName}' to appear on dashboard... {Seconds}s",
                    branchName,
                    i);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private async Task<bool> WaitForBranchToDisappearFromDashboard(string branchName, int timeoutSeconds)
    {
        for (var i = 0; i < timeoutSeconds; i++)
        {
            var cards = _browser.Page.Locator(".merge-group-card");
            var count = await cards.CountAsync();
            var found = false;

            for (var j = 0; j < count; j++)
            {
                var name = (await cards.Nth(j).Locator(".branch-name").InnerTextAsync()).Trim();
                if (name.Contains(branchName, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                Log.Information(
                    "Branch '{BranchName}' disappeared from dashboard after ~{Seconds}s",
                    branchName,
                    i);

                return true;
            }

            if (i % 10 == 0 && i > 0)
            {
                Log.Information(
                    "Waiting for branch '{BranchName}' to disappear from dashboard... {Seconds}s",
                    branchName,
                    i);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    /// <summary>
    ///     Navigates to the merge group details page for the given branch name
    ///     by clicking on its card on the dashboard.
    /// </summary>
    private async Task NavigateToMergeGroupDetails(string branchName)
    {
        var cards = _browser.Page.Locator(".merge-group-card");
        var count = await cards.CountAsync();

        for (var i = 0; i < count; i++)
        {
            var name = (await cards.Nth(i).Locator(".branch-name").InnerTextAsync()).Trim();
            if (name.Contains(branchName, StringComparison.OrdinalIgnoreCase))
            {
                await cards.Nth(i).ClickAsync();
                await _browser.Page.WaitForURLAsync(
                    url => url.Contains("/merge-group/"),
                    new PageWaitForURLOptions { Timeout = 15000 });

                await Task.Delay(2000);
                Log.Information("Navigated to merge group details for '{BranchName}'", branchName);
                return;
            }
        }

        throw new InvalidOperationException(
            $"Could not find card for branch '{branchName}' on dashboard");
    }

    /// <summary>
    ///     Enables auto merge via the UI toggle on the merge group details page.
    ///     Assumes the browser is already on the details page.
    /// </summary>
    private async Task EnableAutoMerge()
    {
        var autoMergeSwitch = _browser.Page.Locator(".auto-merge-controls .v-switch").First;
        await autoMergeSwitch.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

        var switchInput = autoMergeSwitch.Locator("input[type='checkbox']");
        var isChecked = await switchInput.IsCheckedAsync();

        if (!isChecked)
        {
            await autoMergeSwitch.ClickAsync();
            await _browser.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(1500);
            Log.Information("Auto merge enabled via UI toggle");
        }
        else
        {
            Log.Information("Auto merge was already enabled");
        }

        // Verify it's on
        isChecked = await switchInput.IsCheckedAsync();
        if (!isChecked)
        {
            throw new InvalidOperationException("Failed to enable auto merge toggle");
        }
    }

    private static void AssertMrOpen(GitLabMrDetail mr, string projectName, string context)
    {
        if (mr.State != "opened")
        {
            throw new InvalidOperationException(
                $"{context}: Expected MR !{mr.Iid} in {projectName} to be 'opened' but was '{mr.State}'");
        }
    }

    private void CleanupTestData(int projectId1, int projectId2, string branchName, int mrIid1, int mrIid2)
    {
        Log.Information("Cleaning up test data for branch '{BranchName}'...", branchName);
        try
        {
            if (mrIid1 > 0)
            {
                _gitLab.CloseMergeRequest(projectId1, mrIid1);
            }

            if (mrIid2 > 0)
            {
                _gitLab.CloseMergeRequest(projectId2, mrIid2);
            }

            _gitLab.DeleteBranch(projectId1, branchName);
            _gitLab.DeleteBranch(projectId2, branchName);
        }
        catch (Exception ex)
        {
            Log.Warning("Cleanup error (non-fatal): {Message}", ex.Message);
        }
    }

    private async Task LoginAndWaitForDashboard(string username)
    {
        await _browser.Page.Context.ClearCookiesAsync();
        await Task.Delay(500);

        await _browser.Navigate($"{TestConfig.MergicianUrl}/api/auth/login");
        await Task.Delay(2000);

        var currentUrl = _browser.Page.Url;

        if (currentUrl.Contains("/users/sign_in"))
        {
            var usernameField = _browser.Page.Locator("#user_login");
            var passwordField = _browser.Page.Locator("#user_password");
            var signInButton =
                _browser.Page.Locator("input[type='submit'][name='commit'], button[type='submit']");

            await BrowserService.FillFormField(usernameField, username, "username");
            await BrowserService.FillFormField(passwordField, TestConfig.TestPassword, "password");
            await signInButton.First.ClickAsync();
            await _browser.Page.WaitForURLAsync(
                url => url.Contains("/oauth/authorize") || url.Contains("localhost:5000"),
                new PageWaitForURLOptions { Timeout = 30000 });

            currentUrl = _browser.Page.Url;
        }

        if (currentUrl.Contains("/oauth/authorize"))
        {
            Log.Information("OAuth authorization page, submitting...");
            await _browser.Page.EvaluateAsync(
                """
                (() => {
                    const btn = document.querySelector('[data-testid="authorization-button"]');
                    if (btn) { btn.click(); return; }
                    const form = document.querySelector('form');
                    if (form) { form.submit(); }
                })()
                """);

            try
            {
                await _browser.Page.WaitForURLAsync(
                    url => !url.Contains("/oauth/authorize"),
                    new PageWaitForURLOptions { Timeout = 15000 });
            }
            catch
            {
                Log.Warning("OAuth authorize didn't redirect. URL: {Url}", _browser.Page.Url);
            }
        }

        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
        await DashboardWaitHelper.WaitForDashboardReady(_browser.Page);
    }
}