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
public class ManualJobFilterTest : IDisposable
{
    private readonly BrowserService _browser = new();

    private readonly GitLabTestHelper _gitLab = new();

    public void Dispose()
    {
        _browser.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task Run()
    {
        await _browser.Initialize(Path.Combine(TestConfig.ScreenshotDir, "manual-job-filter"));
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

            await LoginAndWaitForDashboard("test1");

            var branchAppeared = await WaitForBranchOnDashboard(branchName, 90);
            if (!branchAppeared)
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' did not appear on dashboard within timeout");
            }

            await _browser.TakeScreenshot("manual_job_filter_01_branch_appeared");

            // Wait for Mergician's sync service to process the pipeline data.
            // The sync cycle runs every 10s; waiting 15s guarantees at least one full
            // refresh after the branch appeared, so pipeline job data is stable.
            Log.Information("Waiting 15s for Mergician sync to process pipeline data...");
            await Task.Delay(15_000);

            await _browser.TakeScreenshot("manual_job_filter_02_after_sync_wait");

            var card = _browser.Page.Locator(".merge-group-card")
                .Filter(new LocatorFilterOptions { HasTextString = branchName })
                .First;

            // Verify the MR title is visible — confirms branch data has been loaded by Mergician.
            var mrTitleCount = await card.Locator(".item-mr-title").CountAsync();
            if (mrTitleCount == 0)
            {
                throw new InvalidOperationException(
                    $"MR title not visible in card for branch '{branchName}'; branch data may not have loaded");
            }

            // Assert no job chips are shown: the manual job must have been filtered out.
            var jobSectionCount = await card.Locator(".card-jobs").CountAsync();
            if (jobSectionCount > 0)
            {
                throw new InvalidOperationException(
                    $"Expected no job chips for branch '{branchName}' (manual job should be filtered out), "
                    + $"but found {jobSectionCount} .card-jobs element(s)");
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

    private async Task<bool> WaitForBranchOnDashboard(string branchName, int timeoutSeconds)
    {
        for (var i = 0; i < timeoutSeconds; i++)
        {
            var cards = _browser.Page.Locator(".merge-group-card");
            var count = await cards.CountAsync();
            for (var j = 0; j < count; j++)
            {
                var name = (await cards.Nth(j).Locator(".branch-name, .branch-subtitle").First.InnerTextAsync()).Trim();
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
}
