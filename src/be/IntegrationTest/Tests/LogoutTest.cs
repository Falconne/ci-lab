using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Tests;

/// <summary>
///     Tests that logging out of Mergician actually clears the session and
///     returns the user to the welcome page rather than re-authenticating via GitLab.
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
            Path.Combine(TestConfig.ScreenshotDir, "logout"));

        // Step 1: Log in first
        Log.Information("Logging in before testing logout...");
        await LoginViaGitLab();

        // Verify we're authenticated by checking the rendered UI
        await _browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(3000); // Wait for Vue to render
        await _browser.TakeScreenshot("01_logged_in_home");

        var logoutButton = _browser.Page.Locator("button:has-text('Logout')");
        var logoutVisible = await BrowserService.WaitForElement(logoutButton, timeoutMs: 10000);
        if (!logoutVisible)
        {
            throw new InvalidOperationException(
                "Pre-condition failed: not logged in. Logout button not found in app bar.");
        }

        Log.Information("Confirmed logged in (Logout button visible in app bar)");

        // Step 2: Click the Logout button in the app bar (same as production flow)
        Log.Information("Clicking Logout button...");
        await logoutButton.ClickAsync();
        await Task.Delay(2000); // Wait for logout redirect to complete
        await _browser.TakeScreenshot("02_after_logout_click");

        // Step 3: Navigate to the home page — should show welcome page, NOT activity
        Log.Information("Navigating to home page after logout...");
        await _browser.Navigate(TestConfig.MergicianUrl);
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

        // Step 4: Verify the Logout button is no longer visible (session cleared)
        var logoutButtonAfter = _browser.Page.Locator("button:has-text('Logout')");
        var logoutStillVisible = await BrowserService.WaitForElement(
            logoutButtonAfter,
            WaitForSelectorState.Hidden);

        if (!logoutStillVisible)
        {
            throw new InvalidOperationException(
                "After logout, Logout button is still visible — session may not have been cleared");
        }

        await _browser.TakeScreenshot("04_no_logout_button");
        Log.Information("Logout test passed - user is signed out and sees welcome page");
    }

    private async Task LoginViaGitLab()
    {
        await _browser.Navigate($"{TestConfig.MergicianUrl}/api/auth/login");

        var currentUrl = _browser.Page.Url;

        if (currentUrl.Contains("/users/sign_in"))
        {
            var usernameField = _browser.Page.Locator("#user_login");
            var passwordField = _browser.Page.Locator("#user_password");
            var signInButton =
                _browser.Page.Locator("input[type='submit'][name='commit'], button[type='submit']");

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
            await _browser.Page.EvaluateAsync(
                """
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
                await _browser.Page.WaitForURLAsync(
                    url => !url.Contains("/oauth/authorize"),
                    new PageWaitForURLOptions { Timeout = 15000 });
            }
            catch
            {
                Log.Warning($"OAuth authorize didn't redirect. URL: {_browser.Page.Url}");
            }
        }
    }
}