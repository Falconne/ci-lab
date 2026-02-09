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
    }
    finally
    {
        authTest.Dispose();
    }

    // Test 2: Git operations and activity
    var activityTest = new ActivityTest();
    try
    {
        Log.Information("");
        Log.Information("--- Test: Git Activity ---");
        await activityTest.Run();
        results.Add(("Git Activity", true, null));
        Log.Information("PASS: Git Activity");
    }
    catch (Exception ex)
    {
        results.Add(("Git Activity", false, ex.Message));
        Log.Error($"FAIL: Git Activity - {ex.Message}");
        allPassed = false;
    }
    finally
    {
        activityTest.Dispose();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unexpected error during integration tests");
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
