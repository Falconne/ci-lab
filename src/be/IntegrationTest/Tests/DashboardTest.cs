using IntegrationTest.Entities;
using Microsoft.Playwright;
using PlaywrightService;
using Serilog;
using System.Text.Json;

namespace IntegrationTest.Tests;

/// <summary>
/// Tests that the dashboard API returns the expected branch activity data
/// based on the deterministic test data created by the bootstrapper.
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
        await TestUserDashboard("test1", activity =>
        {
            // test1 should see branches they pushed to
            var branchNames = activity.Select(a => a.BranchName).Distinct().ToList();
            Log.Information($"test1 branches: {string.Join(", ", branchNames)}");

            AssertContainsBranch(activity, "feature/alpha", "primary-1",
                hasMr: true, expectApproval: true);
            AssertContainsBranch(activity, "feature/alpha", "secondary-1",
                hasMr: true, expectApproval: false);
            AssertContainsBranch(activity, "feature/beta", "primary-2",
                hasMr: true, expectApproval: false);

            Log.Information("test1 dashboard data verified");
        });

        // Test with test2 — should see feature/gamma
        await TestUserDashboard("test2", activity =>
        {
            var branchNames = activity.Select(a => a.BranchName).Distinct().ToList();
            Log.Information($"test2 branches: {string.Join(", ", branchNames)}");

            AssertContainsBranch(activity, "feature/gamma", "primary-1",
                hasMr: true, expectApproval: false);
            AssertContainsBranch(activity, "feature/gamma", "secondary-1",
                hasMr: true, expectApproval: true);
            AssertContainsBranch(activity, "feature/gamma", "secondary-2",
                hasMr: true, expectApproval: false);

            Log.Information("test2 dashboard data verified");
        });

        // Test with test3 — should see feature/delta (no MR)
        await TestUserDashboard("test3", activity =>
        {
            var branchNames = activity.Select(a => a.BranchName).Distinct().ToList();
            Log.Information($"test3 branches: {string.Join(", ", branchNames)}");

            AssertContainsBranch(activity, "feature/delta", "secondary-3",
                hasMr: false, expectApproval: false);

            Log.Information("test3 dashboard data verified");
        });

        Log.Information("Dashboard test passed for all users");
    }

    private async Task TestUserDashboard(string username, Action<List<BranchActivityDto>> verify)
    {
        Log.Information($"Testing dashboard for user '{username}'...");

        // Clear all cookies/state from previous logins to ensure clean session
        await _browser.Page.Context.ClearCookiesAsync();
        await Task.Delay(500);

        // Login as this user via Mergician OAuth flow
        await LoginToMergician(username);

        // Hit the dashboard API
        var activity = await FetchDashboardActivity();

        Log.Information($"Dashboard returned {activity.Count} records for '{username}'");

        // Verify expectations
        verify(activity);
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

        // Verify we're authenticated
        await _browser.Navigate($"{TestConfig.MergicianUrl}/api/auth/me", WaitUntilState.NetworkIdle);
        var meContent = await _browser.GetPageContent();
        await _browser.TakeScreenshot($"dashboard_{username}_04_auth_verified");

        if (!meContent.Contains(username))
        {
            throw new InvalidOperationException(
                $"Login failed: expected '{username}' in /api/auth/me, got: {meContent[..Math.Min(200, meContent.Length)]}");
        }

        Log.Information($"Logged into Mergician as {username}");
    }

    private async Task<List<BranchActivityDto>> FetchDashboardActivity()
    {
        await _browser.Navigate($"{TestConfig.MergicianUrl}/api/activity", WaitUntilState.NetworkIdle);
        await Task.Delay(2000);
        await _browser.TakeScreenshot("dashboard_api_response");

        var rawContent = await _browser.GetPageContent();
        Log.Information($"Activity API raw response: {rawContent[..Math.Min(1000, rawContent.Length)]}");

        if (rawContent.Contains("Unauthorized") || rawContent.Contains("401"))
        {
            throw new InvalidOperationException("Activity API returned unauthorized");
        }

        // The browser wraps JSON in HTML when navigating to an API endpoint.
        // Extract the JSON from the <pre> tag if present.
        var jsonContent = rawContent;
        var preStart = rawContent.IndexOf("<pre>", StringComparison.Ordinal);
        var preEnd = rawContent.IndexOf("</pre>", StringComparison.Ordinal);
        if (preStart >= 0 && preEnd > preStart)
        {
            jsonContent = rawContent[(preStart + 5)..preEnd];
            Log.Information($"Extracted JSON from <pre> tag: {jsonContent[..Math.Min(500, jsonContent.Length)]}");
        }

        var activities = JsonSerializer.Deserialize<List<BranchActivityDto>>(jsonContent,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return activities ?? [];
    }

    private static void AssertContainsBranch(
        List<BranchActivityDto> activities,
        string branchName,
        string projectNameContains,
        bool hasMr,
        bool expectApproval)
    {
        var match = activities.FirstOrDefault(a =>
            a.BranchName == branchName &&
            a.ProjectName.Contains(projectNameContains, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            var available = string.Join(", ",
                activities.Select(a => $"{a.BranchName}@{a.ProjectName}"));
            throw new InvalidOperationException(
                $"Expected branch '{branchName}' in project containing '{projectNameContains}' " +
                $"not found in dashboard. Available: [{available}]");
        }

        if (match.HasMergeRequest != hasMr)
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' in '{projectNameContains}': " +
                $"expected HasMergeRequest={hasMr}, got {match.HasMergeRequest}");
        }

        if (expectApproval && match.ApprovalsGiven is null or 0)
        {
            throw new InvalidOperationException(
                $"Branch '{branchName}' in '{projectNameContains}': " +
                $"expected approvals given > 0, got {match.ApprovalsGiven}");
        }

        Log.Information(
            $"  Verified: {branchName} in {projectNameContains} — MR={match.HasMergeRequest}, " +
            $"Approvals={match.ApprovalsGiven}/{match.ApprovalsRequired}");
    }
}
