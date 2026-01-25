using Bootstrap.Services.Utilities;
using Microsoft.Playwright;
using Serilog;

namespace Bootstrap.Services;

public class PlaywrightService : IDisposable
{
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private bool _disposed;
    private IPage? _page;
    private IPlaywright? _playwright;
    private int _screenshotCounter;
    private string _screenshotDir = string.Empty;

    public IPage Page => _page
                         ?? throw new InvalidOperationException(
                             "Browser not initialized. Call InitializeAsync first.");

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async Task<bool> Initialize(string screenshotDirectory, bool headless = true)
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
            Log.Information($"Screenshot directory created: {_screenshotDir}");

            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
            {
                Log.Error("Playwright browser installation returned non-zero exit code");
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
            Log.Error($"Failed to initialize browser: {ex.Message}");
            return false;
        }
    }

    public async Task Navigate(string url, WaitUntilState waitUntil = WaitUntilState.NetworkIdle)
    {
        Log.Information($"Navigating to {url}");
        await Page.GotoAsync(url, new PageGotoOptions { WaitUntil = waitUntil });
    }

    public async Task<string> GetPageContent()
    {
        return await Page.ContentAsync();
    }

    public async Task TakeScreenshot(string description)
    {
        try
        {
            _screenshotCounter++;
            var timestamp = DateTime.UtcNow.ToString("HHmmss");
            var filename = $"{_screenshotCounter:D3}_{timestamp}_{description}.png";
            var path = Path.Combine(_screenshotDir, filename);
            await Page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            Log.Information($"Screenshot saved: {filename}");
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not save screenshot: {ex.Message}");
        }
    }

    public async Task<bool> WaitForTextToDisappear(
        string text,
        int timeoutSeconds = 120,
        int pollMs = 1000)
    {
        for (var s = 0; s < timeoutSeconds; s++)
        {
            try
            {
                var pageContent = await Page.ContentAsync();
                if (!pageContent.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Text has gone, continuing on...");
                    return true;
                }

                if (s % 5 == 0)
                {
                    Log.Information($"Waiting for '{text}' to disappear... waited {s}s");
                }

                await Task.Delay(pollMs);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    $"Warning reading page content while waiting for '{text}': {ex.Message}");

                await Task.Delay(pollMs);
            }
        }

        Log.Information("Timed out waiting");
        return false;
    }

    public static async Task<bool> FillFormField(ILocator locator, string value, string fieldName)
    {
        if (await locator.CountWithRetry() > 0)
        {
            Log.Information($"Filling {fieldName}...");
            await locator.First.FillAsync(value);
            return true;
        }

        Log.Warning($"{fieldName} field not found");
        return false;
    }

    public async Task<bool> ClickAndWait(
        ILocator locator,
        string buttonName,
        int delayMs = 2000)
    {
        if (!await ClickButton(locator, buttonName))
        {
            return false;
        }

        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(delayMs);
        return true;
    }

    public static async Task<bool> CheckCheckbox(ILocator locator, string checkboxName)
    {
        if (await locator.CountWithRetry() > 0)
        {
            Log.Information($"Checking {checkboxName}...");
            await locator.First.CheckAsync();
            return true;
        }

        Log.Warning($"{checkboxName} checkbox not found");
        return false;
    }

    public static async Task<string?> GetTextContent(ILocator locator)
    {
        if (await locator.CountWithRetry() > 0)
        {
            return await locator.First.TextContentAsync();
        }

        return null;
    }

    public static async Task<string?> GetAttribute(ILocator locator, string attributeName)
    {
        if (await locator.CountWithRetry() > 0)
        {
            return await locator.First.GetAttributeAsync(attributeName);
        }

        return null;
    }

    public static async Task<bool> WaitForElement(
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

    private static async Task<bool> ClickButton(ILocator locator, string buttonName)
    {
        if (await locator.CountWithRetry() > 0)
        {
            Log.Information($"Clicking {buttonName}...");
            await locator.First.ClickAsync();
            return true;
        }

        Log.Warning($"{buttonName} button not found");
        return false;
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
