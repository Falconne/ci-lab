using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests that the dashboard UI displays the expected branch activity data as cards
///     after SSE streaming completes. Uses Playwright to interact with the actual
///     frontend, just as a real user would.
///     Expected data per user (created by ProjectSetupService.SetupTestBranchData):
///     test1: feature/alpha (primary-1 with MR+approval, secondary-1 with MR),
///            feature/beta (primary-2 with MR, no approval)
///     test2: feature/gamma (primary-1 with MR, secondary-1 with MR+approval, secondary-2 with MR)
///     test3: feature/delta (secondary-3, no MR)
/// </summary>
public class DashboardTest : IDisposable
{
    private readonly BrowserService _browser = new();

    /// <summary>
    ///     Parsed cards from the dashboard, populated by WaitForDashboard.
    /// </summary>
    private List<CardData> _parsedCards = [];

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
                AssertBranchRow("feature/alpha", "primary-1", true, true);
                AssertBranchRow("feature/alpha", "secondary-1", true, false);
                AssertBranchRow("feature/beta", "primary-2", true, false);
                Log.Information("test1 dashboard data verified");
            });

        // Test with test2 — should see feature/gamma
        await TestUserDashboard(
            "test2",
            () =>
            {
                AssertBranchRow("feature/gamma", "primary-1", true, false);
                AssertBranchRow("feature/gamma", "secondary-1", true, true);
                AssertBranchRow("feature/gamma", "secondary-2", true, false);
                Log.Information("test2 dashboard data verified");
            });

        // Test with test3 — should see feature/delta (no MR)
        await TestUserDashboard(
            "test3",
            () =>
            {
                AssertBranchRow("feature/delta", "secondary-3", false, false);
                Log.Information("test3 dashboard data verified");
            });

        // Verify cards remain readable across common viewport widths
        await TestResponsiveLayout();

        Log.Information("Dashboard test passed for all users");
    }

    private async Task TestUserDashboard(string username, Action verify)
    {
        Log.Information($"Testing dashboard for user '{username}'...");

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
        Log.Information($"URL after login redirect: {currentUrl}");

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
            Log.Information($"URL after sign in: {currentUrl}");
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
                Log.Warning($"OAuth authorize didn't redirect. URL: {_browser.Page.Url}");
            }

            await _browser.TakeScreenshot($"dashboard_{username}_03_after_authorize");
        }

        Log.Information($"Logged into Mergician as {username}");
    }

    /// <summary>
    ///     Navigates to the Mergician home page, waits for the SSE activity stream to
    ///     finish (all repo-level loading spinners disappear), then parses the rendered
    ///     cards into _parsedCards for assertion.
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

        _parsedCards = await ParseDashboardCards();

        Log.Information($"Dashboard rendered {_parsedCards.Count} card(s) for '{username}':");
        foreach (var card in _parsedCards)
        {
            Log.Information($"  Card: {card.BranchName} [{card.GroupStatus}]");
            foreach (var repo in card.Repos)
                Log.Information($"    {repo.RepoName} [{repo.Status}] approvals={repo.Approvals}");
        }
    }

    /// <summary>
    ///     Waits until at least one merge-group card is rendered and all per-repo loading
    ///     spinners have resolved, indicating the SSE stream has completed.
    /// </summary>
    private async Task<bool> WaitForStreamCompletion(int timeoutSeconds)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            var cardCount = await _browser.Page.Locator("[data-testid='merge-group-card']").CountAsync();
            if (cardCount == 0)
            {
                if (s % 10 == 0)
                    Log.Information($"Waiting for dashboard cards to appear... {s}s");

                await Task.Delay(1000);
                continue;
            }

            // Check for loading spinners in repo rows (hasMergeRequest === null)
            var spinnerCount =
                await _browser.Page.Locator(".card-repos .v-progress-circular").CountAsync();

            if (spinnerCount == 0)
            {
                Log.Information($"Dashboard stream completed after ~{s}s");
                return true;
            }

            if (s % 10 == 0)
                Log.Information($"Waiting for data to resolve... {spinnerCount} spinner(s), {s}s elapsed");

            await Task.Delay(1000);
        }

        return false;
    }

    /// <summary>
    ///     Parses the rendered dashboard cards into structured data for assertion.
    /// </summary>
    private async Task<List<CardData>> ParseDashboardCards()
    {
        var result = new List<CardData>();
        var cards = _browser.Page.Locator("[data-testid='merge-group-card']");
        var cardCount = await cards.CountAsync();

        for (var i = 0; i < cardCount; i++)
        {
            var card = cards.Nth(i);
            var branchName = (await card.Locator(".card-branch-name").InnerTextAsync()).Trim();

            var groupStatusChip = card.Locator(".card-header .status-chip").First;
            var groupStatusClass = await groupStatusChip.GetAttributeAsync("class") ?? "";
            var groupStatus = ExtractStatus(groupStatusClass);

            var repos = new List<RepoRowData>();
            var repoRows = card.Locator(".card-repos .repo-row");
            var rowCount = await repoRows.CountAsync();

            for (var r = 0; r < rowCount; r++)
            {
                var row = repoRows.Nth(r);
                var repoName = (await row.Locator(".repo-name").InnerTextAsync()).Trim();

                var statusChip = row.Locator(".status-chip");
                var repoStatus = "loading";
                if (await statusChip.CountAsync() > 0)
                {
                    var chipClass = await statusChip.First.GetAttributeAsync("class") ?? "";
                    repoStatus = ExtractStatus(chipClass);
                }

                var approvalsEl = row.Locator(".approvals-text");
                var approvals = await approvalsEl.CountAsync() > 0
                    ? (await approvalsEl.InnerTextAsync()).Trim()
                    : "";

                repos.Add(new RepoRowData(repoName, repoStatus, approvals));
            }

            result.Add(new CardData(branchName, groupStatus, repos));
        }

        return result;
    }

    private static string ExtractStatus(string cssClass)
    {
        if (cssClass.Contains("status-chip--ready"))   return "ready";
        if (cssClass.Contains("status-chip--open"))    return "open";
        if (cssClass.Contains("status-chip--waiting")) return "waiting";
        return "unknown";
    }

    /// <summary>
    ///     Asserts that a specific branch/repo combination exists in the parsed cards
    ///     with the expected MR and approval status.
    ///     hasMr=true means the repo's status chip must not be 'waiting' (i.e. an MR exists).
    /// </summary>
    private void AssertBranchRow(
        string branchName,
        string repoContains,
        bool hasMr,
        bool expectApproval)
    {
        var card = _parsedCards.FirstOrDefault(c =>
            c.BranchName.Contains(branchName, StringComparison.OrdinalIgnoreCase));

        if (card == null)
        {
            var available = string.Join(", ", _parsedCards.Select(c => c.BranchName));
            throw new InvalidOperationException(
                $"Expected card for branch '{branchName}' not found. Available: [{available}]");
        }

        var repo = card.Repos.FirstOrDefault(r =>
            r.RepoName.Contains(repoContains, StringComparison.OrdinalIgnoreCase));

        if (repo == null)
        {
            var available = string.Join(", ", card.Repos.Select(r => r.RepoName));
            throw new InvalidOperationException(
                $"Expected repo containing '{repoContains}' not found in card '{branchName}'. Available: [{available}]");
        }

        // hasMr=true means status should be 'ready' or 'open' (an MR exists)
        var actuallyHasMr = repo.Status is "ready" or "open";
        if (actuallyHasMr != hasMr)
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' repo '{repoContains}': expected hasMr={hasMr}, "
                + $"got status='{repo.Status}' (hasMr={actuallyHasMr})");
        }

        if (expectApproval)
        {
            if (string.IsNullOrWhiteSpace(repo.Approvals))
                throw new InvalidOperationException(
                    $"Branch '{branchName}' repo '{repoContains}': expected approvals text, got empty");

            var parts = repo.Approvals.Split('/');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var given) || given <= 0)
                throw new InvalidOperationException(
                    $"Branch '{branchName}' repo '{repoContains}': "
                    + $"expected approvals given > 0, got '{repo.Approvals}'");
        }

        Log.Information(
            $"  Verified: {branchName} in {repoContains} — status={repo.Status}, approvals={repo.Approvals}");
    }

    /// <summary>
    ///     Verifies the dashboard card layout is readable across common viewport widths.
    ///     The current user must already be logged in and on the dashboard page.
    /// </summary>
    private async Task TestResponsiveLayout()
    {
        Log.Information("Testing responsive card layout at multiple viewport widths...");

        int[] widths = [375, 768, 1280];

        foreach (var width in widths)
        {
            await _browser.Page.SetViewportSizeAsync(width, 800);
            await Task.Delay(300);

            var cardCount = await _browser.Page.Locator("[data-testid='merge-group-card']").CountAsync();
            if (cardCount == 0)
                throw new InvalidOperationException(
                    $"No dashboard cards visible at viewport width {width}px");

            var firstHeader = _browser.Page.Locator("[data-testid='merge-group-card'] .card-header").First;
            var box = await firstHeader.BoundingBoxAsync();
            if (box == null || box.Width <= 0 || box.Height <= 0)
                throw new InvalidOperationException(
                    $"Card header not visible at viewport width {width}px");

            await _browser.TakeScreenshot($"responsive_{width}px");
            Log.Information($"  {width}px: {cardCount} card(s), header {box.Width:F0}x{box.Height:F0}");
        }

        // Restore a standard desktop viewport
        await _browser.Page.SetViewportSizeAsync(1280, 800);
        Log.Information("Responsive layout verified across all tested widths");
    }
}

// ─── Data records ─────────────────────────────────────────────────────────────

internal record RepoRowData(string RepoName, string Status, string Approvals);
internal record CardData(string BranchName, string GroupStatus, List<RepoRowData> Repos);