using IntegrationTest.Services;
using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests that GitLab CI manual jobs are filtered out and never appear
///     as job chips in branch cards on the dashboard.
///     Uses the 'gitlab-ci-test' project which has a .gitlab-ci.yml with
///     only a 'when: manual' job and no TeamCity VCS root, ensuring
///     GitLab CI is the sole pipeline source for reliable assertions.
/// </summary>
public class ManualJobFilterTest
{
    private readonly BrowserService _browser;

    private readonly GitLabTestHelper _gitLab = new();

    public ManualJobFilterTest(BrowserService browser)
    {
        _browser = browser;
    }

    public async Task Run()
    {
        _browser.SetScreenshotDir(Path.Combine(TestConfig.ScreenshotDir, "manual-job-filter"));
        await TestManualJobFiltering();
        Log.Information("Manual job filter test passed");
    }

    private async Task TestManualJobFiltering()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var branchName = $"feature/manual-job-test-{timestamp}";
        var projectId = _gitLab.GetProjectId("gitlab-ci-test");

        Log.Information(
            "=== Manual Job Filter Test: branch={BranchName}, project={ProjectId} ===",
            branchName,
            projectId);

        var mergeRequestIid = 0;
        try
        {
            _gitLab.CreateBranchWithCommit(projectId, branchName, "test1");
            mergeRequestIid = _gitLab.CreateMergeRequest(
                projectId,
                branchName,
                "test1",
                $"Manual job filter test ({timestamp})");

            Log.Information("Created branch and MR; waiting for GitLab CI pipeline with manual job...");

            await _gitLab.WaitForGitLabCIPipelineWithManualJob(
                projectId,
                branchName,
                "manual-deploy",
                timeoutSeconds: 60);

            await LoginHelper.EnsureLoggedIn(_browser, "test1");

            var branchAppeared = await WaitForBranchOnDashboard(branchName, 90);
            if (!branchAppeared)
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' did not appear on dashboard within timeout");
            }

            await _browser.TakeScreenshot("manual_job_filter_01_branch_appeared");

            // Wait for the new card's MR data to be resolved before checking pipeline job data.
            await DashboardWaitHelper.WaitForDashboardReady(_browser.Page, 60);

            // Wait for Mergician's sync service to process the pipeline data.
            // The sync cycle runs every 10s; waiting 15s guarantees at least one full
            // refresh after the branch appeared, so pipeline job data is stable.
            Log.Information("Waiting 15s for Mergician sync to process pipeline data...");
            await Task.Delay(15_000);

            await _browser.TakeScreenshot("manual_job_filter_02_after_sync_wait");

            var row = _browser.Page.Locator($"[data-mg-name*='{branchName}']").First;

            // Verify the MR title is visible — confirms branch data has been loaded by Mergician.
            var mrTitleCount = await row.Locator(".col-mr .mr-title").CountAsync();
            if (mrTitleCount == 0)
            {
                throw new InvalidOperationException(
                    $"MR title not visible in grid row for branch '{branchName}'; branch data may not have loaded");
            }

            // Assert no job chips are shown: the manual job must have been filtered out.
            var jobCellCount = await row.Locator(".col-jobs .jobs-cell").CountAsync();
            if (jobCellCount > 0)
            {
                throw new InvalidOperationException(
                    $"Expected no job chips for branch '{branchName}' (manual job should be filtered out), "
                    + $"but found {jobCellCount} .jobs-cell element(s)");
            }

            await _browser.TakeScreenshot("manual_job_filter_03_verified_no_job_chips");

            Log.Information(
                "=== Manual job filter verified: no job chips shown for branch '{BranchName}' ===",
                branchName);
        }
        catch
        {
            try
            {
                await _browser.TakeScreenshot("manual_job_filter_FAILURE");
            }
            catch
            {
                // Ignore screenshot failures during error handling
            }

            throw;
        }
        finally
        {
            if (mergeRequestIid > 0)
                _gitLab.CloseMergeRequest(projectId, mergeRequestIid);
            _gitLab.DeleteBranch(projectId, branchName);
        }
    }

    private async Task<bool> WaitForBranchOnDashboard(string branchName, int timeoutSeconds)
    {
        for (var i = 0; i < timeoutSeconds; i++)
        {
            var rows = _browser.Page.Locator($"[data-mg-name*='{branchName}']");
            if (await rows.CountAsync() > 0)
            {
                Log.Information(
                    "Branch '{BranchName}' found on dashboard after ~{Seconds}s",
                    branchName,
                    i);

                return true;
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
}
