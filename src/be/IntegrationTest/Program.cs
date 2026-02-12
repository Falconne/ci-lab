using IntegrationTest;
using IntegrationTest.Tests;
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

try
{
    // Test 1: Authentication via GitLab OAuth
    var authTest = new AuthenticationTest();
    try
    {
        Log.Information("");
        Log.Information("--- Test: Authentication ---");
        await authTest.Run();
        results.Add(("Authentication", true, null));
        Log.Information("PASS: Authentication");
    }
    catch (Exception ex)
    {
        results.Add(("Authentication", false, ex.Message));
        Log.Error($"FAIL: Authentication - {ex.Message}");
        allPassed = false;
        if (abortOnFirstFailure) throw;
    }
    finally
    {
        authTest.Dispose();
    }

    // Test 2: Logout clears session and shows welcome page
    var logoutTest = new LogoutTest();
    try
    {
        Log.Information("");
        Log.Information("--- Test: Logout ---");
        await logoutTest.Run();
        results.Add(("Logout", true, null));
        Log.Information("PASS: Logout");
    }
    catch (Exception ex)
    {
        results.Add(("Logout", false, ex.Message));
        Log.Error($"FAIL: Logout - {ex.Message}");
        allPassed = false;
        if (abortOnFirstFailure) throw;
    }
    finally
    {
        logoutTest.Dispose();
    }

    // Test 3: Dashboard data verification
    var dashboardTest = new DashboardTest();
    try
    {
        Log.Information("");
        Log.Information("--- Test: Dashboard ---");
        await dashboardTest.Run();
        results.Add(("Dashboard", true, null));
        Log.Information("PASS: Dashboard");
    }
    catch (Exception ex)
    {
        results.Add(("Dashboard", false, ex.Message));
        Log.Error($"FAIL: Dashboard - {ex.Message}");
        allPassed = false;
        if (abortOnFirstFailure) throw;
    }
    finally
    {
        dashboardTest.Dispose();
    }
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
