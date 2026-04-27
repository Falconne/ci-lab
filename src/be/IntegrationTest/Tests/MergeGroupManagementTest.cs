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
public class MergeGroupManagementTest : IDisposable
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
            Path.Combine(TestConfig.ScreenshotDir, "merge-group-management"));

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

        await LoginAndWaitForDashboard("test1");
        await _browser.TakeScreenshot("subscription_01_dashboard");

        // Click the first merge group card to go to details
        var firstCard = _browser.Page.Locator(".merge-group-card").First;
        var mergeGroupName = await firstCard.Locator(".branch-name, .branch-subtitle").First.InnerTextAsync();
        Log.Information("Selected merge group: {Name}", mergeGroupName);

        await firstCard.ClickAsync();
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

        var matchingCards = _browser.Page.Locator(".merge-group-card")
            .Filter(new LocatorFilterOptions { HasTextString = mergeGroupName });

        var matchCount = await matchingCards.CountAsync();
        Log.Information(
            "Merge group '{Name}' cards on dashboard after unsubscribe: {Count}",
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

        matchingCards = _browser.Page.Locator(".merge-group-card")
            .Filter(new LocatorFilterOptions { HasTextString = mergeGroupName });

        matchCount = await matchingCards.CountAsync();
        Log.Information(
            "Merge group '{Name}' cards on dashboard after resubscribe: {Count}",
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
    ///     3. Click "Add Merge Request..." and enter the MR URL
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
            await LoginAndWaitForDashboard("test1");

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

            await Task.Delay(2000);
            await _browser.TakeScreenshot("add_mr_01_details");

            // Count existing branches
            var initialBranchCount = await _browser.Page.Locator(".branch-card").CountAsync();
            Log.Information("Initial branch count: {Count}", initialBranchCount);

            // Click "Add Existing Merge Request..."
            var addMergeRequestBtn = _browser.Page.Locator("button:has-text('Add Existing Merge Request')");
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

            // Wait for dialog to close and data to refresh
            await Task.Delay(3000);
            await _browser.TakeScreenshot("add_mr_04_after_add");

            // Verify dialog is closed
            var dialogCount = await _browser.Page.Locator(".v-dialog--active").CountAsync();
            if (dialogCount > 0)
            {
                // Check for error message in dialog
                var errorText =
                    await _browser.Page.Locator(".v-dialog .v-messages__message").InnerTextAsync();

                throw new InvalidOperationException(
                    $"Dialog still open after submit. Error: {errorText}");
            }

            // Verify branch count increased
            var newBranchCount = await _browser.Page.Locator(".branch-card").CountAsync();
            Log.Information("Branch count after add: {Count}", newBranchCount);

            if (newBranchCount <= initialBranchCount)
            {
                throw new InvalidOperationException(
                    $"Expected branch count to increase from {initialBranchCount}, got {newBranchCount}");
            }

            // Verify the new branch's project name appears
            var newBranchLink = _browser.Page.Locator(".branch-title-link, .branch-title-text")
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
            await LoginAndWaitForDashboard("test1");
            await _browser.TakeScreenshot("find_mr_01_dashboard");

            // Type the MR URL into the filter box.
            // Use Exact = true to avoid matching the clearable icon's aria-label which contains the same text.
            var filterInput = _browser.Page.GetByLabel("Filter by branch name or Merge Request URL", new PageGetByLabelOptions { Exact = true });
            await filterInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            await filterInput.FillAsync(mrWebUrl);
            await _browser.TakeScreenshot("find_mr_02_url_filled");

            // Mergician auto-discovers branches quickly, so the merge group may already be on the dashboard
            // by the time we type the MR URL. Wait for either outcome:
            //   (a) A filtered merge group card appears (MR is already tracked) → click it to navigate
            //   (b) The "Open MR as Merge Group" button appears (MR not yet tracked) → click it to open
            await _browser.Page.WaitForFunctionAsync(
                "() => document.querySelectorAll('.merge-group-card').length > 0 || document.querySelector('.open-mr-btn') !== null",
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
                await _browser.Page.Locator(".merge-group-card").First.ClickAsync();
            }

            // Wait for navigation to the merge group details page
            await _browser.Page.WaitForURLAsync(
                url => url.Contains("/merge-group/"),
                new PageWaitForURLOptions { Timeout = 15000 });

            await Task.Delay(3000);
            await _browser.TakeScreenshot("find_mr_04_details_page");

            // Verify we're on a merge group details page
            var currentUrl = _browser.Page.Url;
            Log.Information("Navigated to: {Url}", currentUrl);

            if (!currentUrl.Contains("/merge-group/"))
            {
                throw new InvalidOperationException(
                    $"Expected to be on merge group details page, but URL is: {currentUrl}");
            }

            // Verify the branch from the MR appears on the page
            var branchCards = _browser.Page.Locator(".branch-card");
            var cardCount = await branchCards.CountAsync();
            Log.Information("Branch cards on details page: {Count}", cardCount);

            if (cardCount == 0)
            {
                throw new InvalidOperationException(
                    "Expected at least one branch card on the details page");
            }

            // Check that secondary-1 project is visible
            var projectLink = _browser.Page.Locator(".branch-title-link, .branch-title-text")
                .Filter(new LocatorFilterOptions { HasTextString = "secondary-1" });

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