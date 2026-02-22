using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests that the dashboard UI displays the expected branch activity data
///     after SSE streaming completes, using the card-based layout.
///     Uses Playwright to interact with the actual frontend, just as a real user would.
///     Expected data per user (created by ProjectSetupService.SetupTestBranchData):
///     test1: feature/alpha (primary-1 with MR+approval, secondary-1 with MR),
///     feature/beta (primary-2 with MR, no approval)
///     test2: feature/gamma (primary-1 with MR, secondary-1 with MR+approval, secondary-2 with MR)
///     test3: feature/delta (secondary-3, no MR)
///
///     The UI should display the MR title next to the corresponding project entry
///     within the branch card when an MR exists.
/// </summary>
public class DashboardTest : IDisposable
{
    private readonly BrowserService _browser = new();

    /// <summary>
    ///     Cached card data from the dashboard, populated by WaitForDashboard.
    ///     Each entry is a parsed card with branch name, group status,
    ///     and per-repo items.
    /// </summary>
    private List<ParsedCard> _parsedCards = [];

    public void Dispose()
    {
        _browser.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task Run()
    {
        await _browser.Initialize(
            Path.Combine(TestConfig.ScreenshotDir, "activity"));

        // Test with test1 — should see feature/alpha and feature/beta
        await TestUserDashboard(
            "test1",
            () =>
            {
                // On free-tier GitLab, approvalsRequired is always 0, so
                // any branch with an MR is "Ready" (0 >= 0 = all approvals met)
                AssertCardItem("feature/alpha", "primary-1", "1/0", "No approval needed", "green", "MR exists", "blue", "Alpha changes in primary-1", "Test Group / primary-1");
                AssertCardItem("feature/alpha", "secondary-1", "0/0", "No approval needed", "green", "MR exists", "blue", "Alpha changes in secondary-1", "Test Group / secondary-1");
                AssertCardItem("feature/beta", "primary-2", "0/0", "No approval needed", "green", "MR exists", "blue", "Beta changes in primary-2", "Test Group / primary-2");
                AssertCardGroupStatus("feature/alpha", "Ready");
                AssertCardGroupStatus("feature/beta", "Ready");
                Log.Information("test1 dashboard data verified");
            });

        // Test with test2 — should see feature/gamma
        await TestUserDashboard(
            "test2",
            () =>
            {
                AssertCardItem("feature/gamma", "primary-1", "0/0", "No approval needed", "green", "MR exists", "blue", "Gamma changes in primary-1", "Test Group / primary-1");
                AssertCardItem("feature/gamma", "secondary-1", "1/0", "No approval needed", "green", "MR exists", "blue", "Gamma changes in secondary-1", "Test Group / secondary-1");
                AssertCardItem("feature/gamma", "secondary-2", "0/0", "No approval needed", "green", "MR exists", "blue", "Gamma changes in secondary-2", "Test Group / secondary-2");
                AssertCardGroupStatus("feature/gamma", "Ready");
                Log.Information("test2 dashboard data verified");
            });

        // Test with test3 — should see feature/delta (no MR)
        await TestUserDashboard(
            "test3",
            () =>
            {
                AssertCardItem("feature/delta", "secondary-3", "", null, null, "MR not created", "grey", null, "Test Group / secondary-3");
                AssertCardGroupStatus("feature/delta", "Waiting");
                Log.Information("test3 dashboard data verified");
            });

        // Test responsive layout at mobile viewport
        await TestResponsiveLayout();

        Log.Information("Dashboard test passed for all users");
    }

    private async Task TestUserDashboard(string username, Action verify)
    {
        Log.Information("Testing dashboard for user '{Username}'...", username);

        // Clear all cookies/state from previous logins to ensure clean session
        await _browser.Page.Context.ClearCookiesAsync();
        await Task.Delay(500);

        // Login as this user via Mergician OAuth flow
        await LoginToMergician(username);

        // Navigate to the dashboard and wait for SSE streaming to complete
        await WaitForDashboard(username);

        // Verify expectations against the rendered UI
        verify();
    }

    private async Task LoginToMergician(string username)
    {
        await _browser.Navigate($"{TestConfig.MergicianUrl}/api/auth/login");
        await Task.Delay(2000);
        await _browser.TakeScreenshot($"dashboard_{username}_01_login_redirect");

        var currentUrl = _browser.Page.Url;
        Log.Information("URL after login redirect: {Url}", currentUrl);

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

            await _browser.TakeScreenshot($"dashboard_{username}_02_after_sign_in");

            currentUrl = _browser.Page.Url;
            Log.Information("URL after sign in: {Url}", currentUrl);
        }

        // Handle OAuth authorization if needed
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

            await _browser.TakeScreenshot($"dashboard_{username}_03_after_authorize");
        }

        Log.Information("Logged into Mergician as {Username}", username);
    }

    /// <summary>
    ///     Navigates to the Mergician home page and waits for the SSE activity stream
    ///     to finish (the streaming indicator disappears and the cards are rendered).
    ///     Parses the rendered cards into <see cref="_parsedCards" /> for assertion.
    /// </summary>
    private async Task WaitForDashboard(string username)
    {
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
        await _browser.TakeScreenshot($"dashboard_{username}_04_initial_load");

        Log.Information("Waiting for SSE activity stream to complete...");
        var streamComplete = await WaitForStreamCompletion(120);
        if (!streamComplete)
        {
            await _browser.TakeScreenshot($"dashboard_{username}_05_stream_timeout");
            throw new InvalidOperationException(
                "Dashboard SSE stream did not complete within timeout");
        }

        await _browser.TakeScreenshot($"dashboard_{username}_05_stream_complete");

        // Parse the rendered dashboard cards
        _parsedCards = await ParseDashboardCards();

        Log.Information("Dashboard rendered {Count} cards for '{Username}':", _parsedCards.Count, username);
        foreach (var card in _parsedCards)
        {
            Log.Information("  Card: {Branch} — Status={Status}", card.BranchName, card.GroupStatus);
            foreach (var item in card.Items)
            {
                Log.Information("    {Repo} — Approvals={Approvals} Tooltip={Tooltip}",
                    item.Repo, item.Approvals, item.Tooltip);
            }
        }
    }

    /// <summary>
    ///     Waits until there are no more loading spinners in the dashboard cards,
    ///     meaning all data has been resolved via SSE.
    /// </summary>
    private async Task<bool> WaitForStreamCompletion(int timeoutSeconds)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            // Check if any cards exist
            var cardCount = await _browser.Page.Locator(".merge-group-card").CountAsync();
            if (cardCount == 0)
            {
                if (s % 10 == 0)
                    Log.Information("Waiting for dashboard cards to appear... {Seconds}s", s);

                await Task.Delay(1000);
                continue;
            }

            // Check for loading spinners in cards
            var spinnerCount =
                await _browser.Page.Locator(".merge-group-card .v-progress-circular").CountAsync();

            // Also check for the streaming indicator at the top
            var streamingIndicator =
                await _browser.Page.Locator(".streaming-indicator").CountAsync();

            if (spinnerCount == 0 && streamingIndicator == 0)
            {
                Log.Information("Dashboard stream completed after ~{Seconds}s (no spinners remaining)", s);
                return true;
            }

            if (s % 10 == 0)
            {
                Log.Information(
                    "Waiting for stream to resolve... {SpinnerCount} spinners, streaming={Streaming}, {Seconds}s elapsed",
                    spinnerCount, streamingIndicator > 0, s);
            }

            await Task.Delay(1000);
        }

        return false;
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

            var branchName = (await card.Locator(".branch-name").InnerTextAsync()).Trim();
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
                var projectTooltip = (await item.Locator(".item-project").GetAttributeAsync("title"))?.Trim() ?? "";

                // MR title (if any)
                var mrTitle = "";
                var mrTitleEl = item.Locator(".item-mr-title");
                if (await mrTitleEl.CountAsync() > 0)
                {
                    mrTitle = (await mrTitleEl.InnerTextAsync()).Trim();
                }

                // MR icon info
                var mrEl = item.Locator(".item-mr-icon");
                var mrTooltip = "";
                var mrColor = "";
                if (await mrEl.CountAsync() > 0)
                {
                    mrTooltip = (await mrEl.GetAttributeAsync("title"))?.Trim() ?? "";
                    var mrIcon = mrEl.Locator(".v-icon");
                    if (await mrIcon.CountAsync() > 0)
                    {
                        mrColor = (await mrIcon.GetAttributeAsync("data-mr-color")) ?? "";
                    }
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
                        iconColor = (await iconEl.GetAttributeAsync("data-approval-color")) ?? "";
                    }
                }

                items.Add(new ParsedCardItem(repo, projectTooltip, approvals, tooltip, iconColor, mrTooltip, mrColor, mrTitle));
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
        string? expectedMrTooltip = null,
        string? expectedMrIconColor = null,
        string? expectedMrTitle = null,
        string? expectedProjectTooltip = null)
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

        if (expectedMrTooltip != null)
        {
            if (item.MrTooltip != expectedMrTooltip)
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' repo '{repoContains}': expected MR tooltip '{expectedMrTooltip}', got '{item.MrTooltip}'");
            }
        }

        if (expectedMrIconColor != null)
        {
            if (item.MrIconColor != expectedMrIconColor)
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' repo '{repoContains}': expected MR icon color '{expectedMrIconColor}', got '{item.MrIconColor}'");
            }
        }

        if (expectedMrTitle != null)
        {
            if (!item.MrTitle.Contains(expectedMrTitle, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' repo '{repoContains}': expected MR title '{expectedMrTitle}', got '{item.MrTitle}'");
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

        Log.Information(
            "  Verified: {Branch} / {Repo} — ProjectTooltip={ProjectTooltip} Approvals={Approvals} Tooltip={Tooltip} MRTitle={MrTitle}",
            branchName, repoContains, item.ProjectTooltip, item.Approvals, item.Tooltip, item.MrTitle);
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
    /// </summary>
    private async Task TestResponsiveLayout()
    {
        Log.Information("Testing responsive card layout...");

        // Login as test1
        await _browser.Page.Context.ClearCookiesAsync();
        await Task.Delay(500);
        await LoginToMergician("test1");

        // Set a mobile-sized viewport
        await _browser.Page.SetViewportSizeAsync(375, 812);
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
        await WaitForStreamCompletion(120);
        await _browser.TakeScreenshot("dashboard_responsive_mobile");

        var cardCount = await _browser.Page.Locator(".merge-group-card").CountAsync();
        if (cardCount == 0)
            throw new InvalidOperationException("No cards visible at mobile viewport");

        // Verify cards are visible and branch names are not clipped to zero width
        var firstCard = _browser.Page.Locator(".merge-group-card").First;
        var box = await firstCard.BoundingBoxAsync();
        if (box == null || box.Width < 200)
            throw new InvalidOperationException($"Card unexpectedly narrow at mobile viewport: {box?.Width}px");

        Log.Information("Mobile viewport: {Count} cards visible, first card width={Width}px", cardCount, box.Width);

        // Set a tablet-sized viewport
        await _browser.Page.SetViewportSizeAsync(768, 1024);
        await Task.Delay(1000);
        await _browser.TakeScreenshot("dashboard_responsive_tablet");

        cardCount = await _browser.Page.Locator(".merge-group-card").CountAsync();
        if (cardCount == 0)
            throw new InvalidOperationException("No cards visible at tablet viewport");

        Log.Information("Tablet viewport: {Count} cards visible", cardCount);

        // Reset viewport to default desktop size
        await _browser.Page.SetViewportSizeAsync(1280, 720);
        await Task.Delay(500);

        Log.Information("Responsive layout test passed");
    }

    private record ParsedCardItem(
        string Repo,
        string ProjectTooltip,
        string Approvals,
        string Tooltip,
        string IconColor,
        string MrTooltip,
        string MrIconColor,
        string MrTitle);

    private record ParsedCard(string BranchName, string GroupStatus, List<ParsedCardItem> Items);
}