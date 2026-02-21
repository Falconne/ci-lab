using IntegrationTest.Services;
using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests that the dashboard dynamically updates when new branches are pushed,
///     when MR status changes, and when card ordering changes while polling.
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
        await TestCardReorderAndHoverLockBehavior();

        Log.Information("Dashboard live update tests passed");
    }

    private async Task TestNewBranchAppearsOnDashboard()
    {
        Log.Information("Testing: new branch appears on loaded dashboard...");

        await LoginAndWaitForDashboard("test1");
        await _browser.TakeScreenshot("live_01_initial_dashboard");

        var projectId = _gitLab.GetProjectId("primary-1");
        var branchName = $"feature/live-test-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _gitLab.CreateBranchWithCommit(projectId, branchName, "test1");

        Log.Information(
            "Pushed branch '{BranchName}' to project {ProjectId}, waiting for dashboard to update...",
            branchName,
            projectId);

        var appeared = await WaitForBranchOnDashboard(branchName, 60);
        await _browser.TakeScreenshot("live_02_after_new_branch");

        if (!appeared)
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' did not appear on dashboard within timeout");
        }

        Log.Information("New branch '{BranchName}' appeared on dashboard successfully", branchName);
    }

    private async Task TestMrStatusUpdatesOnDashboard()
    {
        Log.Information("Testing: branch status changes from Waiting to Open/Ready after MR creation...");

        await LoginAndWaitForDashboard("test1");
        await _browser.TakeScreenshot("live_03_dashboard_before_mr");

        var projectId = _gitLab.GetProjectId("primary-1");
        var branchName = $"feature/mr-test-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _gitLab.CreateBranchWithCommit(projectId, branchName, "test1");

        var appeared = await WaitForBranchOnDashboard(branchName, 60);
        if (!appeared)
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' did not appear on dashboard within timeout");
        }

        await _browser.TakeScreenshot("live_04_branch_without_mr");

        var statusBefore = await GetGroupStatusForBranch(branchName);
        if (!string.Equals(statusBefore, "Waiting", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' should be Waiting before MR creation but was '{statusBefore ?? "(null)"}'");
        }

        Log.Information("Branch '{BranchName}' correctly shows Waiting before MR creation", branchName);

        _gitLab.CreateMergeRequest(projectId, branchName, "test1");
        Log.Information("Created MR for branch '{BranchName}', waiting for dashboard update...", branchName);

        var mrUpdated = await WaitForBranchStatus(
            branchName,
            status => string.Equals(status, "Open", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase),
            60);

        await _browser.TakeScreenshot("live_05_branch_with_mr");

        if (!mrUpdated)
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' did not transition to Open/Ready within timeout");
        }

        Log.Information("MR status updated on dashboard for '{BranchName}'", branchName);
    }

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

        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
        await WaitForDashboardLoadComplete();

        await EnsureBranchStaysAbsent(branchName, 20);
        await _browser.TakeScreenshot("live_08_after_delete_branch_reload");

        Log.Information("Deleted branch '{BranchName}' remained absent after dashboard reload", branchName);
    }

    private async Task TestCardReorderAndHoverLockBehavior()
    {
        Log.Information("Testing: card reorder by recency and hover-lock behavior...");

        await LoginAndWaitForDashboard("test1");
        await _browser.TakeScreenshot("live_09_reorder_before_setup");
        await MoveMouseAwayFromCardsAndWaitForUnlock();

        var projectId = _gitLab.GetProjectId("primary-1");
        var olderBranch = $"feature/reorder-old-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _gitLab.CreateBranchWithCommit(projectId, olderBranch, "test1");

        if (!await WaitForBranchOnDashboard(olderBranch, 60))
        {
            throw new InvalidOperationException($"Branch '{olderBranch}' did not appear on dashboard");
        }

        await Task.Delay(1200);

        var newerBranch = $"feature/reorder-new-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _gitLab.CreateBranchWithCommit(projectId, newerBranch, "test1");

        if (!await WaitForBranchOnDashboard(newerBranch, 60))
        {
            throw new InvalidOperationException($"Branch '{newerBranch}' did not appear on dashboard");
        }

        await MoveMouseAwayFromCardsAndWaitForUnlock();

        var reorderedByRecency = await WaitForBranchOrder(newerBranch, olderBranch, 60);
        if (!reorderedByRecency)
        {
            throw new InvalidOperationException(
                $"Expected newer branch '{newerBranch}' to move above '{olderBranch}'");
        }

        Log.Information("Verified recency reorder: '{Newer}' appears above '{Older}'", newerBranch, olderBranch);

        var hoverCard = _browser.Page.Locator($".merge-group-card[data-branch-name='{olderBranch}']").First;
        await hoverCard.HoverAsync();
        Log.Information("Hovering branch card '{BranchName}' to lock reordering", olderBranch);

        var lockedBranch = $"feature/reorder-locked-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _gitLab.CreateBranchWithCommit(projectId, lockedBranch, "test1");

        if (!await WaitForBranchOnDashboard(lockedBranch, 60))
        {
            throw new InvalidOperationException($"Branch '{lockedBranch}' did not appear while hover lock was active");
        }

        var isAtBottomWhileLocked = await WaitForBranchAtBottom(lockedBranch, 30);
        if (!isAtBottomWhileLocked)
        {
            throw new InvalidOperationException(
                $"Expected new branch '{lockedBranch}' to remain at bottom while hover lock is active");
        }

        await _browser.TakeScreenshot("live_10_hover_lock_bottom_insertion");

        await _browser.Page.Mouse.MoveAsync(2, 2);
        Log.Information("Moved mouse off cards; waiting to verify 2s unlock threshold");

        await Task.Delay(1200);
        if (!await IsBranchAtBottom(lockedBranch))
        {
            throw new InvalidOperationException(
                $"Branch '{lockedBranch}' moved before hover unlock threshold elapsed");
        }

        var movedAfterUnlock = await WaitForBranchToMoveUpFromBottom(lockedBranch, 30);
        if (!movedAfterUnlock)
        {
            throw new InvalidOperationException(
                $"Branch '{lockedBranch}' did not reorder after hover unlock threshold");
        }

        await _browser.TakeScreenshot("live_11_hover_unlock_reordered");
        Log.Information("Verified hover-lock behavior with delayed unlock and reorder");
    }

    private async Task MoveMouseAwayFromCardsAndWaitForUnlock()
    {
        await _browser.Page.Mouse.MoveAsync(2, 2);
        await Task.Delay(2300);
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

        await WaitForDashboardLoadComplete();
    }

    private async Task WaitForDashboardLoadComplete()
    {
        Log.Information("Waiting for initial SSE stream to complete...");
        for (var second = 0; second < 120; second++)
        {
            var cardsExist = await _browser.Page.Locator(".merge-group-card").CountAsync() > 0;
            if (cardsExist)
            {
                var spinnerCount =
                    await _browser.Page.Locator(".merge-group-card .v-progress-circular").CountAsync();
                if (spinnerCount == 0)
                {
                    Log.Information("Dashboard loaded after ~{Seconds}s", second);
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

    private async Task<bool> WaitForBranchOnDashboard(string branchName, int timeoutSeconds)
    {
        for (var second = 0; second < timeoutSeconds; second++)
        {
            if (await IsBranchOnDashboard(branchName))
            {
                Log.Information("Branch '{BranchName}' found on dashboard after ~{Seconds}s", branchName, second);
                return true;
            }

            if (second % 10 == 0)
            {
                Log.Information("Waiting for branch '{BranchName}' to appear... {Seconds}s", branchName, second);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private async Task<bool> WaitForBranchToDisappearFromDashboard(string branchName, int timeoutSeconds)
    {
        for (var second = 0; second < timeoutSeconds; second++)
        {
            if (!await IsBranchOnDashboard(branchName))
            {
                Log.Information("Branch '{BranchName}' disappeared from dashboard after ~{Seconds}s", branchName, second);
                return true;
            }

            if (second % 10 == 0)
            {
                Log.Information("Waiting for branch '{BranchName}' to disappear... {Seconds}s", branchName, second);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private async Task<bool> IsBranchOnDashboard(string branchName)
    {
        var branches = await GetCardBranchOrder();
        return branches.Any(branch =>
            branch.Contains(branchName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> WaitForBranchStatus(string branchName, Func<string?, bool> predicate, int timeoutSeconds)
    {
        for (var second = 0; second < timeoutSeconds; second++)
        {
            var status = await GetGroupStatusForBranch(branchName);
            if (predicate(status))
            {
                Log.Information(
                    "Branch '{BranchName}' reached expected status '{Status}' after ~{Seconds}s",
                    branchName,
                    status,
                    second);
                return true;
            }

            if (second % 10 == 0)
            {
                Log.Information(
                    "Waiting for branch '{BranchName}' status transition... current='{Status}', {Seconds}s",
                    branchName,
                    status ?? "(null)",
                    second);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private async Task<string?> GetGroupStatusForBranch(string branchName)
    {
        var cards = _browser.Page.Locator(".merge-group-card");
        var cardCount = await cards.CountAsync();

        for (var i = 0; i < cardCount; i++)
        {
            var card = cards.Nth(i);
            var candidate = (await card.GetAttributeAsync("data-branch-name") ?? string.Empty).Trim();
            if (!candidate.Contains(branchName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var statusChip = card.Locator(".group-status-chip");
            if (await statusChip.CountAsync() == 0)
            {
                return null;
            }

            return (await statusChip.First.InnerTextAsync()).Trim();
        }

        return null;
    }

    private async Task<List<string>> GetCardBranchOrder()
    {
        var cards = _browser.Page.Locator(".merge-group-card");
        var cardCount = await cards.CountAsync();
        var branches = new List<string>(cardCount);

        for (var i = 0; i < cardCount; i++)
        {
            var card = cards.Nth(i);
            var branch = (await card.GetAttributeAsync("data-branch-name") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(branch))
            {
                branch = (await card.Locator(".merge-group-branch").First.InnerTextAsync()).Trim();
            }

            if (!string.IsNullOrWhiteSpace(branch))
            {
                branches.Add(branch);
            }
        }

        return branches;
    }

    private async Task<bool> WaitForBranchOrder(string branchThatShouldComeFirst, string branchThatShouldComeAfter, int timeoutSeconds)
    {
        for (var second = 0; second < timeoutSeconds; second++)
        {
            var order = await GetCardBranchOrder();
            var firstIndex = order.FindIndex(branch =>
                branch.Contains(branchThatShouldComeFirst, StringComparison.OrdinalIgnoreCase));
            var secondIndex = order.FindIndex(branch =>
                branch.Contains(branchThatShouldComeAfter, StringComparison.OrdinalIgnoreCase));

            if (firstIndex >= 0 && secondIndex >= 0 && firstIndex < secondIndex)
            {
                return true;
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private async Task<bool> WaitForBranchAtBottom(string branchName, int timeoutSeconds)
    {
        for (var second = 0; second < timeoutSeconds; second++)
        {
            if (await IsBranchAtBottom(branchName))
            {
                Log.Information("Branch '{BranchName}' reached bottom position after ~{Seconds}s", branchName, second);
                return true;
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private async Task<bool> IsBranchAtBottom(string branchName)
    {
        var order = await GetCardBranchOrder();
        if (order.Count == 0)
        {
            return false;
        }

        var index = order.FindIndex(branch =>
            branch.Contains(branchName, StringComparison.OrdinalIgnoreCase));

        return index == order.Count - 1;
    }

    private async Task<bool> WaitForBranchToMoveUpFromBottom(string branchName, int timeoutSeconds)
    {
        for (var second = 0; second < timeoutSeconds; second++)
        {
            var order = await GetCardBranchOrder();
            if (order.Count == 0)
            {
                await Task.Delay(1000);
                continue;
            }

            var index = order.FindIndex(branch =>
                branch.Contains(branchName, StringComparison.OrdinalIgnoreCase));

            if (index >= 0 && index < order.Count - 1)
            {
                Log.Information(
                    "Branch '{BranchName}' moved up from bottom after ~{Seconds}s (index {Index} of {Count})",
                    branchName,
                    second,
                    index,
                    order.Count);
                return true;
            }

            await Task.Delay(1000);
        }

        return false;
    }
}
