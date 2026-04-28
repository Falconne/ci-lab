using System.Text.Json;
using IntegrationTest;
using IntegrationTest.Services;
using IntegrationTest.Tests;
using PlaywrightService;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(TestConfig.LogDir, "integration-test.log"),
        rollingInterval: RollingInterval.Infinite,
        fileSizeLimitBytes: 5 * 1024 * 1024)
    .CreateLogger();

Log.Information("============================================================");
Log.Information("Mergician Integration Tests");
Log.Information("============================================================");

var allPassed = true;
var results = new List<(string Name, bool Passed, string? Error)>();

// Abort on first failure to speed up debugging during development.
// Change this to false to run all tests regardless of failures.
var abortOnFirstFailure = true;

async Task RunTest(string name, Func<Task> testFn)
{
    Log.Information("");
    Log.Information("--- Test: {Name} ---", name);
    try
    {
        await testFn();
        results.Add((name, true, null));
        Log.Information("PASS: {Name}", name);
    }
    catch (Exception ex)
    {
        results.Add((name, false, ex.Message));
        Log.Error("FAIL: {Name} - {Error}", name, ex.Message);
        allPassed = false;
        if (abortOnFirstFailure)
            throw;
    }
}

using var browser = new BrowserService();

try
{
    // Wait for Mergician to be fully started and ready before running tests
    {
        var healthUrl = $"{TestConfig.MergicianUrl}/api/health";
        var timeout = TimeSpan.FromMinutes(5);
        var pollInterval = TimeSpan.FromSeconds(3);
        var deadline = DateTime.UtcNow + timeout;
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        Log.Information("");
        Log.Information(
            "--- Waiting for Mergician to be ready at {Url} (timeout: {Timeout}) ---",
            healthUrl,
            timeout);

        var ready = false;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await httpClient.GetAsync(healthUrl);
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var isReady = doc.RootElement.TryGetProperty("isReady", out var prop) && prop.GetBoolean();
                if (isReady)
                {
                    Log.Information("Mergician is ready");
                    ready = true;
                    break;
                }

                var message = doc.RootElement.TryGetProperty("message", out var msgProp)
                    ? msgProp.GetString()
                    : "unknown";

                Log.Debug("Mergician not yet ready (message: {Message}), retrying...", message);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                Log.Debug("Startup check failed ({Message}), retrying...", ex.Message);
            }

            await Task.Delay(pollInterval);
        }

        if (!ready)
        {
            throw new TimeoutException(
                $"Mergician did not become ready at {healthUrl} within {timeout.TotalMinutes} minutes");
        }

        results.Add(("StartupCheck", true, null));
        Log.Information("PASS: StartupCheck");
    }

    await browser.Initialize(TestConfig.ScreenshotDir);

    // Test 1: Authentication and logout via GitLab OAuth
    await RunTest("Auth and Logout", () => new AuthAndLogoutTest(browser).Run());

    // Test 2: Dashboard data verification
    await RunTest("Dashboard", () => new DashboardTest(browser).Run());

    // Test 3: Dashboard live updates (new branches and MR status changes)
    await RunTest("Dashboard Live Updates", () => new DashboardLiveUpdateTest(browser).Run());

    // Test 4: Version display and Last Updated column
    await RunTest("Version and Last Updated", () => new VersionAndLastUpdatedTest().Run());

    // Test 5: Auto merge toggle and dashboard badge
    await RunTest("Auto Merge Toggle", () => new AutoMergeToggleTest(browser).Run());

    // Test 6: Auto merge behavior (pipeline blocking, partial readiness, rebase, merge)
    await RunTest("Auto Merge Behavior", () => new AutoMergeBehaviorTest(browser).Run());

    // Test 7: Merge group management (subscribe/unsubscribe, add MR, find by MR)
    await RunTest("Merge Group Management", () => new MergeGroupManagementTest(browser).Run());

    // Test 8: Manual GitLab CI job filtering (manual jobs must not appear as job chips)
    await RunTest("Manual Job Filter", () => new ManualJobFilterTest(browser).Run());
}
catch (Exception ex)
{
    Log.Fatal(ex, "Test run aborted");
    allPassed = false;
}

// Summary
Log.Information("");
Log.Information("============================================================");
Log.Information("Test Results:");
foreach (var (name, passed, error) in results)
{
    var status = passed ? "PASS" : "FAIL";
    var detail = error != null ? $" - {error}" : "";
    Log.Information($"  [{status}] {name}{detail}");
}

Log.Information("============================================================");
Log.Information(allPassed ? "All tests passed!" : "Some tests failed.");
Log.Information("============================================================");

Log.CloseAndFlush();
return allPassed ? 0 : 1;