using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
/// Tests that the dashboard UI displays the expected branch activity data
/// after SSE streaming completes. Uses Playwright to interact with the actual
/// frontend, just as a real user would.
///
/// Expected data per user (created by ProjectSetupService.SetupTestBranchData):
///   test1: feature/alpha (primary-1 with MR+approval, secondary-1 with MR),
///          feature/beta (primary-2 with MR, no approval)
///   test2: feature/gamma (primary-1 with MR, secondary-1 with MR+approval, secondary-2 with MR)
///   test3: feature/delta (secondary-3, no MR)
/// </summary>
public class DashboardTest : IDisposable
{
    private readonly BrowserService _browser = new();

    public void Dispose()
    {
        _browser.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task Run()
    {
        await _browser.Initialize(
            Path.Combine(TestConfig.ScreenshotDir, "activity"),
            headless: true);

        // Test with test1 — should see feature/alpha and feature/beta
        await TestUserDashboard("test1", () =>
        {
            AssertBranchRow("feature/alpha", "primary-1", hasMr: true, expectApproval: true);
            AssertBranchRow("feature/alpha", "secondary-1", hasMr: true, expectApproval: false);
            AssertBranchRow("feature/beta", "primary-2", hasMr: true, expectApproval: false);
            Log.Information("test1 dashboard data verified");
        });

        // Test with test2 — should see feature/gamma
        await TestUserDashboard("test2", () =>
        {
            AssertBranchRow("feature/gamma", "primary-1", hasMr: true, expectApproval: false);
            AssertBranchRow("feature/gamma", "secondary-1", hasMr: true, expectApproval: true);
            AssertBranchRow("feature/gamma", "secondary-2", hasMr: true, expectApproval: false);
            Log.Information("test2 dashboard data verified");
        });

        // Test with test3 — should see feature/delta (no MR)
        await TestUserDashboard("test3", () =>
        {
            AssertBranchRow("feature/delta", "secondary-3", hasMr: false, expectApproval: false);
            Log.Information("test3 dashboard data verified");
        });

        Log.Information("Dashboard test passed for all users");
    }

    /// <summary>
    /// Cached page content from the dashboard table, populated by WaitForDashboard.
    /// </summary>
    private string _dashboardContent = "";

    /// <summary>
    /// Cached table rows from the dashboard, populated by WaitForDashboard.
    /// Each tuple is (branchName, repoName, hasMrIcon, approvalsText).
    /// </summary>
    private List<(string Branch, string Repo, bool HasMr, string Approvals)> _parsedRows = [];

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
        await _browser.Navigate($"{TestConfig.MergicianUrl}/api/auth/login", WaitUntilState.NetworkIdle);
        await Task.Delay(2000);
        await _browser.TakeScreenshot($"dashboard_{username}_01_login_redirect");

        var currentUrl = _browser.Page.Url;
        Log.Information($"URL after login redirect: {currentUrl}");

        if (currentUrl.Contains("/users/sign_in"))
        {
            var usernameField = _browser.Page.Locator("#user_login");
            var passwordField = _browser.Page.Locator("#user_password");
            var signInButton = _browser.Page.Locator("input[type='submit'][name='commit'], button[type='submit']");

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
            await _browser.Page.EvaluateAsync("""
                (() => {
                    const btn = document.querySelector('[data-testid="authorization-button"]');
                    if (btn) { btn.click(); return; }
                    const form = document.querySelector('form');
                    if (form) { form.submit(); }
                })()
                """);
            try
            {
                await _browser.Page.WaitForURLAsync(url => !url.Contains("/oauth/authorize"),
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
    /// Navigates to the Mergician home page and waits for the SSE activity stream
    /// to finish (the loading spinner disappears and the dashboard table is rendered).
    /// Parses the rendered table rows into _parsedRows for assertion.
    /// </summary>
    private async Task WaitForDashboard(string username)
    {
        await _browser.Navigate(TestConfig.MergicianUrl, WaitUntilState.NetworkIdle);
        await Task.Delay(2000);
        await _browser.TakeScreenshot($"dashboard_{username}_04_initial_load");

        // Wait for SSE streaming to complete — the v-progress-circular spinner in the
        // Dashboard heading disappears once the "done" SSE event is received.
        // We detect this by waiting for all loading spinners in the table to disappear,
        // meaning all MR/approval data has been resolved.
        Log.Information("Waiting for SSE activity stream to complete...");
        var streamComplete = await WaitForStreamCompletion(timeoutSeconds: 120);
        if (!streamComplete)
        {
            await _browser.TakeScreenshot($"dashboard_{username}_05_stream_timeout");
            throw new InvalidOperationException(
                "Dashboard SSE stream did not complete within timeout");
        }

        await _browser.TakeScreenshot($"dashboard_{username}_05_stream_complete");

        // Parse the rendered dashboard table
        _dashboardContent = await _browser.GetPageContent();
        _parsedRows = await ParseDashboardTable();

        Log.Information($"Dashboard rendered {_parsedRows.Count} rows for '{username}':");
        foreach (var row in _parsedRows)
        {
            Log.Information($"  {row.Branch} / {row.Repo} — MR={row.HasMr}, Approvals={row.Approvals}");
        }
    }

    /// <summary>
    /// Waits until there are no more loading spinners in the dashboard table,
    /// meaning all data has been resolved via SSE.
    /// </summary>
    private async Task<bool> WaitForStreamCompletion(int timeoutSeconds)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            // Check if the dashboard table exists
            var tableExists = await _browser.Page.Locator(".dashboard-table").CountAsync() > 0;
            if (!tableExists)
            {
                if (s % 10 == 0)
                    Log.Information($"Waiting for dashboard table to appear... {s}s");

                await Task.Delay(1000);
                continue;
            }

            // Check for loading spinners in the table (v-progress-circular elements)
            var spinnerCount = await _browser.Page.Locator(".dashboard-table .v-progress-circular").CountAsync();
            if (spinnerCount == 0)
            {
                Log.Information($"Dashboard stream completed after ~{s}s (no spinners remaining)");
                return true;
            }

            if (s % 10 == 0)
                Log.Information($"Waiting for stream to resolve... {spinnerCount} spinners remaining, {s}s elapsed");

            await Task.Delay(1000);
        }

        return false;
    }

    /// <summary>
    /// Parses the rendered dashboard HTML table into structured row data.
    /// The table uses rowspan for branch names, so we track the current branch
    /// across rows that don't have a branch cell.
    /// </summary>
    private async Task<List<(string Branch, string Repo, bool HasMr, string Approvals)>> ParseDashboardTable()
    {
        var rows = new List<(string Branch, string Repo, bool HasMr, string Approvals)>();

        var tableRows = _browser.Page.Locator(".dashboard-table tbody tr");
        var rowCount = await tableRows.CountAsync();

        var currentBranch = "";

        for (var i = 0; i < rowCount; i++)
        {
            var row = tableRows.Nth(i);
            var cells = row.Locator("td");
            var cellCount = await cells.CountAsync();

            // If the row has a branch-name-cell (rowspan cell), it's the first row of a group
            var branchCell = row.Locator(".branch-name-cell");
            var hasBranchCell = await branchCell.CountAsync() > 0;

            int repoIndex;
            if (hasBranchCell)
            {
                currentBranch = (await branchCell.InnerTextAsync()).Trim();
                repoIndex = 1; // repo is the second cell
            }
            else
            {
                repoIndex = 0; // no branch cell, repo is the first cell
            }

            var repoName = (await cells.Nth(repoIndex).InnerTextAsync()).Trim();

            // MR column: check for mdi-check-circle (has MR) vs mdi-minus-circle-outline (no MR)
            var mrCell = cells.Nth(repoIndex + 1);
            var hasMr = await mrCell.Locator(".mdi-check-circle").CountAsync() > 0;

            // Approvals column: get the text content (e.g. "1/1" or "—")
            var approvalsCell = cells.Nth(repoIndex + 2);
            var approvalsText = (await approvalsCell.InnerTextAsync()).Trim();

            rows.Add((currentBranch, repoName, hasMr, approvalsText));
        }

        return rows;
    }

    /// <summary>
    /// Asserts that a specific branch/repo combination exists in the parsed dashboard rows
    /// with the expected MR and approval status.
    /// </summary>
    private void AssertBranchRow(
        string branchName,
        string repoContains,
        bool hasMr,
        bool expectApproval)
    {
        var match = _parsedRows.FirstOrDefault(r =>
            r.Branch.Contains(branchName, StringComparison.OrdinalIgnoreCase) &&
            r.Repo.Contains(repoContains, StringComparison.OrdinalIgnoreCase));

        if (match == default)
        {
            var available = string.Join(", ",
                _parsedRows.Select(r => $"{r.Branch}@{r.Repo}"));
            throw new InvalidOperationException(
                $"Expected branch '{branchName}' in repo containing '{repoContains}' " +
                $"not found in dashboard UI. Available: [{available}]");
        }

        if (match.HasMr != hasMr)
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' in '{repoContains}': " +
                $"expected MR icon={hasMr}, got {match.HasMr}");
        }

        if (expectApproval)
        {
            // Approvals text should be something like "1/1", not "—"
            if (match.Approvals == "—" || string.IsNullOrWhiteSpace(match.Approvals))
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' in '{repoContains}': " +
                    $"expected approvals, got '{match.Approvals}'");
            }

            // Parse "X/Y" and verify X > 0
            var parts = match.Approvals.Split('/');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var given) || given <= 0)
            {
                throw new InvalidOperationException(
                    $"Branch '{branchName}' in '{repoContains}': " +
                    $"expected approvals given > 0, got '{match.Approvals}'");
            }
        }

        Log.Information(
            $"  Verified: {branchName} in {repoContains} — MR={match.HasMr}, Approvals={match.Approvals}");
    }
}
