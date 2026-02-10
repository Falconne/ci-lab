using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
/// Tests that logging out of Mergician actually clears the session and
/// returns the user to the welcome page rather than re-authenticating via GitLab.
/// </summary>
public class LogoutTest : IDisposable
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
            Path.Combine(TestConfig.ScreenshotDir, "logout"),
            headless: true);

        // Step 1: Log in first
        Log.Information("Logging in before testing logout...");
        await LoginViaGitLab();

        // Verify we're authenticated
        await _browser.Navigate($"{TestConfig.MergicianUrl}/api/auth/me", WaitUntilState.NetworkIdle);
        var meContent = await _browser.GetPageContent();
        await _browser.TakeScreenshot("01_logged_in_me");

        if (!meContent.Contains(TestConfig.TestUsername))
        {
            throw new InvalidOperationException(
                $"Pre-condition failed: not logged in. /api/auth/me returned: {meContent[..Math.Min(200, meContent.Length)]}");
        }

        Log.Information("Confirmed logged in as test1");

        // Step 2: Call the logout endpoint
        Log.Information("Calling logout endpoint...");
        await _browser.Page.EvaluateAsync("fetch('/api/auth/logout', { method: 'POST' })");
        await Task.Delay(1000);
        await _browser.TakeScreenshot("02_after_logout_api_call");

        // Step 3: Navigate to the home page — should show welcome page, NOT activity
        Log.Information("Navigating to home page after logout...");
        await _browser.Navigate(TestConfig.MergicianUrl, WaitUntilState.NetworkIdle);
        await Task.Delay(2000);
        await _browser.TakeScreenshot("03_home_after_logout");

        var homeContent = await _browser.GetPageContent();

        // The page should contain the welcome/sign-in content, not activity
        if (!homeContent.Contains("Sign in with GitLab"))
        {
            throw new InvalidOperationException(
                $"After logout, expected welcome page with 'Sign in with GitLab' button, got: {homeContent[..Math.Min(300, homeContent.Length)]}");
        }

        Log.Information("Welcome page shown after logout - correct");

        // Step 4: Verify /api/auth/me returns 401 (not authenticated)
        await _browser.Navigate($"{TestConfig.MergicianUrl}/api/auth/me", WaitUntilState.NetworkIdle);
        var meAfterLogout = await _browser.GetPageContent();
        await _browser.TakeScreenshot("04_me_after_logout");

        if (meAfterLogout.Contains(TestConfig.TestUsername))
        {
            throw new InvalidOperationException(
                "After logout, /api/auth/me still returns user info — session was not cleared");
        }

        if (!meAfterLogout.Contains("401") && !meAfterLogout.Contains("Unauthorized"))
        {
            throw new InvalidOperationException(
                $"After logout, expected 401 from /api/auth/me, got: {meAfterLogout[..Math.Min(200, meAfterLogout.Length)]}");
        }

        Log.Information("Logout test passed - user is signed out and sees welcome page");
    }

    private async Task LoginViaGitLab()
    {
        await _browser.Navigate($"{TestConfig.MergicianUrl}/api/auth/login", WaitUntilState.NetworkIdle);

        var currentUrl = _browser.Page.Url;

        if (currentUrl.Contains("/users/sign_in"))
        {
            var usernameField = _browser.Page.Locator("#user_login");
            var passwordField = _browser.Page.Locator("#user_password");
            var signInButton = _browser.Page.Locator("input[type='submit'][name='commit'], button[type='submit']");

            await BrowserService.FillFormField(usernameField, TestConfig.TestUsername, "username");
            await BrowserService.FillFormField(passwordField, TestConfig.TestPassword, "password");
            await signInButton.First.ClickAsync();
            // Wait for either the OAuth authorize page or the final redirect back to Mergician
            await _browser.Page.WaitForURLAsync(
                url => url.Contains("/oauth/authorize") || url.Contains("localhost:5000"),
                new PageWaitForURLOptions { Timeout = 30000 });

            currentUrl = _browser.Page.Url;
        }

        if (currentUrl.Contains("/oauth/authorize"))
        {
            await _browser.Page.EvaluateAsync("""
                (() => {
                    const btn = document.querySelector('[data-testid="authorization-button"]');
                    if (btn) { btn.click(); return; }
                    const submit = document.querySelector('input[type="submit"][value="Authorize"]');
                    if (submit) { submit.click(); return; }
                    document.querySelector('form')?.submit();
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
        }
    }
}
