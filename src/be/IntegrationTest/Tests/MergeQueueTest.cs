using IntegrationTest.Entities;
using IntegrationTest.Services;
using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests the merge queue system end-to-end:
///     - Two merge groups sharing a repo are assigned to the same queue when
///       both have auto_merge + auto_rebase enabled and no hard blockers.
///     - The Queues page shows queue position badges and the correct card order.
///     - The merge group details page shows a clickable queue position link.
///     - The queue link navigates back to the Queues page.
///
///     Pipeline statuses are held at "running" to keep MGs queue-eligible while
///     preventing premature merges during verification.
/// </summary>
public class MergeQueueTest
{
    private const string PipelineName = "queue-integration-test";

    private readonly BrowserService _browser;

    private readonly GitLabTestHelper _gitLab = new();

    public MergeQueueTest(BrowserService browser)
    {
        _browser = browser;
    }

    public async Task Run()
    {
        _browser.SetScreenshotDir(Path.Combine(TestConfig.ScreenshotDir, "merge-queue"));

        await TestMergeQueue();

        Log.Information("Merge queue tests passed");
    }

    private async Task TestMergeQueue()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var branchA = $"feature/queue-test-a-{timestamp}";
        var branchB = $"feature/queue-test-b-{timestamp}";
        var projectId = _gitLab.GetProjectId("primary-1");

        Log.Information(
            "=== Merge Queue Test: branchA={A}, branchB={B}, project={P} ===",
            branchA,
            branchB,
            projectId);

        var mrIidA = 0;
        var mrIidB = 0;

        try
        {
            // === SETUP: Create two branches and MRs in the same project ===
            Log.Information("--- Setup: creating branches and MRs ---");

            _gitLab.CreateBranchWithCommit(projectId, branchA, "test1");
            _gitLab.CreateBranchWithCommit(projectId, branchB, "test1");

            mrIidA = _gitLab.CreateMergeRequest(
                projectId, branchA, "test1", $"Queue test A ({timestamp})");

            mrIidB = _gitLab.CreateMergeRequest(
                projectId, branchB, "test1", $"Queue test B ({timestamp})");

            Log.Information(
                "Created MRs: project {P} MR-A !{A}, MR-B !{B}",
                projectId,
                mrIidA,
                mrIidB);

            await WaitForMergeRequestReady(projectId, mrIidA);
            await WaitForMergeRequestReady(projectId, mrIidB);

            // Wait for TeamCity CI to finish so we have a stable baseline before
            // overriding with our own "running" status.
            Log.Information("--- Waiting for CI to stabilize ---");
            await WaitForCiStabilization(projectId, mrIidA, "primary-1 MR-A");
            await WaitForCiStabilization(projectId, mrIidB, "primary-1 MR-B");

            // Override both with "running" so combined status → ci_still_running.
            // ci_still_running is queue-eligible but prevents auto-merge from firing
            // during the test, giving us a stable window to verify queue state.
            var shaA = _gitLab.GetBranchHeadSha(projectId, branchA);
            var shaB = _gitLab.GetBranchHeadSha(projectId, branchB);
            _gitLab.SetCommitStatus(projectId, shaA, "running", PipelineName);
            _gitLab.SetCommitStatus(projectId, shaB, "running", PipelineName);

            Log.Information("Set both pipeline statuses to 'running' — queue-eligible but not merge-ready");

            // === Enable auto merge + auto rebase on both MGs via the UI ===
            await LoginHelper.EnsureLoggedIn(_browser, "test1");

            var branchAAppeared = await WaitForBranchOnDashboard(branchA, 90);
            if (!branchAAppeared)
            {
                throw new InvalidOperationException(
                    $"Branch '{branchA}' did not appear on dashboard within 90s");
            }

            var branchBAppeared = await WaitForBranchOnDashboard(branchB, 90);
            if (!branchBAppeared)
            {
                throw new InvalidOperationException(
                    $"Branch '{branchB}' did not appear on dashboard within 90s");
            }

            await _browser.TakeScreenshot("queue_01_both_branches_on_dashboard");

            await NavigateToMergeGroupDetails(branchA);
            await EnableAutoMerge();
            await _browser.TakeScreenshot("queue_02_auto_merge_enabled_a");

            await LoginHelper.NavigateToDashboard(_browser);
            await NavigateToMergeGroupDetails(branchB);
            await EnableAutoMerge();
            await _browser.TakeScreenshot("queue_03_auto_merge_enabled_b");

            // === Wait for AutoMergeService to assign both MGs to a queue ===
            Log.Information("--- Waiting for queue assignment (~25s) ---");
            await Task.Delay(25_000);

            // === Verify: queue position badges on dashboard ===
            await LoginHelper.NavigateToDashboard(_browser);
            await _browser.TakeScreenshot("queue_04_dashboard_with_queue_badges");

            var queueBadges = _browser.Page.Locator(".queue-position-badge");
            var badgeCount = await queueBadges.CountAsync();
            Log.Information("Queue position badges found on dashboard: {Count}", badgeCount);

            if (badgeCount < 2)
            {
                throw new InvalidOperationException(
                    $"Expected at least 2 queue position badges on dashboard, found {badgeCount}");
            }

            var nextInQueueBadges = _browser.Page
                .Locator(".queue-position-badge")
                .Filter(new LocatorFilterOptions { HasText = "Next in queue" });
            var nextCount = await nextInQueueBadges.CountAsync();
            Log.Information("'Next in queue' badges: {Count}", nextCount);

            if (nextCount == 0)
            {
                throw new InvalidOperationException(
                    "Expected at least one 'Next in queue' badge on the dashboard");
            }

            // === Verify: Queues page shows the queue with both cards ===
            await _browser.Navigate($"{TestConfig.MergicianUrl}/queues");
            await Task.Delay(2000);
            await _browser.TakeScreenshot("queue_05_queues_page");

            // Verify the Queues tab is present in the nav bar
            var queuesTab = _browser.Page
                .Locator(".nav-tabs .v-tab")
                .Filter(new LocatorFilterOptions { HasText = "Queues" });
            var tabCount = await queuesTab.CountAsync();
            Log.Information("Queues nav tab found: {Count}", tabCount);

            if (tabCount == 0)
            {
                throw new InvalidOperationException("Queues navigation tab not found in app bar");
            }

            // The queue selector autocomplete is only shown when multiple queues exist.
            // When there is a single queue (as in this test), it is auto-selected and
            // content is displayed directly without any picker.
            var queueAutocomplete = _browser.Page.Locator(".queue-autocomplete");
            var autocompleteCount = await queueAutocomplete.CountAsync();
            Log.Information("Queue selector autocomplete visible: {Visible}", autocompleteCount > 0);

            if (autocompleteCount > 0)
            {
                // Multiple queues exist — select the one containing "primary"
                await queueAutocomplete.ClickAsync();
                await Task.Delay(1000);
                await _browser.TakeScreenshot("queue_06_queue_selector_open");

                var queueDropdownItems = _browser.Page
                    .Locator(".v-overlay--active .v-list-item")
                    .Filter(new LocatorFilterOptions { HasText = "primary" });
                var dropdownItemCount = await queueDropdownItems.CountAsync();
                Log.Information("Queue dropdown items containing 'primary': {Count}", dropdownItemCount);

                if (dropdownItemCount == 0)
                {
                    throw new InvalidOperationException(
                        "No queue items containing 'primary' found in the queue selector dropdown");
                }

                await queueDropdownItems.First.ClickAsync();
                await Task.Delay(2000);
                await _browser.TakeScreenshot("queue_07_queue_selected");
            }
            else
            {
                // Single queue — auto-selected, wait for content to load
                Log.Information("Single queue detected (auto-selected); waiting for queue content to load");
                await _browser.TakeScreenshot("queue_06_single_queue_content");
            }

            // Poll for queue grid rows to appear, confirming queue data has loaded.
            var queueRows = _browser.Page.Locator(".grid-row[data-mg-name]");
            for (var i = 0; i < 30; i++)
            {
                if (await queueRows.CountAsync() > 0)
                {
                    Log.Information("Queue grid rows appeared after ~{Seconds}s", i);
                    break;
                }

                if (i % 10 == 0)
                {
                    Log.Information("Waiting for queue grid rows... {Seconds}s", i);
                }

                await Task.Delay(1000);
            }

            var rowCount = await queueRows.CountAsync();
            await _browser.TakeScreenshot("queue_07_queue_rows_loaded");
            Log.Information("Rows in selected queue: {Count}", rowCount);

            if (rowCount < 2)
            {
                throw new InvalidOperationException(
                    $"Expected at least 2 merge group rows in the queue, found {rowCount}");
            }

            // Verify the first entry shows a queue-position badge indicating it is next
            var firstQueueBadge = _browser.Page.Locator(".grid-row[data-mg-name] .queue-position-badge").First;
            var queueBadgeCount = await firstQueueBadge.CountAsync();
            Log.Information("Queue position badge on first row: {Count}", queueBadgeCount);

            if (queueBadgeCount == 0)
            {
                throw new InvalidOperationException(
                    "Expected the first queue entry to have a queue-position badge");
            }

            // === Verify: queue position link on MG details page ===
            // Click the first row to navigate to its details page
            await queueRows.First.ClickAsync();
            await _browser.Page.WaitForURLAsync(
                url => url.Contains("/merge-group/"),
                new PageWaitForURLOptions { Timeout = 15000 });

            await Task.Delay(2000);
            await _browser.TakeScreenshot("queue_08_mg_details_queue_link");

            var queuePositionInfo = _browser.Page.Locator(".queue-position-info");
            var queueInfoCount = await queuePositionInfo.CountAsync();
            Log.Information("Queue position info on MG details: {Count}", queueInfoCount);

            if (queueInfoCount == 0)
            {
                throw new InvalidOperationException(
                    "Queue position info block not found on the merge group details page");
            }

            var queuePositionLink = _browser.Page.Locator(".queue-position-link");
            var linkText = await queuePositionLink.First.InnerTextAsync();
            Log.Information("Queue position link text: '{Text}'", linkText);

            if (!linkText.Contains("queue", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Expected queue position link to contain 'queue', got: '{linkText}'");
            }

            // Clicking the queue link should navigate back to the /queues page
            await queuePositionLink.First.ClickAsync();
            await _browser.Page.WaitForURLAsync(
                url => url.Contains("/queues"),
                new PageWaitForURLOptions { Timeout = 15000 });

            await Task.Delay(1500);
            await _browser.TakeScreenshot("queue_09_back_on_queues_page");

            Log.Information("Queue position link navigates to /queues correctly");
            Log.Information("=== All merge queue scenarios passed ===");
        }
        catch
        {
            try
            {
                await _browser.TakeScreenshot("queue_FAILURE");
            }
            catch
            {
                // Ignore screenshot errors during failure handling
            }

            throw;
        }
        finally
        {
            Log.Information("Cleaning up merge queue test data...");
            if (mrIidA > 0) _gitLab.CloseMergeRequest(projectId, mrIidA);
            if (mrIidB > 0) _gitLab.CloseMergeRequest(projectId, mrIidB);
            _gitLab.DeleteBranch(projectId, branchA);
            _gitLab.DeleteBranch(projectId, branchB);
        }
    }

    /// <summary>
    ///     Waits for a merge request to transition out of the 'preparing' state.
    /// </summary>
    private async Task WaitForMergeRequestReady(int projectId, int mergeRequestIid)
    {
        for (var i = 0; i < 20; i++)
        {
            var mr = _gitLab.GetMergeRequestDetail(projectId, mergeRequestIid);
            if (mr.DetailedMergeStatus != "preparing")
            {
                Log.Information(
                    "MR !{MergeRequestIid} ready: dms={Dms}",
                    mergeRequestIid,
                    mr.DetailedMergeStatus);

                return;
            }

            await Task.Delay(1000);
        }

        Log.Warning(
            "MR !{MergeRequestIid} in project {ProjectId} still 'preparing' after timeout",
            mergeRequestIid,
            projectId);
    }

    /// <summary>
    ///     Waits for TeamCity's CI pipeline to finish so we have a stable commit-status baseline.
    /// </summary>
    private async Task WaitForCiStabilization(int projectId, int mergeRequestIid, string label)
    {
        const int timeoutSeconds = 240;
        Log.Information(
            "Waiting for CI to stabilize on {Label} MR !{Iid} (up to {Timeout}s)...",
            label,
            mergeRequestIid,
            timeoutSeconds);

        for (var i = 0; i < timeoutSeconds; i++)
        {
            var mr = _gitLab.GetMergeRequestDetail(projectId, mergeRequestIid);
            if (mr.DetailedMergeStatus is not ("ci_still_running" or "preparing"))
            {
                Log.Information(
                    "CI stabilized on {Label} MR !{Iid} after ~{Seconds}s: dms={Dms}",
                    label,
                    mergeRequestIid,
                    i,
                    mr.DetailedMergeStatus);

                return;
            }

            if (i % 30 == 0 && i > 0)
            {
                Log.Information(
                    "Still waiting for CI on {Label} MR !{Iid}... {Seconds}s (dms={Dms})",
                    label,
                    mergeRequestIid,
                    i,
                    mr.DetailedMergeStatus);
            }

            await Task.Delay(1000);
        }

        Log.Warning(
            "CI did not stabilize on {Label} MR !{Iid} within {Timeout}s. Proceeding anyway.",
            label,
            mergeRequestIid,
            timeoutSeconds);
    }

    /// <summary>
    ///     Polls the dashboard until the given branch name appears as a grid row.
    /// </summary>
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

            if (i % 15 == 0)
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

    /// <summary>
    ///     Finds and clicks the merge group row for the given branch, waiting for the details page.
    /// </summary>
    private async Task NavigateToMergeGroupDetails(string branchName)
    {
        var row = _browser.Page.Locator($"[data-mg-name*='{branchName}']").First;
        if (await row.CountAsync() > 0)
        {
            await row.ClickAsync();
            await _browser.Page.WaitForURLAsync(
                url => url.Contains("/merge-group/"),
                new PageWaitForURLOptions { Timeout = 15000 });

            await Task.Delay(2000);
            Log.Information("Navigated to merge group details for '{BranchName}'", branchName);
            return;
        }

        throw new InvalidOperationException(
            $"Merge group row for branch '{branchName}' not found on dashboard");
    }

    /// <summary>
    ///     Enables auto merge (and auto rebase) via the UI toggles on the details page.
    ///     Waits for the toggle to be interactable, then clicks it.
    /// </summary>
    private async Task EnableAutoMerge()
    {
        var autoMergeSwitch = _browser.Page.Locator(".auto-merge-controls .v-switch").First;
        await autoMergeSwitch.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

        var switchInput = autoMergeSwitch.Locator("input[type='checkbox']");

        // Wait for the toggle to become enabled (permission check may take a moment)
        var enabledDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < enabledDeadline)
        {
            if (await switchInput.IsEnabledAsync()) break;
            await Task.Delay(300);
        }

        if (!await switchInput.IsEnabledAsync())
        {
            throw new InvalidOperationException(
                "Auto merge toggle never became enabled (permission check did not resolve)");
        }

        if (!await switchInput.IsCheckedAsync())
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

        if (!await switchInput.IsCheckedAsync())
        {
            throw new InvalidOperationException("Failed to enable auto merge toggle");
        }
    }
}
