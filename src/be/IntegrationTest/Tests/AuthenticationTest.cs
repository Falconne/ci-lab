using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests that a user can authenticate to Mergician via GitLab OAuth.
///     Logs in as test1 (created by Bootstrap's ProjectSetupService).
/// </summary>
public class AuthenticationTest : IDisposable
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
            Path.Combine(TestConfig.ScreenshotDir, "auth"));

        // Step 1: Navigate to Mergician home — should show welcome page (not logged in)
        Log.Information("Navigating to Mergician home page...");
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000); // Wait for Vue to render
        await _browser.TakeScreenshot("01_welcome_page");

        var homeContent = await _browser.GetPageContent();
        if (!homeContent.Contains("Sign in with GitLab"))
        {
            throw new InvalidOperationException(
                $"Expected welcome page with 'Sign in with GitLab' button, got: {homeContent[..Math.Min(300, homeContent.Length)]}");
        }

        Log.Information("Welcome page displayed correctly");

        // Step 2: Click the Sign in button — should redirect to GitLab
        Log.Information("Clicking Sign in with GitLab...");
        var signInLink = _browser.Page.Locator("a:has-text('Sign in with GitLab')");
        await signInLink.ClickAsync();
        await _browser.Page.WaitForURLAsync(
            url => url.Contains("localhost:8081"),
            new PageWaitForURLOptions { Timeout = 30000 });

        await _browser.TakeScreenshot("02_after_sign_in_click");

        // After the redirect chain, we should be on the GitLab login page
        var currentUrl = _browser.Page.Url;
        Log.Information($"URL after login redirect: {currentUrl}");

        if (currentUrl.Contains("/users/sign_in"))
        {
            Log.Information("Reached GitLab login page, entering credentials...");
            await _browser.TakeScreenshot("03_gitlab_login_page");

            var usernameField = _browser.Page.Locator("#user_login");
            var passwordField = _browser.Page.Locator("#user_password");
            var gitlabSignInButton =
                _browser.Page.Locator("input[type='submit'][name='commit'], button[type='submit']");

            await BrowserService.FillFormField(usernameField, TestConfig.TestUsername, "username");
            await BrowserService.FillFormField(passwordField, TestConfig.TestPassword, "password");
            await _browser.TakeScreenshot("04_credentials_filled");

            await gitlabSignInButton.First.ClickAsync();
            await _browser.Page.WaitForURLAsync(
                url => url.Contains("/oauth/authorize") || url.Contains("localhost:5000"),
                new PageWaitForURLOptions { Timeout = 30000 });

            await _browser.TakeScreenshot("05_after_sign_in");

            currentUrl = _browser.Page.Url;
            Log.Information($"URL after sign in: {currentUrl}");
        }

        // Step 3: GitLab may show an OAuth authorization page — authorize if needed
        if (currentUrl.Contains("/oauth/authorize"))
        {
            Log.Information("OAuth authorization page detected, submitting...");
            await HandleOAuthAuthorize();
            currentUrl = _browser.Page.Url;
            Log.Information($"URL after authorize: {currentUrl}");
        }

        await _browser.TakeScreenshot("06_final_url");
        Log.Information($"Final URL: {currentUrl}");

        // Step 4: Verify we're logged in — navigate to the home page and check the UI
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(3000); // Wait for Vue to render and /api/auth/me to resolve
        await _browser.TakeScreenshot("07_logged_in_home");

        var pageContent = await _browser.GetPageContent();

        // The welcome page should NOT be shown (we're logged in)
        if (pageContent.Contains("Sign in with GitLab"))
        {
            throw new InvalidOperationException(
                "Expected dashboard after login, but welcome page is still showing");
        }

        // The AppBar should show the user's name and a Logout button
        var logoutButton = _browser.Page.Locator("button:has-text('Logout')");
        var logoutVisible = await BrowserService.WaitForElement(logoutButton, timeoutMs: 10000);
        if (!logoutVisible)
        {
            throw new InvalidOperationException(
                "Expected Logout button in app bar after login, but it was not found");
        }

        // The Dashboard heading should be visible (or the 'No active branches' message)
        if (!pageContent.Contains("Dashboard") && !pageContent.Contains("No active branches"))
        {
            throw new InvalidOperationException(
                $"Expected Dashboard content after login, got: {pageContent[..Math.Min(300, pageContent.Length)]}");
        }

        Log.Information("Authentication test passed - user is logged in and dashboard is visible");
    }

    private async Task HandleOAuthAuthorize()
    {
        // Try JS click on the authorize button
        var clicked = await _browser.Page.EvaluateAsync<bool>(
            """
            (() => {
                const btn = document.querySelector('[data-testid="authorization-button"]');
                if (btn) { btn.click(); return true; }
                const submit = document.querySelector('input[type="submit"][value="Authorize"]');
                if (submit) { submit.click(); return true; }
                return false;
            })()
            """);

        Log.Information($"JS authorize click: {clicked}");

        if (clicked)
        {
            try
            {
                await _browser.Page.WaitForURLAsync(
                    url => !url.Contains("/oauth/authorize"),
                    new PageWaitForURLOptions { Timeout = 15000 });

                return;
            }
            catch
            {
                Log.Warning("JS click didn't cause navigation, trying form submit...");
            }
        }

        // Fallback: submit the first form on the page
        await _browser.Page.EvaluateAsync("document.querySelector('form')?.submit()");
        try
        {
            await _browser.Page.WaitForURLAsync(
                url => !url.Contains("/oauth/authorize"),
                new PageWaitForURLOptions { Timeout = 15000 });
        }
        catch
        {
            Log.Warning($"Form submit didn't navigate. Current URL: {_browser.Page.Url}");
            await _browser.TakeScreenshot("authorize_debug");
        }
    }
}