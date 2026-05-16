using IntegrationTest.Services;
using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests that the dashboard dynamically updates when new branches are pushed
///     and when MR/approval status changes, while the dashboard is already loaded.
///     Uses Playwright to interact with the card-based UI and GitLab API to create
///     test data in real time. The dashboard uses polling to discover new branches
///     from the database (populated by a background sync thread) and a separate
///     refresh cycle for MR/approval status updates.
/// </summary>
public class DashboardLiveUpdateTest
{
    private readonly BrowserService _browser;

    private readonly GitLabTestHelper _gitLab = new();

    public DashboardLiveUpdateTest(BrowserService browser)
    {
        _browser = browser;
    }

    public async Task Run()
    {
        _browser.SetScreenshotDir(Path.Combine(TestConfig.ScreenshotDir, "live-updates"));

        await TestNewBranchAppearsOnDashboard();
        await TestMergeRequestStatusUpdatesOnDashboard();
        await TestDeletedBranchDisappearsAndStaysGoneAfterReload();

        Log.Information("Dashboard live update tests passed");
    }

    /// <summary>
    ///     Verifies that when a user pushes a new branch while the dashboard is loaded,
    ///     the branch appears on the dashboard via polling (background sync discovers it,
    ///     then the dashboard poll returns it) without requiring a page refresh.
    /// </summary>
    private async Task TestNewBranchAppearsOnDashboard()
    {
        Log.Information("Testing: new branch appears on loaded dashboard...");

        // Login as test1 and wait for the initial dashboard to fully load
        await LoginHelper.EnsureLoggedIn(_browser, "test1");
        await _browser.TakeScreenshot("live_01_initial_dashboard");

        // Push a new branch via GitLab API as test1
        var projectId = _gitLab.GetProjectId("primary-1");
        var branchName = $"feature/live-test-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _gitLab.CreateBranchWithCommit(projectId, branchName, "test1");

        Log.Information(
            "Pushed branch '{BranchName}' to project {ProjectId}, waiting for dashboard to update...",
            branchName,
            projectId);

        // Wait for the branch to appear on the dashboard (polling interval is 5s)
        var appeared = await WaitForBranchOnDashboard(branchName, 60);
        await _browser.TakeScreenshot("live_02_after_new_branch");

        if (!appeared)
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' did not appear on dashboard within timeout");
        }

        Log.Information("New branch '{BranchName}' appeared on dashboard successfully", branchName);
    }

    /// <summary>
    ///     Verifies that when an MR is created on an existing branch,
    ///     the dashboard updates the MR status via the refresh polling.
    /// </summary>
    private async Task TestMergeRequestStatusUpdatesOnDashboard()
    {
        Log.Information("Testing: MR status updates on loaded dashboard...");

        // Reuse existing test1 session — navigate to the dashboard and wait for full load
        await LoginHelper.NavigateToDashboard(_browser);
        await _browser.TakeScreenshot("live_03_dashboard_before_mr");

        // Push a branch without MR first
        var projectId = _gitLab.GetProjectId("primary-1");
        var branchName = $"feature/mr-test-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _gitLab.CreateBranchWithCommit(projectId, branchName, "test1");

        // Wait for the branch to appear
        var appeared = await WaitForBranchOnDashboard(branchName, 60);
        if (!appeared)
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' did not appear on dashboard within timeout");
        }

        await _browser.TakeScreenshot("live_04_branch_without_mr");

        // verify explicit no-MR text is shown before an MR exists
        // Wait for MR data to load so we can verify the no-MR state (row may still be loading)
        var noMrLoaded = await WaitForNoMrText(branchName, 30);
        if (!noMrLoaded)
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' did not show 'No MR' within timeout");
        }

        var row = _browser.Page.Locator($"[data-mg-name*='{branchName}']").First;
        var noMergeRequestText = (await row.Locator(".col-mr .no-mr-text").InnerTextAsync()).Trim();
        if (!noMergeRequestText.Contains("No MR", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expected 'No MR' text before MR creation, got '{noMergeRequestText}'");
        }

        // there should be no MR title element yet
        var titleCount = await row.Locator(".col-mr .mr-title").CountAsync();
        if (titleCount > 0)
        {
            throw new InvalidOperationException("Unexpected MR title shown before any MR exists");
        }

        // Create an MR on the branch with an intentionally long title,
        // so we can verify the full title is displayed in the grid without truncation.
        var longTitle = new string('X', 230);
        _gitLab.CreateMergeRequest(projectId, branchName, "test1", longTitle);
        Log.Information(
            "Created MR for branch '{BranchName}' with long title, waiting for dashboard update...",
            branchName);

        // Wait for the MR title to appear on the row (the refresh cycle picks up the new MR)
        var mrUpdated = await WaitForMergeRequestToAppear(branchName, 60);
        await _browser.TakeScreenshot("live_05_branch_with_mr");

        if (!mrUpdated)
        {
            throw new InvalidOperationException(
                $"MR title did not appear for branch '{branchName}' within timeout");
        }

        Log.Information("MR status updated on dashboard for '{BranchName}'", branchName);

        var noMergeRequestAfterMergeRequestCount = await row.Locator(".col-mr .no-mr-text").CountAsync();
        if (noMergeRequestAfterMergeRequestCount > 0)
        {
            throw new InvalidOperationException("No-MR text should disappear after MR creation");
        }

        // Check the MR title element is present with the full title (no truncation in grid view)
        var titleEl = row.Locator(".col-mr .mr-title");
        if (await titleEl.CountAsync() == 0)
        {
            throw new InvalidOperationException(
                "Expected MR title element in grid row after MR creation, but none found");
        }

        var titleText = (await titleEl.InnerTextAsync()).Trim();
        if (titleText != longTitle)
        {
            throw new InvalidOperationException(
                $"MR title should contain full long title, got '{titleText}' (len={titleText.Length}), expected len={longTitle.Length}");
        }
    }

    /// <summary>
    ///     Verifies that when a tracked branch is deleted in GitLab, it disappears from
    ///     the already open dashboard and stays gone after a full page reload.
    /// </summary>
    private async Task TestDeletedBranchDisappearsAndStaysGoneAfterReload()
    {
        Log.Information("Testing: deleted branch disappears and stays gone after reload...");

        // Reuse existing test1 session — navigate to the dashboard and wait for full load
        await LoginHelper.NavigateToDashboard(_browser);
        await _browser.TakeScreenshot("live_06_before_delete_branch_test");

        var projectId = _gitLab.GetProjectId("primary-1");
        var branchName = $"feature/delete-test-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _gitLab.CreateBranchWithCommit(projectId, branchName, "test1");

        var appeared = await WaitForBranchOnDashboard(branchName, 60);
        if (!appeared)
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' did not appear on dashboard before delete test");
        }

        _gitLab.DeleteBranch(projectId, branchName);
        Log.Information("Deleted branch '{BranchName}', waiting for dashboard removal", branchName);

        var disappeared = await WaitForBranchToDisappearFromDashboard(branchName, 90);
        await _browser.TakeScreenshot("live_07_after_delete_branch_live_update");

        if (!disappeared)
        {
            throw new InvalidOperationException(
                $"Deleted branch '{branchName}' did not disappear from dashboard within timeout");
        }

        // Fresh reload: branch should not come back from server cache.
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
        await WaitForDashboardLoadComplete();

        await EnsureBranchStaysAbsent(branchName, 20);
        await _browser.TakeScreenshot("live_08_after_delete_branch_reload");

        Log.Information("Deleted branch '{BranchName}' remained absent after dashboard reload", branchName);
    }

    private async Task WaitForDashboardLoadComplete()
    {
        var loaded = await DashboardWaitHelper.WaitForDashboardReady(_browser.Page);
        if (!loaded)
        {
            throw new InvalidOperationException("Dashboard did not fully load within 120s");
        }
    }

    private async Task EnsureBranchStaysAbsent(string branchName, int durationSeconds)
    {
        for (var second = 0; second < durationSeconds; second++)
        {
            if (await IsBranchOnDashboard(branchName))
            {
                throw new InvalidOperationException(
                    $"Deleted branch '{branchName}' reappeared on dashboard after reload (at {second}s)");
            }

            await Task.Delay(1000);
        }
    }

    /// <summary>
    ///     Waits for a branch name to appear in the dashboard cards.
    /// </summary>
    private async Task<bool> WaitForBranchOnDashboard(string branchName, int timeoutSeconds)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            if (await IsBranchOnDashboard(branchName))
            {
                Log.Information(
                    "Branch '{BranchName}' found on dashboard after ~{Seconds}s",
                    branchName,
                    s);

                return true;
            }

            if (s % 10 == 0)
            {
                Log.Information(
                    "Waiting for branch '{BranchName}' to appear... {Seconds}s",
                    branchName,
                    s);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private async Task<bool> WaitForBranchToDisappearFromDashboard(string branchName, int timeoutSeconds)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            if (!await IsBranchOnDashboard(branchName))
            {
                Log.Information(
                    "Branch '{BranchName}' disappeared from dashboard after ~{Seconds}s",
                    branchName,
                    s);

                return true;
            }

            if (s % 10 == 0)
            {
                Log.Information(
                    "Waiting for branch '{BranchName}' to disappear... {Seconds}s",
                    branchName,
                    s);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private async Task<bool> IsBranchOnDashboard(string branchName)
    {
        var rows = _browser.Page.Locator($"[data-mg-name*='{branchName}']");
        return await rows.CountAsync() > 0;
    }

    /// <summary>
    ///     Waits until a branch grid row shows the "No MR" indicator (row data loaded, no MR).
    /// </summary>
    private async Task<bool> WaitForNoMrText(string branchName, int timeoutSeconds)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            var row = _browser.Page.Locator($"[data-mg-name*='{branchName}']").First;
            if (await row.CountAsync() > 0 && await row.Locator(".col-mr .no-mr-text").CountAsync() > 0)
            {
                Log.Information(
                    "Branch '{BranchName}' shows 'No MR' after ~{Seconds}s",
                    branchName,
                    s);

                return true;
            }

            if (s % 10 == 0)
            {
                Log.Information(
                    "Waiting for 'No MR' text on '{BranchName}'... {Seconds}s",
                    branchName,
                    s);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    /// <summary>
    ///     Waits until a branch grid row shows an MR title element (indicating the dashboard
    ///     has picked up a newly created MR via the refresh cycle).
    /// </summary>
    private async Task<bool> WaitForMergeRequestToAppear(string branchName, int timeoutSeconds)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            var row = _browser.Page.Locator($"[data-mg-name*='{branchName}']").First;
            if (await row.CountAsync() > 0 && await row.Locator(".col-mr .mr-title").CountAsync() > 0)
            {
                Log.Information(
                    "Branch '{BranchName}' shows MR title after ~{Seconds}s",
                    branchName,
                    s);

                return true;
            }

            if (s % 10 == 0)
            {
                Log.Information(
                    "Waiting for MR title to appear on '{BranchName}'... {Seconds}s",
                    branchName,
                    s);
            }

            await Task.Delay(1000);
        }

        return false;
    }
}