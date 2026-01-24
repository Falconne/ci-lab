using Microsoft.Playwright;

namespace Bootstrap.Services.Utilities;

public class PlaywrightService : IDisposable
{
    private IBrowser? _browser;

    private IBrowserContext? _context;

    private bool _disposed;

    private IPage? _page;

    private IPlaywright? _playwright;

    private int _screenshotCounter;

    private string _screenshotDir = string.Empty;

    public IPage Page
    {
        get
        {
            if (_page == null)
            {
                throw new InvalidOperationException("Browser not initialized. Call InitializeAsync first.");
            }

            return _page;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async Task<bool> InitializeAsync(string screenshotDirectory, bool headless = true)
    {
        try
        {
            _screenshotDir = screenshotDirectory;
            _screenshotCounter = 0;

            if (Directory.Exists(_screenshotDir))
            {
                Directory.Delete(_screenshotDir, true);
            }

            Directory.CreateDirectory(_screenshotDir);
            Logging.Log($"Screenshot directory created: {_screenshotDir}");

            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
            {
                Logging.LogError("Playwright browser installation returned non-zero exit code");
                return false;
            }

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = headless, Timeout = 60000 });

            _context = await _browser.NewContextAsync(
                new BrowserNewContextOptions { IgnoreHTTPSErrors = true });

            _page = await _context.NewPageAsync();
            _page.SetDefaultTimeout(60000);

            return true;
        }
        catch (Exception ex)
        {
            Logging.LogError($"Failed to initialize browser: {ex.Message}");
            return false;
        }
    }

    public async Task NavigateAsync(string url, WaitUntilState waitUntil = WaitUntilState.NetworkIdle)
    {
        Logging.Log($"Navigating to {url}");
        await Page.GotoAsync(url, new PageGotoOptions { WaitUntil = waitUntil });
    }

    public async Task<string> GetPageContentAsync()
    {
        return await Page.ContentAsync();
    }

    public async Task TakeScreenshotAsync(string description)
    {
        try
        {
            _screenshotCounter++;
            var timestamp = DateTime.UtcNow.ToString("HHmmss");
            var filename = $"{_screenshotCounter:D3}_{timestamp}_{description}.png";
            var path = Path.Combine(_screenshotDir, filename);
            await Page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            Logging.LogInfo($"Screenshot saved: {filename}", 1);
        }
        catch (Exception ex)
        {
            Logging.LogWarning($"Could not save screenshot: {ex.Message}", 1);
        }
    }

    public async Task<bool> WaitForTextToDisappearAsync(
        string text,
        int timeoutSeconds = 120,
        int pollMs = 1000)
    {
        var found = false;
        for (var s = 0; s < timeoutSeconds; s++)
        {
            try
            {
                var pageContent = await Page.ContentAsync();
                if (pageContent.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = true;
                    if (s % 5 == 0)
                    {
                        Logging.LogInfo($"Waiting for '{text}' to disappear... waited {s}s", 1);
                    }

                    await Task.Delay(pollMs);
                    continue;
                }

                break;
            }
            catch (Exception ex)
            {
                Logging.LogWarning(
                    $"Warning reading page content while waiting for '{text}': {ex.Message}",
                    1);

                await Task.Delay(pollMs);
            }
        }

        if (found)
        {
            Logging.LogInfo($"'{text}' no longer present (or timed out waiting)", 1);
        }

        return !found;
    }

    public static async Task<bool> FillFormFieldAsync(ILocator locator, string value, string fieldName)
    {
        if (await locator.CountAsync() > 0)
        {
            Logging.LogInfo($"Filling {fieldName}...", 2);
            await locator.First.FillAsync(value);
            return true;
        }

        Logging.LogWarning($"{fieldName} field not found", 2);
        return false;
    }

    public static async Task<bool> ClickButtonAsync(ILocator locator, string buttonName)
    {
        if (await locator.CountAsync() > 0)
        {
            Logging.LogInfo($"Clicking {buttonName}...", 2);
            await locator.First.ClickAsync();
            return true;
        }

        Logging.LogWarning($"{buttonName} button not found", 2);
        return false;
    }

    public async Task<bool> ClickAndWaitAsync(
        ILocator locator,
        string buttonName,
        int delayMs = 2000)
    {
        if (await PlaywrightService.ClickButtonAsync(locator, buttonName))
        {
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(delayMs);
            return true;
        }

        return false;
    }

    public static async Task<bool> CheckCheckboxAsync(ILocator locator, string checkboxName)
    {
        if (await locator.CountAsync() > 0)
        {
            Logging.LogInfo($"Checking {checkboxName}...", 2);
            await locator.First.CheckAsync();
            return true;
        }

        Logging.LogWarning($"{checkboxName} checkbox not found", 2);
        return false;
    }

    public static async Task<string?> GetTextContentAsync(ILocator locator)
    {
        if (await locator.CountAsync() > 0)
        {
            return await locator.First.TextContentAsync();
        }

        return null;
    }

    public static async Task<string?> GetAttributeAsync(ILocator locator, string attributeName)
    {
        if (await locator.CountAsync() > 0)
        {
            return await locator.First.GetAttributeAsync(attributeName);
        }

        return null;
    }

    public static async Task<bool> WaitForElementAsync(
        ILocator locator,
        WaitForSelectorState state = WaitForSelectorState.Visible,
        int timeoutMs = 5000)
    {
        try
        {
            await locator.First.WaitForAsync(
                new LocatorWaitForOptions { State = state, Timeout = timeoutMs });

            return true;
        }
        catch
        {
            return false;
        }
    }

    public ILocator GetLocator(string selector)
    {
        return Page.Locator(selector);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _context?.DisposeAsync().AsTask().Wait();
            _browser?.DisposeAsync().AsTask().Wait();
            _playwright?.Dispose();
        }

        _disposed = true;
    }
}