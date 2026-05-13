using IntegrationTest.Services;
using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests that the dashboard UI displays the expected branch activity data
///     after polling-based loading completes, using the card-based layout.
///     Uses Playwright to interact with the actual frontend, just as a real user would.
///     Expected data per user (created by ProjectSetupService.SetupTestBranchData):
///     test1: feature/alpha (primary-1 with MR+approval, secondary-1 with MR),
///     feature/beta (primary-2 with MR, no approval),
///     feature/epsilon (secondary-4 with draft MR → Waiting)
///     test2: feature/gamma (primary-1 with MR, secondary-1 with MR+approval, secondary-2 with MR)
///     test3: feature/delta (secondary-3, no MR)
///     The UI should display the MR title next to the corresponding project entry
///     within the branch card when an MR exists.
///     When no MR exists, the UI shows 'No Merge Request' in the MR title area.
/// </summary>
public class DashboardTest
{
    private readonly BrowserService _browser;

    /// <summary>
    ///     Cached card data from the dashboard, populated by WaitForDashboard.
    ///     Each entry is a parsed card with branch name, group status,
    ///     and per-repo items.
    /// </summary>
    private List<ParsedCard> _parsedCards = [];

    public DashboardTest(BrowserService browser)
    {
        _browser = browser;
    }

    public async Task Run()
    {
        _browser.SetScreenshotDir(Path.Combine(TestConfig.ScreenshotDir, "dashboard"));

        // Test with test1 — should see feature/alpha, feature/beta, and feature/epsilon
        // Wait for builds to complete (TeamCity reports build status via external pipelines which
        // Mergician now tracks; branches show 'Waiting' until builds finish)
        await TestUserDashboard(
            "test1",
            () =>
            {
                // On free-tier GitLab, approvalsRequired is always 0, so
                // any branch with an MR is "Ready" (0 >= 0 = all approvals met)
                AssertCardItem(
                    "feature/alpha",
                    "primary-1",
                    "1/0",
                    "No approval needed",
                    "green",
                    "Alpha changes in primary-1",
                    "Test Group / primary-1");

                AssertCardItem(
                    "feature/alpha",
                    "secondary-1",
                    "0/0",
                    "No approval needed",
                    "green",
                    "Alpha changes in secondary-1",
                    "Test Group / secondary-1");

                AssertCardItem(
                    "feature/beta",
                    "primary-2",
                    "0/0",
                    "No approval needed",
                    "green",
                    "Beta changes in primary-2",
                    "Test Group / primary-2");

                // secondary-4 has feature/epsilon as a draft MR, so the group is Waiting
                AssertCardItem(
                    "feature/epsilon",
                    "secondary-4",
                    "0/0",
                    "No approval needed",
                    "green",
                    "Draft: Epsilon changes in secondary-4",
                    "Test Group / secondary-4");

                Log.Information("test1 dashboard data verified");
            });

        await TestMergeGroupDetailsNavigationAndLinks(
            "feature/alpha",
            "primary-1",
            "Alpha changes in primary-1");

        // Responsive layout test runs here while still logged in as test1 (richest card set)
        await TestResponsiveLayout();

        // Test with test2 — should see feature/gamma
        await TestUserDashboard(
            "test2",
            () =>
            {
                AssertCardItem(
                    "feature/gamma",
                    "primary-1",
                    "0/0",
                    "No approval needed",
                    "green",
                    "Gamma changes in primary-1",
                    "Test Group / primary-1");

                AssertCardItem(
                    "feature/gamma",
                    "secondary-1",
                    "1/0",
                    "No approval needed",
                    "green",
                    "Gamma changes in secondary-1",
                    "Test Group / secondary-1");

                AssertCardItem(
                    "feature/gamma",
                    "secondary-2",
                    "0/0",
                    "No approval needed",
                    "green",
                    "Gamma changes in secondary-2",
                    "Test Group / secondary-2");

                Log.Information("test2 dashboard data verified");
            });

        // Test with test3 — should see feature/delta (no MR)
        await TestUserDashboard(
            "test3",
            () =>
            {
                AssertCardItem(
                    "feature/delta",
                    "secondary-3",
                    "",
                    null,
                    null,
                    null,
                    "Test Group / secondary-3",
                    "No Merge Request");

                Log.Information("test3 dashboard data verified");
            });

        Log.Information("Dashboard test passed for all users");
    }

    private async Task TestUserDashboard(
        string username,
        Action verify,
        IReadOnlyDictionary<string, string>? expectedGroupStatuses = null)
    {
        Log.Information("Testing dashboard for user '{Username}'...", username);

        await LoginHelper.LoginAndWaitForDashboard(_browser, username);

        await WaitForDashboard(username, expectedGroupStatuses);

        // Verify expectations against the rendered UI
        verify();
    }

    /// <summary>
    ///     Waits for the dashboard data to load via polling (cards appear and MR data
    ///     is resolved through the refresh cycle). If <paramref name="expectedGroupStatuses"/>
    ///     is provided, additionally waits until every listed branch shows the expected
    ///     status badge. Parses the rendered cards into <see cref="_parsedCards" /> for assertion.
    /// </summary>
    private async Task WaitForDashboard(
        string username,
        IReadOnlyDictionary<string, string>? expectedGroupStatuses = null)
    {
        await _browser.TakeScreenshot($"dashboard_{username}_01_initial_load");

        Log.Information("Waiting for dashboard data to load...");
        var loaded = await WaitForDashboardReady(120);
        if (!loaded)
        {
            await _browser.TakeScreenshot($"dashboard_{username}_02_load_timeout");
            throw new InvalidOperationException(
                "Dashboard did not fully load within timeout");
        }

        // If specific group statuses are expected, wait for them (e.g. builds finishing before Ready)
        if (expectedGroupStatuses != null)
        {
            Log.Information(
                "Waiting for expected group statuses: {Statuses}",
                string.Join(", ", expectedGroupStatuses.Select(kv => $"{kv.Key}={kv.Value}")));

            var statusesReached = await DashboardWaitHelper.WaitForGroupStatuses(
                _browser.Page,
                expectedGroupStatuses,
                timeoutSeconds: 360);

            if (!statusesReached)
            {
                await _browser.TakeScreenshot($"dashboard_{username}_02_status_timeout");
                throw new InvalidOperationException(
                    $"Expected group statuses were not reached within timeout for user '{username}'");
            }
        }

        await _browser.TakeScreenshot($"dashboard_{username}_02_load_complete");

        // Parse the rendered dashboard cards
        _parsedCards = await ParseDashboardCards();

        Log.Information("Dashboard rendered {Count} cards for '{Username}':", _parsedCards.Count, username);
        foreach (var card in _parsedCards)
        {
            Log.Information("  Card: {Branch} — Status={Status}", card.BranchName, card.GroupStatus);
            foreach (var item in card.Items)
            {
                Log.Information(
                    "    {Repo} — Approvals={Approvals} Tooltip={Tooltip}",
                    item.Repo,
                    item.Approvals,
                    item.Tooltip);
            }
        }
    }

    /// <summary>
    ///     Waits until the dashboard cards appear and all card items have their MR data resolved.
    /// </summary>
    private async Task<bool> WaitForDashboardReady(int timeoutSeconds)
    {
        return await DashboardWaitHelper.WaitForDashboardReady(_browser.Page, timeoutSeconds);
    }

    /// <summary>
    ///     Parses the rendered dashboard cards into structured data for assertions.
    /// </summary>
    private async Task<List<ParsedCard>> ParseDashboardCards()
    {
        var cards = new List<ParsedCard>();
        var cardElements = _browser.Page.Locator(".merge-group-card");
        var cardCount = await cardElements.CountAsync();

        for (var i = 0; i < cardCount; i++)
        {
            var card = cardElements.Nth(i);

            var branchName = (await card.Locator(".branch-name, .branch-subtitle").First.InnerTextAsync()).Trim();
            var groupStatusBadge = card.Locator(".card-status-badge");
            var groupStatus = await groupStatusBadge.CountAsync() > 0
                ? (await groupStatusBadge.InnerTextAsync()).Trim()
                : "";

            var items = new List<ParsedCardItem>();
            var itemElements = card.Locator(".card-item");
            var itemCount = await itemElements.CountAsync();

            for (var j = 0; j < itemCount; j++)
            {
                var item = itemElements.Nth(j);
                var repo = (await item.Locator(".item-project").InnerTextAsync()).Trim();
                var projectTooltip = (await item.Locator(".item-project").GetAttributeAsync("title"))?.Trim()
                                     ?? "";

                // MR title (if any)
                var mergeRequestTitle = "";
                var mergeRequestTitleEl = item.Locator(".item-mr-title");
                if (await mergeRequestTitleEl.CountAsync() > 0)
                {
                    mergeRequestTitle = (await mergeRequestTitleEl.InnerTextAsync()).Trim();
                }

                var noMergeRequestText = "";
                var noMergeRequestEl = item.Locator(".item-no-mr");
                if (await noMergeRequestEl.CountAsync() > 0)
                {
                    noMergeRequestText = (await noMergeRequestEl.InnerTextAsync()).Trim();
                }

                var approvalEl = item.Locator(".item-approvals");
                var approvals = "";
                var tooltip = "";
                var iconColor = "";
                if (await approvalEl.CountAsync() > 0)
                {
                    approvals = (await approvalEl.InnerTextAsync()).Trim();
                    tooltip = (await approvalEl.GetAttributeAsync("title"))?.Trim() ?? "";
                    // try read color prop from the icon if present
                    var iconEl = approvalEl.Locator(".approval-icon");
                    if (await iconEl.CountAsync() > 0)
                    {
                        iconColor = await iconEl.GetAttributeAsync("data-approval-color") ?? "";
                    }
                }

                items.Add(
                    new ParsedCardItem(
                        repo,
                        projectTooltip,
                        approvals,
                        tooltip,
                        iconColor,
                        mergeRequestTitle,
                        noMergeRequestText));
            }

            cards.Add(new ParsedCard(branchName, groupStatus, items));
        }

        return cards;
    }

    /// <summary>
    ///     Asserts that a specific branch/repo combination exists in the parsed cards
    ///     with the expected approvals text.
    /// </summary>
    private void AssertCardItem(
        string branchName,
        string repoContains,
        string expectedApprovals,
        string? expectedTooltip = null,
        string? expectedIconColor = null,
        string? expectedMergeRequestTitle = null,
        string? expectedProjectTooltip = null,
        string? expectedNoMergeRequestText = null)
    {
        var card = _parsedCards.FirstOrDefault(c =>
            c.BranchName.Contains(branchName, StringComparison.OrdinalIgnoreCase));

        if (card == null)
        {
            var available = string.Join(", ", _parsedCards.Select(c => c.BranchName));
            throw new InvalidOperationException(
                $"Expected card for branch '{branchName}' not found. Available: [{available}]");
        }

        var item = card.Items.FirstOrDefault(i =>
            i.Repo.Contains(repoContains, StringComparison.OrdinalIgnoreCase));

        if (item == null)
        {
            var repos = string.Join(", ", card.Items.Select(i => i.Repo));
            throw new InvalidOperationException(
                $"Expected repo containing '{repoContains}' in branch '{branchName}' not found. Available: [{repos}]");
        }

        if (expectedApprovals != "" && item.Approvals != expectedApprovals)
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' repo '{repoContains}': expected approvals '{expectedApprovals}', got '{item.Approvals}'");
        }

        if (expectedTooltip != null)
        {
            if (item.Tooltip != expectedTooltip)
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' repo '{repoContains}': expected tooltip '{expectedTooltip}', got '{item.Tooltip}'");
            }
        }

        if (expectedIconColor != null)
        {
            if (item.IconColor != expectedIconColor)
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' repo '{repoContains}': expected icon color '{expectedIconColor}', got '{item.IconColor}'");
            }
        }

        if (expectedMergeRequestTitle != null)
        {
            if (!item.MergeRequestTitle.Contains(expectedMergeRequestTitle, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' repo '{repoContains}': expected MR title '{expectedMergeRequestTitle}', got '{item.MergeRequestTitle}'");
            }
        }

        if (expectedProjectTooltip != null)
        {
            if (!item.ProjectTooltip.Equals(expectedProjectTooltip, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' repo '{repoContains}': expected project tooltip '{expectedProjectTooltip}', got '{item.ProjectTooltip}'");
            }
        }

        if (expectedNoMergeRequestText != null)
        {
            if (!item.NoMergeRequestText.Contains(expectedNoMergeRequestText, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' repo '{repoContains}': expected no-MR text '{expectedNoMergeRequestText}', got '{item.NoMergeRequestText}'");
            }
        }

        Log.Information(
            "  Verified: {Branch} / {Repo} — ProjectTooltip={ProjectTooltip} Approvals={Approvals} Tooltip={Tooltip} MRTitle={MergeRequestTitle}",
            branchName,
            repoContains,
            item.ProjectTooltip,
            item.Approvals,
            item.Tooltip,
            item.MergeRequestTitle);
    }

    /// <summary>
    ///     Asserts the overall group status badge on a card.
    /// </summary>
    private void AssertCardGroupStatus(string branchName, string expectedStatus)
    {
        var card = _parsedCards.FirstOrDefault(c =>
            c.BranchName.Contains(branchName, StringComparison.OrdinalIgnoreCase));

        if (card == null)
        {
            throw new InvalidOperationException(
                $"Expected card for branch '{branchName}' not found for group status assertion");
        }

        if (!card.GroupStatus.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}': expected group status '{expectedStatus}', got '{card.GroupStatus}'");
        }

        Log.Information("  Verified group status: {Branch} = {Status}", branchName, card.GroupStatus);
    }

    /// <summary>
    ///     Tests that the dashboard card layout is responsive at mobile viewport width.
    ///     Verifies that cards render properly and content is visible at small screen sizes.
    ///     Called while already logged in as test1 (richest card set for better coverage).
    /// </summary>
    private async Task TestResponsiveLayout()
    {
        Log.Information("Testing responsive card layout...");

        // Set a mobile-sized viewport and reload to apply it
        await _browser.Page.SetViewportSizeAsync(375, 812);
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
        await WaitForDashboardReady(120);
        await _browser.TakeScreenshot("dashboard_responsive_mobile");

        var cardCount = await _browser.Page.Locator(".merge-group-card").CountAsync();
        if (cardCount == 0)
        {
            throw new InvalidOperationException("No cards visible at mobile viewport");
        }

        // Verify cards are visible and branch names are not clipped to zero width
        var firstCard = _browser.Page.Locator(".merge-group-card").First;
        var box = await firstCard.BoundingBoxAsync();
        if (box == null || box.Width < 200)
        {
            throw new InvalidOperationException(
                $"Card unexpectedly narrow at mobile viewport: {box?.Width}px");
        }

        Log.Information(
            "Mobile viewport: {Count} cards visible, first card width={Width}px",
            cardCount,
            box.Width);

        // Set a tablet-sized viewport
        await _browser.Page.SetViewportSizeAsync(768, 1024);
        await Task.Delay(1000);
        await _browser.TakeScreenshot("dashboard_responsive_tablet");

        cardCount = await _browser.Page.Locator(".merge-group-card").CountAsync();
        if (cardCount == 0)
        {
            throw new InvalidOperationException("No cards visible at tablet viewport");
        }

        Log.Information("Tablet viewport: {Count} cards visible", cardCount);

        // Reset viewport to default desktop size
        await _browser.Page.SetViewportSizeAsync(1280, 720);
        await Task.Delay(500);

        Log.Information("Responsive layout test passed");
    }

    private async Task TestMergeGroupDetailsNavigationAndLinks(
        string branchName,
        string repoContains,
        string expectedMergeRequestTitle)
    {
        Log.Information("Testing merge-group details navigation and links for '{BranchName}'", branchName);

        var card = _browser.Page.Locator(".merge-group-card")
            .Filter(new LocatorFilterOptions { HasTextString = branchName })
            .First;

        await card.ClickAsync();

        await _browser.Page.WaitForURLAsync(
            url => url.Contains("/merge-group/"),
            new PageWaitForURLOptions { Timeout = 20000 });

        await _browser.TakeScreenshot("dashboard_details_01_opened");

        var pageTitle = (await _browser.Page.Locator(".page-title").InnerTextAsync()).Trim();
        if (!pageTitle.Equals($"Merge Group: {branchName}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expected app bar title 'Merge Group: {branchName}', got '{pageTitle}'");
        }

        var backButton = _browser.Page.Locator(".v-btn:has-text('Back to Dashboard')");
        if (!await BrowserService.WaitForElement(backButton, timeoutMs: 10000))
        {
            throw new InvalidOperationException("Back to Dashboard button was not visible on details page");
        }

        var summaryCard = _browser.Page.Locator(".repo-card-list");
        if (!await BrowserService.WaitForElement(summaryCard, timeoutMs: 10000))
        {
            throw new InvalidOperationException("Project list was not visible on details page");
        }

        // Wait for the data to load - the details page polls for a full snapshot including MR data
        // populated by the background sync thread
        await WaitForDetailsPageReady(repoContains, 60);

        var repoCard = _browser.Page.Locator(".repo-card-list .branch-card")
            .Filter(new LocatorFilterOptions { HasTextString = repoContains })
            .First;

        if (!await BrowserService.WaitForElement(repoCard, timeoutMs: 10000))
        {
            throw new InvalidOperationException($"Could not find details card for repo '{repoContains}'");
        }

        // Branch link is `.branch-title-link` normally, or `.branch-subtitle-link` when MR title is in header.
        var repoLink = repoCard.Locator(".branch-title-link, .branch-subtitle-link").First;
        var repoHref = await repoLink.GetAttributeAsync("href") ?? "";
        if (string.IsNullOrWhiteSpace(repoHref))
        {
            throw new InvalidOperationException($"Repo link href was empty for repo '{repoContains}'");
        }

        // MR title is now shown as the primary link in the card header (`.mr-title-link`)
        // when the branch has an MR. The old `.detail-row` for "Merge Request" is hidden in that case.
        var mrTitleLink = repoCard.Locator(".mr-title-link").First;
        var hasMrTitleLink = await mrTitleLink.CountAsync() > 0;

        string mergeRequestText;
        if (hasMrTitleLink)
        {
            mergeRequestText = (await mrTitleLink.InnerTextAsync()).Trim();
        }
        else
        {
            // Fallback: no MR title in header — check legacy detail row (skeleton/no-MR state)
            var mergeRequestRow = repoCard.Locator(".detail-row")
                .Filter(new LocatorFilterOptions { HasTextString = "Merge Request" })
                .First;
            var legacyLink = mergeRequestRow.Locator(".detail-link").First;
            mergeRequestText = await legacyLink.CountAsync() > 0
                ? (await legacyLink.InnerTextAsync()).Trim()
                : "";
        }

        if (!mergeRequestText.Contains(expectedMergeRequestTitle, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expected MR title containing '{expectedMergeRequestTitle}' on details page, got '{mergeRequestText}'");
        }

        if (mergeRequestText.EndsWith("...", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Details page MR title should not be truncated, but got '{mergeRequestText}'");
        }

        var externalJobsSection = repoCard.Locator("text=Build Jobs:");
        if (!await BrowserService.WaitForElement(externalJobsSection, timeoutMs: 10000))
        {
            throw new InvalidOperationException("Build Jobs section was not visible on details page");
        }

        await _browser.TakeScreenshot("dashboard_details_01b_data_loaded");

        var homeLink = _browser.Page.Locator("[data-mergician-home-link]");
        await homeLink.ClickAsync();
        await _browser.Page.WaitForURLAsync(
            url => url.TrimEnd('/').Equals(TestConfig.MergicianUrl, StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { Timeout = 20000 });

        var loaded = await WaitForDashboardReady(120);
        if (!loaded)
        {
            throw new InvalidOperationException(
                "Dashboard did not load after navigating home from details page");
        }

        await _browser.TakeScreenshot("dashboard_details_02_back_home");

        var cardsAfterReturn = await _browser.Page.Locator(".merge-group-card").CountAsync();
        if (cardsAfterReturn == 0)
        {
            throw new InvalidOperationException(
                "No dashboard cards visible after returning home via app bar title");
        }

        Log.Information("Merge-group details navigation and links verified for '{BranchName}'", branchName);
    }

    /// <summary>
    ///     Waits until the details page has loaded branch cards and their MR data has been
    ///     populated by the background sync thread. A branch card is considered
    ///     resolved when it shows either an MR link, "No Merge Request", or approval info.
    /// </summary>
    private async Task WaitForDetailsPageReady(string repoContains, int timeoutSeconds)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            var cards = _browser.Page.Locator(".repo-card-list .branch-card");
            var cardCount = await cards.CountAsync();

            if (cardCount == 0)
            {
                if (s % 10 == 0)
                {
                    Log.Information("Waiting for details page branch cards... {Seconds}s", s);
                }

                await Task.Delay(1000);
                continue;
            }

            // Check if the repo we care about has resolved MR data
            var repoCard = cards.Filter(new LocatorFilterOptions { HasTextString = repoContains }).First;
            if (await repoCard.CountAsync() == 0)
            {
                await Task.Delay(1000);
                continue;
            }

            // MR title is now rendered as the primary header link when available.
            var hasMrTitleInHeader = await repoCard.Locator(".mr-title-link").CountAsync() > 0
                                     || await repoCard.Locator(".mr-title-text").CountAsync() > 0;

            bool isResolved;
            if (hasMrTitleInHeader)
            {
                // Header contains MR title — the detail row for "Merge Request" is intentionally hidden.
                isResolved = true;
            }
            else
            {
                var mergeRequestRow = repoCard.Locator(".detail-row")
                    .Filter(new LocatorFilterOptions { HasTextString = "Merge Request" })
                    .First;

                var hasMergeRequestLink = await mergeRequestRow.Locator(".detail-link").CountAsync() > 0;
                var hasCreateMrButton = await mergeRequestRow.Locator(".v-btn").CountAsync() > 0;
                var hasNoMergeRequestText = await mergeRequestRow.Locator(".text-medium-emphasis").CountAsync() > 0;
                var noMergeRequestText = hasNoMergeRequestText
                    ? (await mergeRequestRow.Locator(".text-medium-emphasis").InnerTextAsync()).Trim()
                    : "";

                isResolved = hasMergeRequestLink
                             || hasCreateMrButton
                             || noMergeRequestText.Contains("No Merge Request", StringComparison.OrdinalIgnoreCase);
            }

            // Also check that all cards have resolved (not showing skeleton/loading state)
            var allResolved = isResolved;
            if (allResolved)
            {
                for (var i = 0; i < cardCount; i++)
                {
                    var card = cards.Nth(i);

                    // A card is still loading if the MR title area shows a skeleton, or if the
                    // legacy "Merge Request" detail row (shown when there's no MR) contains "Resolving...".
                    var cardHasMrTitle = await card.Locator(".mr-title-link, .mr-title-text").CountAsync() > 0;
                    if (cardHasMrTitle)
                    {
                        // MR title present in header — resolved.
                        continue;
                    }

                    var cardMergeRequestRow = card.Locator(".detail-row")
                        .Filter(new LocatorFilterOptions { HasTextString = "Merge Request" })
                        .First;

                    if (await cardMergeRequestRow.CountAsync() > 0)
                    {
                        var text = (await cardMergeRequestRow.InnerTextAsync()).Trim();
                        if (text.Contains("Resolving...", StringComparison.OrdinalIgnoreCase))
                        {
                            allResolved = false;
                            break;
                        }
                    }
                }
            }

            if (allResolved)
            {
                Log.Information(
                    "Details page fully loaded after ~{Seconds}s ({Cards} branch cards)",
                    s,
                    cardCount);

                return;
            }

            if (s % 10 == 0)
            {
                Log.Information(
                    "Waiting for details page MR data to resolve... {Cards} cards visible, {Seconds}s elapsed",
                    cardCount,
                    s);
            }

            await Task.Delay(1000);
        }

        await _browser.TakeScreenshot("dashboard_details_load_timeout");
        throw new InvalidOperationException(
            "Details page data did not fully resolve within timeout");
    }

    private record ParsedCardItem(
        string Repo,
        string ProjectTooltip,
        string Approvals,
        string Tooltip,
        string IconColor,
        string MergeRequestTitle,
        string NoMergeRequestText);

    private record ParsedCard(string BranchName, string GroupStatus, List<ParsedCardItem> Items);
}