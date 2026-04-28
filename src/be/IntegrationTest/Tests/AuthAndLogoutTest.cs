using Microsoft.Playwright;
using PlaywrightService;
using Serilog;
using IntegrationTest.Services;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests that a user can authenticate to Mergician via GitLab OAuth,
///     and that logging out clears the session and returns the user to the welcome page.
///     Runs as a single sequential test so the logout verification piggybacks on the
///     already-established authenticated session from the login step.
///     Logs in as test1 (created by Bootstrap's ProjectSetupService).
/// </summary>
public class AuthAndLogoutTest
{
    private readonly BrowserService _browser;

    public AuthAndLogoutTest(BrowserService browser)
    {
        _browser = browser;
    }

    public async Task Run()
    {
        _browser.SetScreenshotDir(Path.Combine(TestConfig.ScreenshotDir, "auth-and-logout"));

        await TestAuthentication();
        await TestLogout();

        Log.Information("Auth and logout tests passed");
    }

    /// <summary>
    ///     Verifies that navigating to Mergician shows the welcome page, that clicking
    ///     the Sign in button triggers the GitLab OAuth flow, and that after completing
    ///     it the user lands on the dashboard with a visible Logout button.
    /// </summary>
    private async Task TestAuthentication()
    {
        Log.Information("Testing: authentication via GitLab OAuth...");

        // Step 1: Navigate to Mergician home — should show welcome page (not logged in)
        Log.Information("Navigating to Mergician home page...");
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
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

        var currentUrl = _browser.Page.Url;
        Log.Information("URL after login redirect: {Url}", currentUrl);

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
            Log.Information("URL after sign in: {Url}", currentUrl);
        }

        // Step 3: GitLab may show an OAuth authorization page — authorize if needed
        if (currentUrl.Contains("/oauth/authorize"))
        {
            Log.Information("OAuth authorization page detected, submitting...");
            await HandleOAuthAuthorize();
            currentUrl = _browser.Page.Url;
            Log.Information("URL after authorize: {Url}", currentUrl);
        }

        await _browser.TakeScreenshot("06_final_url");

        // Step 4: Verify we're logged in — navigate to the home page and check the UI
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(3000);
        await _browser.TakeScreenshot("07_logged_in_home");

        var pageContent = await _browser.GetPageContent();

        if (pageContent.Contains("Sign in with GitLab"))
        {
            throw new InvalidOperationException(
                "Expected dashboard after login, but welcome page is still showing");
        }

        var logoutButton = _browser.Page.Locator("button:has-text('Logout')");
        var logoutVisible = await BrowserService.WaitForElement(logoutButton, timeoutMs: 10000);
        if (!logoutVisible)
        {
            throw new InvalidOperationException(
                "Expected Logout button in app bar after login, but it was not found");
        }

        if (!pageContent.Contains("Dashboard") && !pageContent.Contains("No active branches"))
        {
            throw new InvalidOperationException(
                $"Expected Dashboard content after login, got: {pageContent[..Math.Min(300, pageContent.Length)]}");
        }

        LoginHelper.SetCurrentUser(TestConfig.TestUsername);
        Log.Information("Authentication test passed - user is logged in and dashboard is visible");
    }

    /// <summary>
    ///     Verifies that clicking Logout clears the session and returns to the welcome page
    ///     with no Logout button visible.
    /// </summary>
    private async Task TestLogout()
    {
        Log.Information("Testing: logout clears session and shows welcome page...");

        // Step 1: Click the Logout button
        Log.Information("Clicking Logout button...");
        var logoutButton = _browser.Page.Locator("button:has-text('Logout')");
        await logoutButton.ClickAsync();
        await Task.Delay(2000);
        await _browser.TakeScreenshot("08_after_logout_click");

        // Step 2: Navigate to the home page — should show welcome page, NOT dashboard
        Log.Information("Navigating to home page after logout...");
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
        await _browser.TakeScreenshot("09_home_after_logout");

        var homeContent = await _browser.GetPageContent();

        if (!homeContent.Contains("Sign in with GitLab"))
        {
            throw new InvalidOperationException(
                $"After logout, expected welcome page with 'Sign in with GitLab' button, got: {homeContent[..Math.Min(300, homeContent.Length)]}");
        }

        Log.Information("Welcome page shown after logout - correct");

        // Step 3: Verify the Logout button is no longer visible (session cleared)
        var logoutButtonAfter = _browser.Page.Locator("button:has-text('Logout')");
        var logoutStillVisible = await BrowserService.WaitForElement(
            logoutButtonAfter,
            WaitForSelectorState.Hidden);

        if (!logoutStillVisible)
        {
            throw new InvalidOperationException(
                "After logout, Logout button is still visible — session may not have been cleared");
        }

        await _browser.TakeScreenshot("10_no_logout_button");
        LoginHelper.SetCurrentUser(null);
        Log.Information("Logout test passed - user is signed out and sees welcome page");
    }

    private async Task HandleOAuthAuthorize()
    {
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

        Log.Information("JS authorize click: {Clicked}", clicked);

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

        await _browser.Page.EvaluateAsync("document.querySelector('form')?.submit()");
        try
        {
            await _browser.Page.WaitForURLAsync(
                url => !url.Contains("/oauth/authorize"),
                new PageWaitForURLOptions { Timeout = 15000 });
        }
        catch
        {
            Log.Warning("Form submit didn't navigate. Current URL: {Url}", _browser.Page.Url);
            await _browser.TakeScreenshot("authorize_debug");
        }
    }
}
