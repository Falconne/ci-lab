using IntegrationTest.Services;
using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests the merge group management features:
///     1. Subscribe/unsubscribe from merge groups
///     2. Add merge request to a merge group by URL
///     3. Find merge group by merge request URL from the app bar
/// </summary>
public class MergeGroupManagementTest
{
    private readonly BrowserService _browser;

    private readonly GitLabTestHelper _gitLab = new();

    public MergeGroupManagementTest(BrowserService browser)
    {
        _browser = browser;
    }

    public async Task Run()
    {
        _browser.SetScreenshotDir(Path.Combine(TestConfig.ScreenshotDir, "merge-group-management"));

        await TestSubscriptionToggle();
        await TestAddMergeRequestToGroup();
        await TestFindByMergeRequest();

        Log.Information("All merge group management tests passed");
    }

    /// <summary>
    ///     Tests the subscribe/unsubscribe button on the merge group details page.
    ///     1. Navigate to a merge group, verify subscription button shows "In my Merge Groups"
    ///     2. Click to unsubscribe, verify button changes to "Add to my Merge Groups"
    ///     3. Verify the merge group disappears from the dashboard
    ///     4. Navigate directly to the merge group details page
    ///     5. Re-subscribe, verify button changes back
    ///     6. Verify the merge group reappears on the dashboard
    /// </summary>
    private async Task TestSubscriptionToggle()
    {
        Log.Information("Testing: subscription toggle...");

        await LoginHelper.EnsureLoggedIn(_browser, "test1");
        await _browser.TakeScreenshot("subscription_01_dashboard");

        // Click the first merge group row to go to details
        var firstRow = _browser.Page.Locator(".grid-row[data-mg-name]").First;
        var mergeGroupName = (await firstRow.GetAttributeAsync("data-mg-name") ?? "").Trim();
        Log.Information("Selected merge group: {Name}", mergeGroupName);

        await firstRow.ClickAsync();
        await _browser.Page.WaitForURLAsync(
            url => url.Contains("/merge-group/"),
            new PageWaitForURLOptions { Timeout = 15000 });

        await Task.Delay(2000);
        await _browser.TakeScreenshot("subscription_02_details");

        // Extract merge group ID from URL
        var detailsUrl = _browser.Page.Url;
        var mergeGroupId = detailsUrl.Split("/merge-group/")[1].Split("?")[0];

        // Verify subscription button shows "Untrack" (user is subscribed by default)
        var subBtn = _browser.Page.Locator(".subscription-btn");
        await subBtn.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        var btnText = await subBtn.InnerTextAsync();
        Log.Information("Subscription button text: {Text}", btnText);

        if (!btnText.Contains("Untrack"))
        {
            throw new InvalidOperationException(
                $"Expected subscription button to say 'Untrack', got '{btnText}'");
        }

        // Unsubscribe
        await subBtn.ClickAsync();
        await Task.Delay(2000);
        await _browser.TakeScreenshot("subscription_03_after_unsubscribe");

        btnText = await subBtn.InnerTextAsync();
        Log.Information("Subscription button after unsubscribe: {Text}", btnText);

        if (!btnText.Contains("Track"))
        {
            throw new InvalidOperationException(
                $"Expected subscription button to say 'Track', got '{btnText}'");
        }

        // Navigate to dashboard and verify the merge group is gone
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(3000);

        // Wait for dashboard to stabilize
        await DashboardWaitHelper.WaitForDashboardReady(_browser.Page, 30);
        await _browser.TakeScreenshot("subscription_04_dashboard_after_unsubscribe");

        var matchingRows = _browser.Page.Locator($"[data-mg-name='{mergeGroupName}']");

        var matchCount = await matchingRows.CountAsync();
        Log.Information(
            "Merge group '{Name}' rows on dashboard after unsubscribe: {Count}",
            mergeGroupName,
            matchCount);

        if (matchCount > 0)
        {
            throw new InvalidOperationException(
                $"Expected merge group '{mergeGroupName}' to be absent from dashboard after unsubscribe");
        }

        // Navigate directly to the merge group details page and re-subscribe
        await _browser.Navigate($"{TestConfig.MergicianUrl}/merge-group/{mergeGroupId}");
        await Task.Delay(3000);
        await _browser.TakeScreenshot("subscription_05_details_direct");

        subBtn = _browser.Page.Locator(".subscription-btn");
        await subBtn.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        btnText = await subBtn.InnerTextAsync();
        Log.Information("Subscription button on direct navigation: {Text}", btnText);

        if (!btnText.Contains("Track"))
        {
            throw new InvalidOperationException(
                $"Expected 'Track' after direct navigation, got '{btnText}'");
        }

        // Re-subscribe
        await subBtn.ClickAsync();
        await Task.Delay(2000);
        await _browser.TakeScreenshot("subscription_06_after_resubscribe");

        btnText = await subBtn.InnerTextAsync();
        if (!btnText.Contains("Untrack"))
        {
            throw new InvalidOperationException(
                $"Expected 'Untrack' after resubscribe, got '{btnText}'");
        }

        // Navigate to dashboard and verify the merge group is back
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
        await DashboardWaitHelper.WaitForDashboardReady(_browser.Page, 60);
        await _browser.TakeScreenshot("subscription_07_dashboard_after_resubscribe");

        matchingRows = _browser.Page.Locator($"[data-mg-name='{mergeGroupName}']");

        matchCount = await matchingRows.CountAsync();
        Log.Information(
            "Merge group '{Name}' rows on dashboard after resubscribe: {Count}",
            mergeGroupName,
            matchCount);

        if (matchCount == 0)
        {
            throw new InvalidOperationException(
                $"Expected merge group '{mergeGroupName}' to be back on dashboard after resubscribe");
        }

        Log.Information("Subscription toggle test passed");
    }

    /// <summary>
    ///     Tests adding a merge request to a merge group by URL.
    ///     1. Create a branch + MR in a project that the test user doesn't have on a MG yet
    ///     2. Navigate to an existing merge group
    ///     3. Click "Add Another MR to Group..." and enter the MR URL
    ///     4. Verify the branch appears in the merge group
    /// </summary>
    private async Task TestAddMergeRequestToGroup()
    {
        Log.Information("Testing: add merge request to group...");

        // Create a branch + MR that's not in any existing merge group
        var projectId = _gitLab.GetProjectId("primary-1");
        var branchName = $"feature/add-mr-test-{DateTime.UtcNow:HHmmss}";
        _gitLab.CreateBranchWithCommit(projectId, branchName, "test1");
        var (_, mrWebUrl) = _gitLab.CreateMergeRequestWithUrl(
            projectId,
            branchName,
            "test1",
            "Test MR for add-by-URL");

        Log.Information("Created test MR at: {Url}", mrWebUrl);

        try
        {
            await LoginHelper.NavigateToDashboard(_browser);

            // Click a known merge group (feature/alpha) to avoid hitting the auto-created
            // group for our test branch. The test MR's branch is from primary-1 which is
            // not already in feature/alpha, so the branch count should increase.
            var targetCard = _browser.Page.Locator(".merge-group-card")
                .Filter(new LocatorFilterOptions { HasTextString = "feature/alpha" });

            var targetCount = await targetCard.CountAsync();
            Log.Information("Found {Count} merge group card(s) matching 'feature/alpha'", targetCount);

            if (targetCount == 0)
            {
                throw new InvalidOperationException(
                    "No merge group card found with 'feature/alpha' on dashboard");
            }

            await targetCard.First.ClickAsync();
            await _browser.Page.WaitForURLAsync(
                url => url.Contains("/merge-group/"),
                new PageWaitForURLOptions { Timeout = 15000 });

            // Wait for existing branch cards to load before capturing the baseline count
            await _browser.Page.Locator(".branch-card").First.WaitForAsync(
                new LocatorWaitForOptions { Timeout = 15000 });
            await _browser.TakeScreenshot("add_mr_01_details");

            // Count existing branches
            var initialBranchCount = await _browser.Page.Locator(".branch-card").CountAsync();
            Log.Information("Initial branch count: {Count}", initialBranchCount);

            // Click "Add Another MR to Group..."
            var addMergeRequestBtn = _browser.Page.Locator("button:has-text('Add Another MR to Group')");
            await addMergeRequestBtn.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            await addMergeRequestBtn.ClickAsync();
            await Task.Delay(1000);
            await _browser.TakeScreenshot("add_mr_02_dialog_open");

            // Fill in the MR URL
            var urlField = _browser.Page.Locator(".v-dialog .v-text-field input");
            await urlField.FillAsync(mrWebUrl);
            await _browser.TakeScreenshot("add_mr_03_url_filled");

            // Click "Add"
            var addBtn = _browser.Page.Locator(".v-dialog .v-card-actions button:has-text('Add')");
            await addBtn.ClickAsync();

            // Wait for the dialog to close (success) or timeout (failure with error message visible)
            await _browser.Page.WaitForFunctionAsync(
                "() => document.querySelector('.v-dialog--active') === null",
                null, new PageWaitForFunctionOptions { Timeout = 10000 });
            await _browser.TakeScreenshot("add_mr_04_after_add");

            // Poll until the branch count increases — the page refreshes asynchronously after the add
            var newBranchCount = 0;
            await _browser.Page.WaitForFunctionAsync(
                $"() => document.querySelectorAll('.branch-card').length > {initialBranchCount}",
                null, new PageWaitForFunctionOptions { Timeout = 15000 });
            newBranchCount = await _browser.Page.Locator(".branch-card").CountAsync();
            Log.Information("Branch count after add: {Count}", newBranchCount);

            if (newBranchCount <= initialBranchCount)
            {
                throw new InvalidOperationException(
                    $"Expected branch count to increase from {initialBranchCount}, got {newBranchCount}");
            }

            // Verify the new branch's project name appears.
            // The project name shows in .branch-title-link/.branch-title-text when there is no MR
            // title yet, and in .branch-subtitle-link/.branch-subtitle-text once MR details have
            // been synced. Accept either so the check is timing-independent.
            var newBranchLink = _browser.Page
                .Locator(".branch-title-link, .branch-title-text, .branch-subtitle-link, .branch-subtitle-text")
                .Filter(new LocatorFilterOptions { HasTextString = "primary-1" });

            var linkCount = await newBranchLink.CountAsync();
            Log.Information("Branch entries with 'primary-1': {Count}", linkCount);

            if (linkCount == 0)
            {
                throw new InvalidOperationException(
                    "Expected to find a branch entry from 'primary-1' project after adding MR");
            }

            Log.Information("Add merge request to group test passed");
        }
        finally
        {
            // Cleanup: close MR and delete branch
            try
            {
                var mrIid = int.Parse(mrWebUrl.Split("/merge_requests/")[1].Split('?')[0].Split('#')[0]);
                _gitLab.CloseMergeRequest(projectId, mrIid);
                _gitLab.DeleteBranch(projectId, branchName);
            }
            catch (Exception ex)
            {
                Log.Warning("Cleanup failed: {Message}", ex.Message);
            }
        }
    }

    /// <summary>
    ///     Tests the "Find by MR URL" feature integrated into the dashboard filter box.
    ///     1. Create a branch + MR with a unique name
    ///     2. Type the MR URL into the filter box on the dashboard
    ///     3. Click the "Open MR as Merge Group" button that appears
    ///     4. Verify navigation to the merge group details page
    ///     5. Verify the branch appears on the details page
    /// </summary>
    private async Task TestFindByMergeRequest()
    {
        Log.Information("Testing: find by merge request...");

        // Create a branch + MR with a unique name
        var projectId = _gitLab.GetProjectId("secondary-1");
        var branchName = $"feature/find-mr-test-{DateTime.UtcNow:HHmmss}";
        _gitLab.CreateBranchWithCommit(projectId, branchName, "test1");
        var (_, mrWebUrl) = _gitLab.CreateMergeRequestWithUrl(
            projectId,
            branchName,
            "test1",
            "Test MR for find-by-URL");

        Log.Information("Created test MR at: {Url}", mrWebUrl);

        try
        {
            await LoginHelper.NavigateToDashboard(_browser);
            await _browser.TakeScreenshot("find_mr_01_dashboard");

            // Type the MR URL into the filter box.
            // Use Exact = true to avoid matching the clearable icon's aria-label which contains the same text.
            var filterInput = _browser.Page.GetByLabel("Filter by branch name or Merge Request URL", new PageGetByLabelOptions { Exact = true });
            await filterInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            await filterInput.FillAsync(mrWebUrl);
            await _browser.TakeScreenshot("find_mr_02_url_filled");

            // Mergician auto-discovers branches quickly, so the merge group may already be on the dashboard
            // by the time we type the MR URL. Wait for either outcome:
            //   (a) A filtered merge group card for our specific branch appears (MR is already tracked)
            //   (b) The "Open MR as Merge Group" button appears (MR not yet tracked)
            // Require the specific branch name in any matching card to avoid acting on unrelated cards
            // that may still be in the DOM during filter transitions.
            var jsBranchName = branchName.Replace("\\", "\\\\").Replace("'", "\\'");
            await _browser.Page.WaitForFunctionAsync(
                $"() => document.querySelector('.open-mr-btn') !== null || " +
                $"Array.from(document.querySelectorAll('.merge-group-card')).some(c => c.textContent.includes('{jsBranchName}'))",
                null, new PageWaitForFunctionOptions { Timeout = 15000 });
            await _browser.TakeScreenshot("find_mr_03_filtered");

            var openMRBtn = _browser.Page.Locator(".open-mr-btn");
            if (await openMRBtn.IsVisibleAsync())
            {
                Log.Information("MR not yet tracked - clicking 'Open MR as Merge Group' button");
                await openMRBtn.ClickAsync();
            }
            else
            {
                Log.Information("MR already tracked - clicking the filtered merge group card");
                var matchingCard = _browser.Page.Locator(".merge-group-card")
                    .Filter(new LocatorFilterOptions { HasTextString = branchName });
                await matchingCard.First.ClickAsync();
            }

            // Wait for navigation to the merge group details page
            await _browser.Page.WaitForURLAsync(
                url => url.Contains("/merge-group/"),
                new PageWaitForURLOptions { Timeout = 15000 });

            // Wait for the merge group header to show the correct branch name, confirming we
            // navigated to the right merge group (not another group that also contains secondary-1).
            // Branch name shows in .header-mr-subtitle (single-MR layout) or .header-title (multi/no-MR layout).
            var groupHeader = _browser.Page.Locator(".header-mr-subtitle, .header-title")
                .Filter(new LocatorFilterOptions { HasTextString = branchName });
            await groupHeader.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            await _browser.TakeScreenshot("find_mr_04_details_page");

            Log.Information("Navigated to: {Url}", _browser.Page.Url);

            // Wait for branch cards to appear — they are loaded asynchronously after the header renders
            var branchCards = _browser.Page.Locator(".branch-card");
            await branchCards.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            var cardCount = await branchCards.CountAsync();
            Log.Information("Branch cards on details page: {Count}", cardCount);

            if (cardCount == 0)
            {
                throw new InvalidOperationException(
                    "Expected at least one branch card on the details page");
            }

            // Check that secondary-1 project is visible.
            // The project name shows in .branch-title-link/.branch-title-text when there is no MR
            // title yet, and in .branch-subtitle-link/.branch-subtitle-text once MR details have
            // been synced. Accept either so the check is timing-independent.
            var projectLink = _browser.Page
                .Locator(".branch-title-link, .branch-title-text, .branch-subtitle-link, .branch-subtitle-text")
                .Filter(new LocatorFilterOptions { HasTextString = "secondary-1" });

            await projectLink.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            var projectLinkCount = await projectLink.CountAsync();
            Log.Information("Branch entries with 'secondary-1': {Count}", projectLinkCount);

            if (projectLinkCount == 0)
            {
                throw new InvalidOperationException(
                    "Expected to find 'secondary-1' project on the merge group details page");
            }

            Log.Information("Find by merge request test passed");
        }
        finally
        {
            // Cleanup: close MR and delete branch
            try
            {
                var mrIid = int.Parse(mrWebUrl.Split("/merge_requests/")[1].Split('?')[0].Split('#')[0]);
                _gitLab.CloseMergeRequest(projectId, mrIid);
                _gitLab.DeleteBranch(projectId, branchName);
            }
            catch (Exception ex)
            {
                Log.Warning("Cleanup failed: {Message}", ex.Message);
            }
        }
    }

}