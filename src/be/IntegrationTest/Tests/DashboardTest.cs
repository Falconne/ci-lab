using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests that the dashboard UI displays the expected branch activity data
///     after SSE streaming completes. Uses Playwright to interact with the actual
///     frontend, just as a real user would.
/// </summary>
public class DashboardTest : IDisposable
{
    private readonly BrowserService _browser = new();

    private List<DashboardCardData> _parsedCards = [];

    private List<DashboardRowData> _parsedRows = [];

    public void Dispose()
    {
        _browser.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task Run()
    {
        await _browser.Initialize(
            Path.Combine(TestConfig.ScreenshotDir, "activity"));

        await TestUserDashboard(
            "test1",
            () =>
            {
                AssertBranchRow("feature/alpha", "primary-1", expectMergeRequest: true, expectApprovalSignal: true);
                AssertBranchRow("feature/alpha", "secondary-1", expectMergeRequest: true, expectApprovalSignal: false);
                AssertBranchRow("feature/beta", "primary-2", expectMergeRequest: true, expectApprovalSignal: false);
                AssertAllCardsUseLeastReadyStatus();
                Log.Information("test1 dashboard card data verified");
            });

        await VerifyResponsiveCardLayout("test1");

        await TestUserDashboard(
            "test2",
            () =>
            {
                AssertBranchRow("feature/gamma", "primary-1", expectMergeRequest: true, expectApprovalSignal: false);
                AssertBranchRow("feature/gamma", "secondary-1", expectMergeRequest: true, expectApprovalSignal: true);
                AssertBranchRow("feature/gamma", "secondary-2", expectMergeRequest: true, expectApprovalSignal: false);
                AssertAllCardsUseLeastReadyStatus();
                Log.Information("test2 dashboard card data verified");
            });

        await TestUserDashboard(
            "test3",
            () =>
            {
                AssertBranchRow("feature/delta", "secondary-3", expectMergeRequest: false, expectApprovalSignal: false);
                AssertAllCardsUseLeastReadyStatus();
                Log.Information("test3 dashboard card data verified");
            });

        Log.Information("Dashboard test passed for all users");
    }

    private async Task TestUserDashboard(string username, Action verify)
    {
        Log.Information("Testing dashboard cards for user '{Username}'...", username);

        await _browser.Page.Context.ClearCookiesAsync();
        await Task.Delay(500);

        await LoginToMergician(username);
        await WaitForDashboard(username);

        verify();
    }

    private async Task LoginToMergician(string username)
    {
        await _browser.Navigate($"{TestConfig.MergicianUrl}/api/auth/login");
        await Task.Delay(2000);
        await _browser.TakeScreenshot($"dashboard_{username}_01_login_redirect");

        var currentUrl = _browser.Page.Url;
        Log.Information("URL after login redirect: {CurrentUrl}", currentUrl);

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
            Log.Information("URL after sign in: {CurrentUrl}", currentUrl);
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

            await _browser.TakeScreenshot($"dashboard_{username}_03_after_authorize");
        }

        Log.Information("Logged into Mergician as {Username}", username);
    }

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
        _parsedRows = _parsedCards.SelectMany(c => c.Rows).ToList();

        Log.Information("Dashboard rendered {Count} cards for '{Username}':", _parsedCards.Count, username);
        foreach (var card in _parsedCards)
        {
            Log.Information("  {Branch} — GroupStatus={Status}", card.Branch, card.Status);
            foreach (var row in card.Rows)
            {
                Log.Information(
                    "    {Repo} — Status={Status}, Approvals={Approvals}",
                    row.Repo,
                    row.Status,
                    row.Approvals);
            }
        }
    }

    private async Task<bool> WaitForStreamCompletion(int timeoutSeconds)
    {
        for (var second = 0; second < timeoutSeconds; second++)
        {
            var cardsExist = await _browser.Page.Locator(".merge-group-card").CountAsync() > 0;
            if (!cardsExist)
            {
                if (second % 10 == 0)
                {
                    Log.Information("Waiting for dashboard cards to appear... {Seconds}s", second);
                }

                await Task.Delay(1000);
                continue;
            }

            var spinnerCount =
                await _browser.Page.Locator(".merge-group-card .v-progress-circular").CountAsync();

            if (spinnerCount == 0)
            {
                Log.Information("Dashboard stream completed after ~{Seconds}s (no spinners remaining)", second);
                return true;
            }

            if (second % 10 == 0)
            {
                Log.Information(
                    "Waiting for stream to resolve... {SpinnerCount} spinners remaining, {Seconds}s elapsed",
                    spinnerCount,
                    second);
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private async Task<List<DashboardCardData>> ParseDashboardCards()
    {
        var cards = new List<DashboardCardData>();

        var cardLocators = _browser.Page.Locator(".merge-group-card");
        var cardCount = await cardLocators.CountAsync();

        for (var i = 0; i < cardCount; i++)
        {
            var card = cardLocators.Nth(i);
            var branchName = (await card.GetAttributeAsync("data-branch-name") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(branchName))
            {
                branchName = (await card.Locator(".merge-group-branch").First.InnerTextAsync()).Trim();
            }

            var groupStatus = (await card.Locator(".group-status-chip").First.InnerTextAsync()).Trim();

            var rows = new List<DashboardRowData>();
            var rowLocators = card.Locator(".repo-row");
            var rowCount = await rowLocators.CountAsync();

            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var row = rowLocators.Nth(rowIndex);
                var repoName = (await row.GetAttributeAsync("data-repo-name") ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(repoName))
                {
                    repoName = (await row.Locator(".text-body-2").First.InnerTextAsync()).Trim();
                }

                var statusChip = row.Locator(".repo-status-chip");
                var status = await statusChip.CountAsync() > 0
                    ? (await statusChip.First.InnerTextAsync()).Trim()
                    : "Loading";

                var approvalsChip = row.Locator(".repo-approvals-chip");
                var approvals = await approvalsChip.CountAsync() > 0
                    ? (await approvalsChip.First.InnerTextAsync()).Trim()
                    : "—";

                rows.Add(new DashboardRowData(branchName, repoName, status, approvals));
            }

            cards.Add(new DashboardCardData(branchName, groupStatus, rows));
        }

        return cards;
    }

    private void AssertBranchRow(
        string branchName,
        string repoContains,
        bool expectMergeRequest,
        bool expectApprovalSignal)
    {
        var match = _parsedRows.FirstOrDefault(row =>
            row.Branch.Contains(branchName, StringComparison.OrdinalIgnoreCase)
            && row.Repo.Contains(repoContains, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            var available = string.Join(
                ", ",
                _parsedRows.Select(row => $"{row.Branch}@{row.Repo}"));

            throw new InvalidOperationException(
                $"Expected branch '{branchName}' in repo containing '{repoContains}' "
                + $"not found in dashboard UI. Available: [{available}]");
        }

        if (expectMergeRequest && match.Status.Equals("Waiting", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' in '{repoContains}' expected MR status, got '{match.Status}'");
        }

        if (!expectMergeRequest && !match.Status.Equals("Waiting", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' in '{repoContains}' expected Waiting status, got '{match.Status}'");
        }

        if (expectApprovalSignal)
        {
            if (!TryParseApprovals(match.Approvals, out var approvalsGiven, out _) || approvalsGiven <= 0)
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' in '{repoContains}': expected approvals > 0, got '{match.Approvals}'");
            }
        }

        Log.Information(
            "  Verified: {Branch} in {Repo} — Status={Status}, Approvals={Approvals}",
            branchName,
            repoContains,
            match.Status,
            match.Approvals);
    }

    private void AssertAllCardsUseLeastReadyStatus()
    {
        foreach (var card in _parsedCards)
        {
            var expectedStatus = CalculateLeastReadyStatus(card.Rows);
            if (!card.Status.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Card '{card.Branch}' expected least-ready status '{expectedStatus}', got '{card.Status}'");
            }
        }

        Log.Information("Verified least-ready group status for {Count} cards", _parsedCards.Count);
    }

    private async Task VerifyResponsiveCardLayout(string username)
    {
        var viewports = new List<(int Width, int Height, string Name)>
        {
            (1280, 900, "desktop"),
            (900, 1200, "tablet"),
            (390, 844, "mobile")
        };

        foreach (var viewport in viewports)
        {
            Log.Information(
                "Verifying responsive dashboard layout at {Name} viewport ({Width}x{Height})...",
                viewport.Name,
                viewport.Width,
                viewport.Height);

            await _browser.Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
            await Task.Delay(1000);

            var cardCount = await _browser.Page.Locator(".merge-group-card").CountAsync();
            if (cardCount == 0)
            {
                throw new InvalidOperationException(
                    $"Expected at least one dashboard card at {viewport.Name} viewport, found none");
            }

            var hasHorizontalOverflow = await _browser.Page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > window.innerWidth + 2");

            if (hasHorizontalOverflow)
            {
                throw new InvalidOperationException(
                    $"Dashboard has horizontal overflow at {viewport.Name} viewport ({viewport.Width}x{viewport.Height})");
            }

            await _browser.TakeScreenshot($"dashboard_{username}_responsive_{viewport.Name}");
        }

        Log.Information("Responsive dashboard card layout verified across multiple viewport sizes");
    }

    private static string CalculateLeastReadyStatus(IEnumerable<DashboardRowData> rows)
    {
        var highestPriority = 0;

        foreach (var row in rows)
        {
            var priority = GetStatusPriority(row.Status);
            if (priority > highestPriority)
            {
                highestPriority = priority;
            }
        }

        return highestPriority switch
        {
            3 => "Waiting",
            2 => "Open",
            _ => "Ready"
        };
    }

    private static int GetStatusPriority(string status)
    {
        return status switch
        {
            "Waiting" => 3,
            "Open" => 2,
            "Ready" => 1,
            _ => 0
        };
    }

    private static bool TryParseApprovals(string approvals, out int given, out int required)
    {
        given = 0;
        required = 0;

        if (string.IsNullOrWhiteSpace(approvals))
        {
            return false;
        }

        var parts = approvals.Split('/');
        if (parts.Length != 2)
        {
            return false;
        }

        return int.TryParse(parts[0], out given) && int.TryParse(parts[1], out required);
    }
}

public sealed record DashboardCardData(string Branch, string Status, List<DashboardRowData> Rows);

public sealed record DashboardRowData(string Branch, string Repo, string Status, string Approvals);
