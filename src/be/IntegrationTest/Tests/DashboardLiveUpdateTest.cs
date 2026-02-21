using IntegrationTest.Services;
using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests that the dashboard dynamically updates when new branches are pushed
///     and when MR/approval status changes, while the dashboard is already loaded.
///     Uses Playwright to interact with the card-based UI and GitLab API to create
///     test data in real time.
/// </summary>
public class DashboardLiveUpdateTest : IDisposable
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
        await _browser.Initialize(
            Path.Combine(TestConfig.ScreenshotDir, "activity"));

        await TestNewBranchAppearsOnDashboard();
        await TestMrStatusUpdatesOnDashboard();
        await TestDeletedBranchDisappearsAndStaysGoneAfterReload();

        Log.Information("Dashboard live update tests passed");
    }

    /// <summary>
    ///     Verifies that when a user pushes a new branch while the dashboard is loaded,
    ///     the branch appears on the dashboard via polling without requiring a page refresh.
    /// </summary>
    private async Task TestNewBranchAppearsOnDashboard()
    {
        Log.Information("Testing: new branch appears on loaded dashboard...");

        // Login as test1 and wait for the initial dashboard to fully load
        await LoginAndWaitForDashboard("test1");
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
            throw new InvalidOperationException(
                $"Branch '{branchName}' did not appear on dashboard within timeout");

        Log.Information("New branch '{BranchName}' appeared on dashboard successfully", branchName);
    }

    /// <summary>
    ///     Verifies that when an MR is created on an existing branch,
    ///     the dashboard updates the MR status via the refresh polling.
    /// </summary>
    private async Task TestMrStatusUpdatesOnDashboard()
    {
        Log.Information("Testing: MR status updates on loaded dashboard...");

        // Login as test1 and wait for the dashboard
        await LoginAndWaitForDashboard("test1");
        await _browser.TakeScreenshot("live_03_dashboard_before_mr");

        // Push a branch without MR first
        var projectId = _gitLab.GetProjectId("primary-1");
        var branchName = $"feature/mr-test-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _gitLab.CreateBranchWithCommit(projectId, branchName, "test1");

        // Wait for the branch to appear
        var appeared = await WaitForBranchOnDashboard(branchName, 60);
        if (!appeared)
            throw new InvalidOperationException(
                $"Branch '{branchName}' did not appear on dashboard within timeout");

        await _browser.TakeScreenshot("live_04_branch_without_mr");

        // Verify that the branch card shows "Waiting" status (no MR)
        var hasWaitingStatus = await BranchCardHasStatus(branchName, "Waiting");
        if (!hasWaitingStatus)
            throw new InvalidOperationException(
                $"Branch '{branchName}' should show 'Waiting' status before MR creation");

        Log.Information("Branch '{BranchName}' correctly shows 'Waiting' status", branchName);

        // also verify MR icon is grey and tooltip indicates no MR
        var card = _browser.Page.Locator(".merge-group-card").Filter(new() { HasTextString = branchName }).First;
        var mrSpan = card.Locator(".item-mr-icon");
        var mrTooltip = await mrSpan.GetAttributeAsync("title") ?? "";
        var mrColor = await mrSpan.Locator(".v-icon").GetAttributeAsync("data-mr-color") ?? "";
        if (mrTooltip != "MR not created" || mrColor != "grey")
            throw new InvalidOperationException($"Expected MR icon grey/not created before MR, got tooltip='{mrTooltip}' color='{mrColor}'");

        // Create an MR on the branch
        _gitLab.CreateMergeRequest(projectId, branchName, "test1");
        Log.Information("Created MR for branch '{BranchName}', waiting for dashboard update...", branchName);

        // Wait for the status to change from "Waiting" to "Open" or "Ready"
        var mrUpdated = await WaitForBranchStatusChange(branchName, "Waiting", 60);
        await _browser.TakeScreenshot("live_05_branch_with_mr");

        if (!mrUpdated)
            throw new InvalidOperationException(
                $"Status did not change from 'Waiting' for branch '{branchName}' within timeout");

        Log.Information("MR status updated on dashboard for '{BranchName}'", branchName);

        // verify MR icon turned blue with 'MR exists' tooltip
        mrTooltip = await mrSpan.GetAttributeAsync("title") ?? "";
        mrColor = await mrSpan.Locator(".v-icon").GetAttributeAsync("data-mr-color") ?? "";
        if (mrTooltip != "MR exists" || mrColor != "blue")
            throw new InvalidOperationException($"Expected MR icon blue/exists after MR creation, got tooltip='{mrTooltip}' color='{mrColor}'");
    }

    private async Task LoginAndWaitForDashboard(string username)
    {
        // Clear cookies/state
        await _browser.Page.Context.ClearCookiesAsync();
        await Task.Delay(500);

        // Login via OAuth flow
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

        // Navigate to dashboard and wait for SSE to complete
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);

        await WaitForDashboardLoadComplete();
    }

    /// <summary>
    ///     Verifies that when a tracked branch is deleted in GitLab, it disappears from
    ///     the already open dashboard and stays gone after a full page reload.
    /// </summary>
    private async Task TestDeletedBranchDisappearsAndStaysGoneAfterReload()
    {
        Log.Information("Testing: deleted branch disappears and stays gone after reload...");

        await LoginAndWaitForDashboard("test1");
        await _browser.TakeScreenshot("live_06_before_delete_branch_test");

        var projectId = _gitLab.GetProjectId("primary-1");
        var branchName = $"feature/delete-test-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _gitLab.CreateBranchWithCommit(projectId, branchName, "test1");

        var appeared = await WaitForBranchOnDashboard(branchName, 60);
        if (!appeared)
            throw new InvalidOperationException(
                $"Branch '{branchName}' did not appear on dashboard before delete test");

        _gitLab.DeleteBranch(projectId, branchName);
        Log.Information("Deleted branch '{BranchName}', waiting for dashboard removal", branchName);

        var disappeared = await WaitForBranchToDisappearFromDashboard(branchName, 90);
        await _browser.TakeScreenshot("live_07_after_delete_branch_live_update");

        if (!disappeared)
            throw new InvalidOperationException(
                $"Deleted branch '{branchName}' did not disappear from dashboard within timeout");

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
        Log.Information("Waiting for initial SSE stream to complete...");
        for (var s = 0; s < 120; s++)
        {
            var cardCount = await _browser.Page.Locator(".merge-group-card").CountAsync();
            if (cardCount > 0)
            {
                var spinnerCount =
                    await _browser.Page.Locator(".merge-group-card .v-progress-circular").CountAsync();
                var streamingIndicator =
                    await _browser.Page.Locator(".streaming-indicator").CountAsync();

                if (spinnerCount == 0 && streamingIndicator == 0)
                {
                    Log.Information("Dashboard loaded after ~{Seconds}s", s);
                    return;
                }
            }

            await Task.Delay(1000);
        }

        throw new InvalidOperationException("Dashboard SSE stream did not complete within 120s");
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
                Log.Information("Branch '{BranchName}' found on dashboard after ~{Seconds}s",
                    branchName, s);
                return true;
            }

            if (s % 10 == 0)
            {
                Log.Information("Waiting for branch '{BranchName}' to appear... {Seconds}s",
                    branchName, s);
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
                Log.Information("Branch '{BranchName}' disappeared from dashboard after ~{Seconds}s", branchName, s);
                return true;
            }

            if (s % 10 == 0)
            {
                Log.Information("Waiting for branch '{BranchName}' to disappear... {Seconds}s", branchName, s);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private async Task<bool> IsBranchOnDashboard(string branchName)
    {
        var branchElements = _browser.Page.Locator(".merge-group-card .branch-name");
        var count = await branchElements.CountAsync();

        for (var i = 0; i < count; i++)
        {
            var text = (await branchElements.Nth(i).InnerTextAsync()).Trim();
            if (text.Contains(branchName))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Checks if a branch card has a specific group status label (e.g. "Waiting", "Open", "Ready").
    /// </summary>
    private async Task<bool> BranchCardHasStatus(string branchName, string expectedStatus)
    {
        var cards = _browser.Page.Locator(".merge-group-card");
        var cardCount = await cards.CountAsync();

        for (var i = 0; i < cardCount; i++)
        {
            var card = cards.Nth(i);
            var name = (await card.Locator(".branch-name").InnerTextAsync()).Trim();
            if (!name.Contains(branchName)) continue;

            var badge = card.Locator(".card-status-badge");
            if (await badge.CountAsync() > 0)
            {
                var statusText = (await badge.InnerTextAsync()).Trim();
                if (statusText.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Waits for a branch card's item status to change from the given status.
    /// </summary>
    private async Task<bool> WaitForBranchStatusChange(string branchName, string fromStatus, int timeoutSeconds)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            var stillHasOldStatus = await BranchCardHasStatus(branchName, fromStatus);
            if (!stillHasOldStatus && await IsBranchOnDashboard(branchName))
            {
                Log.Information("Status changed from '{FromStatus}' for '{BranchName}' after ~{Seconds}s",
                    fromStatus, branchName, s);
                return true;
            }

            if (s % 10 == 0)
            {
                Log.Information("Waiting for status change from '{FromStatus}' on '{BranchName}'... {Seconds}s",
                    fromStatus, branchName, s);
            }

            await Task.Delay(1000);
        }

        return false;
    }
}
