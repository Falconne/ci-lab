using IntegrationTest.Services;
using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests the auto merge toggle feature on the merge group details page
///     and verifies the auto merge badge appears on the dashboard when enabled.
///     Uses Playwright to interact with the actual UI.
/// </summary>
public class AutoMergeToggleTest
{
    private readonly BrowserService _browser;

    public AutoMergeToggleTest(BrowserService browser)
    {
        _browser = browser;
    }

    public async Task Run()
    {
        _browser.SetScreenshotDir(Path.Combine(TestConfig.ScreenshotDir, "auto-merge"));

        await TestAutoMergeToggle();

        Log.Information("Auto merge toggle tests passed");
    }

    /// <summary>
    ///     Navigates to a merge group details page, toggles auto merge on,
    ///     verifies the toggle state persists, then checks the dashboard shows
    ///     the auto merge badge. Finally, disables auto merge and verifies
    ///     the badge disappears from the dashboard.
    /// </summary>
    private async Task TestAutoMergeToggle()
    {
        Log.Information("Testing: auto merge toggle and dashboard badge...");

        await LoginHelper.EnsureLoggedIn(_browser, "test1");
        await _browser.TakeScreenshot("auto_merge_01_dashboard");

        // Verify no auto merge badge initially
        var badgeCount = await _browser.Page.Locator(".auto-merge-badge").CountAsync();
        Log.Information("Initial auto merge badges on dashboard: {Count}", badgeCount);
        if (badgeCount > 0)
        {
            throw new InvalidOperationException(
                "Expected no auto merge badges on dashboard initially");
        }

        // Find a merge group card that:
        //   1. Has all branches with MRs (no .item-no-mr) so the auto-merge toggle is enabled.
        //   2. Is currently blocked or waiting so enabling auto-merge won't immediately merge
        //      the group (which would remove it from the dashboard before we can verify the
        //      auto-merge badge). Draft MRs are the safest choice because the AutoMergeService
        //      will not merge them regardless of pipeline state.
        var allCards = _browser.Page.Locator(".merge-group-card");
        ILocator? targetCard = null;
        ILocator? fallbackCard = null;
        var totalCards = await allCards.CountAsync();
        for (var i = 0; i < totalCards; i++)
        {
            var card = allCards.Nth(i);
            var isBlockedOrWaiting =
                await card.Locator(".card-status-badge.status-blocked").CountAsync() > 0 ||
                await card.Locator(".card-status-badge.status-waiting").CountAsync() > 0;
            var hasNoMrBranch = await card.Locator(".item-no-mr").CountAsync() > 0;
            if (!isBlockedOrWaiting || hasNoMrBranch)
                continue;

            // Prefer draft-MR cards: auto-merge will not execute on a draft MR, making them
            // a stable choice that won't disappear from the dashboard during the test.
            var hasDraftMr = await card.GetByText("Draft:").CountAsync() > 0;
            if (hasDraftMr)
            {
                targetCard = card;
                break;
            }

            fallbackCard ??= card;
        }

        targetCard ??= fallbackCard;

        if (targetCard == null)
        {
            throw new InvalidOperationException(
                "No blocked/waiting merge group card with all branches having MRs found; cannot safely run auto merge toggle test");
        }

        var targetBranchName = (await targetCard.Locator(".branch-name, .branch-subtitle").First.InnerTextAsync()).Trim();
        Log.Information("Selected card for auto merge toggle test: '{BranchName}'", targetBranchName);

        await targetCard.ClickAsync();
        await _browser.Page.WaitForURLAsync(
            url => url.Contains("/merge-group/"),
            new PageWaitForURLOptions { Timeout = 15000 });

        await Task.Delay(2000);
        await _browser.TakeScreenshot("auto_merge_02_details_page");

        // Verify the auto merge toggle exists and is off
        var autoMergeSwitch = _browser.Page.Locator(".auto-merge-controls .v-switch").First;
        await autoMergeSwitch.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        var switchInput = autoMergeSwitch.Locator("input[type='checkbox']");

        // Wait for the toggle to become enabled (permission check can take a moment)
        Log.Information("Waiting for auto merge toggle to become enabled...");
        var enabledDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < enabledDeadline)
        {
            if (await switchInput.IsEnabledAsync()) break;
            await Task.Delay(300);
        }

        if (!await switchInput.IsEnabledAsync())
        {
            throw new InvalidOperationException(
                "Auto merge toggle never became enabled (permissions check did not resolve)");
        }

        var isChecked = await switchInput.IsCheckedAsync();
        Log.Information("Auto merge toggle initial state: {State}", isChecked);

        if (isChecked)
        {
            throw new InvalidOperationException(
                "Expected auto merge toggle to be off initially");
        }

        // Enable auto merge by clicking the toggle
        await autoMergeSwitch.ClickAsync();
        await _browser.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(1500);
        await _browser.TakeScreenshot("auto_merge_03_after_toggle_on");

        // Verify it's now enabled
        isChecked = await switchInput.IsCheckedAsync();
        Log.Information("Auto merge toggle state after click: {State}", isChecked);

        if (!isChecked)
        {
            throw new InvalidOperationException(
                "Expected auto merge toggle to be on after click");
        }

        // Verify auto rebase is also enabled (enabling auto merge enables auto rebase)
        var autoRebaseSwitch = _browser.Page.Locator(".auto-merge-controls .v-switch").Nth(1);
        var rebaseInput = autoRebaseSwitch.Locator("input[type='checkbox']");
        var rebaseChecked = await rebaseInput.IsCheckedAsync();
        Log.Information("Auto rebase toggle state after enabling auto merge: {State}", rebaseChecked);

        if (!rebaseChecked)
        {
            throw new InvalidOperationException(
                "Expected auto rebase to be enabled when auto merge is on");
        }

        // Navigate back to dashboard and check for the auto merge badge
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
        await DashboardWaitHelper.WaitForDashboardReady(_browser.Page, 60);
        await _browser.TakeScreenshot("auto_merge_04_dashboard_with_badge");

        badgeCount = await _browser.Page.Locator(".auto-merge-badge").CountAsync();
        Log.Information("Auto merge badges on dashboard after enabling: {Count}", badgeCount);

        if (badgeCount == 0)
        {
            throw new InvalidOperationException(
                "Expected at least one auto merge badge on dashboard after enabling auto merge");
        }

        // Navigate back to details and disable auto merge.
        // Find the card with the auto merge badge specifically (the one we just enabled).
        var cardWithBadge = _browser.Page
            .Locator(".merge-group-card")
            .Filter(new LocatorFilterOptions { Has = _browser.Page.Locator(".auto-merge-badge") });

        await cardWithBadge.First.ClickAsync();
        await _browser.Page.WaitForURLAsync(
            url => url.Contains("/merge-group/"),
            new PageWaitForURLOptions { Timeout = 15000 });

        await Task.Delay(2000);

        autoMergeSwitch = _browser.Page.Locator(".auto-merge-controls .v-switch").First;
        await autoMergeSwitch.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        switchInput = autoMergeSwitch.Locator("input[type='checkbox']");
        isChecked = await switchInput.IsCheckedAsync();
        Log.Information("Auto merge toggle state on return: {State}", isChecked);

        if (!isChecked)
        {
            throw new InvalidOperationException(
                "Expected auto merge toggle to still be on after returning");
        }

        // Disable auto merge
        await autoMergeSwitch.ClickAsync();
        await _browser.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(1500);
        await _browser.TakeScreenshot("auto_merge_05_after_toggle_off");

        isChecked = await switchInput.IsCheckedAsync();
        Log.Information("Auto merge toggle state after disabling: {State}", isChecked);

        if (isChecked)
        {
            throw new InvalidOperationException(
                "Expected auto merge toggle to be off after disabling");
        }

        // Navigate back to dashboard and verify badge is gone
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
        await DashboardWaitHelper.WaitForDashboardReady(_browser.Page, 60);
        await _browser.TakeScreenshot("auto_merge_06_dashboard_no_badge");

        badgeCount = await _browser.Page.Locator(".auto-merge-badge").CountAsync();
        Log.Information("Auto merge badges on dashboard after disabling: {Count}", badgeCount);

        if (badgeCount > 0)
        {
            throw new InvalidOperationException(
                "Expected no auto merge badges on dashboard after disabling auto merge");
        }

        Log.Information("Auto merge toggle and dashboard badge test passed");
    }
}