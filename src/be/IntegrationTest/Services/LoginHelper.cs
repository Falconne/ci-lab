using Microsoft.Playwright;
using PlaywrightService;
using Serilog;

namespace IntegrationTest.Services;

/// <summary>
///     Centralised OAuth login helper for integration tests.
///     Tracks the current logged-in user and supports session reuse
///     to avoid redundant OAuth round-trips.
/// </summary>
public static class LoginHelper
{
    private static string? _currentUser;

    /// <summary>
    ///     Full OAuth login: clears cookies, authenticates as <paramref name="username"/>,
    ///     navigates to the dashboard and waits for it to fully load.
    ///     Updates <see cref="_currentUser"/> on success.
    /// </summary>
    public static async Task LoginAndWaitForDashboard(BrowserService browser, string username)
    {
        Log.Information("Logging in as '{Username}'...", username);
        await browser.Page.Context.ClearCookiesAsync();
        await Task.Delay(500);

        await PerformOAuthLogin(browser, username);

        await browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
        await WaitForDashboardReady(browser);
        _currentUser = username;
        Log.Information("Logged in as '{Username}' and dashboard loaded", username);
    }

    /// <summary>
    ///     Navigates to the dashboard and waits for it to fully load, reusing the existing
    ///     session when the current user already matches <paramref name="username"/>.
    ///     Falls back to a full <see cref="LoginAndWaitForDashboard"/> when the session
    ///     has expired or belongs to a different user.
    /// </summary>
    public static async Task EnsureLoggedIn(BrowserService browser, string username)
    {
        if (_currentUser == username)
        {
            Log.Information("Session already established for '{Username}', verifying...", username);
            await browser.Navigate(TestConfig.MergicianUrl);
            await Task.Delay(1500);

            var content = await browser.GetPageContent();
            if (!content.Contains("Sign in with GitLab", StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("Session still valid for '{Username}', using existing session", username);
                await WaitForDashboardReady(browser);
                return;
            }

            Log.Warning("Expected active session for '{Username}' but login page shown; re-authenticating", username);
            _currentUser = null;
        }

        await LoginAndWaitForDashboard(browser, username);
    }

    /// <summary>
    ///     Navigates to the dashboard home and waits for it to fully load,
    ///     without touching the session state.
    /// </summary>
    public static async Task NavigateToDashboard(BrowserService browser)
    {
        await browser.Navigate(TestConfig.MergicianUrl);
        await Task.Delay(2000);
        await WaitForDashboardReady(browser);
    }

    /// <summary>
    ///     Explicitly sets the current-user hint (e.g. after UI-based auth tests
    ///     that manage the session themselves).
    /// </summary>
    public static void SetCurrentUser(string? username)
    {
        _currentUser = username;
    }

    private static async Task PerformOAuthLogin(BrowserService browser, string username)
    {
        await browser.Navigate($"{TestConfig.MergicianUrl}/api/auth/login");
        await Task.Delay(2000);

        var currentUrl = browser.Page.Url;
        Log.Information("URL after login redirect: {Url}", currentUrl);

        if (currentUrl.Contains("/users/sign_in"))
        {
            Log.Information("GitLab login page reached, entering credentials for '{Username}'...", username);
            var usernameField = browser.Page.Locator("#user_login");
            var passwordField = browser.Page.Locator("#user_password");
            var signInButton =
                browser.Page.Locator("input[type='submit'][name='commit'], button[type='submit']");

            await BrowserService.FillFormField(usernameField, username, "username");
            await BrowserService.FillFormField(passwordField, TestConfig.TestPassword, "password");
            await signInButton.First.ClickAsync();
            await browser.Page.WaitForURLAsync(
                url => url.Contains("/oauth/authorize") || url.Contains("localhost:5000"),
                new PageWaitForURLOptions { Timeout = 30000 });

            currentUrl = browser.Page.Url;
            Log.Information("URL after sign-in: {Url}", currentUrl);
        }

        if (currentUrl.Contains("/oauth/authorize"))
        {
            Log.Information("OAuth authorization page, submitting...");
            await browser.Page.EvaluateAsync(
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
                await browser.Page.WaitForURLAsync(
                    url => !url.Contains("/oauth/authorize"),
                    new PageWaitForURLOptions { Timeout = 15000 });
            }
            catch
            {
                Log.Warning("OAuth authorize didn't redirect. URL: {Url}", browser.Page.Url);
            }
        }
    }

    private static async Task WaitForDashboardReady(BrowserService browser)
    {
        var ready = await DashboardWaitHelper.WaitForDashboardReady(browser.Page);
        if (!ready)
        {
            throw new InvalidOperationException("Dashboard did not fully load within 120s timeout");
        }
    }
}
