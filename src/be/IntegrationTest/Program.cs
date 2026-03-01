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
    // Wait for Mergician to be healthy before running tests
    {
        var healthUrl = $"{TestConfig.MergicianUrl}/api/health";
        var timeout = TimeSpan.FromMinutes(5);
        var pollInterval = TimeSpan.FromSeconds(3);
        var deadline = DateTime.UtcNow + timeout;
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        Log.Information("");
        Log.Information(
            "--- Waiting for Mergician to be healthy at {Url} (timeout: {Timeout}) ---",
            healthUrl,
            timeout);

        var healthy = false;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await httpClient.GetAsync(healthUrl);
                if (response.IsSuccessStatusCode)
                {
                    Log.Information("Mergician is healthy (HTTP {StatusCode})", (int)response.StatusCode);
                    healthy = true;
                    break;
                }

                Log.Debug("Health check returned HTTP {StatusCode}, retrying...", (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Log.Debug("Health check failed ({Message}), retrying...", ex.Message);
            }

            await Task.Delay(pollInterval);
        }

        if (!healthy)
        {
            throw new TimeoutException(
                $"Mergician did not become healthy at {healthUrl} within {timeout.TotalMinutes} minutes");
        }

        results.Add(("HealthCheck", true, null));
        Log.Information("PASS: HealthCheck");
    }

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
        if (abortOnFirstFailure)
        {
            throw;
        }
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
        if (abortOnFirstFailure)
        {
            throw;
        }
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
        if (abortOnFirstFailure)
        {
            throw;
        }
    }
    finally
    {
        dashboardTest.Dispose();
    }

    // Test 4: Dashboard live updates (new branches and MR status changes)
    var liveUpdateTest = new DashboardLiveUpdateTest();
    try
    {
        Log.Information("");
        Log.Information("--- Test: Dashboard Live Updates ---");
        await liveUpdateTest.Run();
        results.Add(("Dashboard Live Updates", true, null));
        Log.Information("PASS: Dashboard Live Updates");
    }
    catch (Exception ex)
    {
        results.Add(("Dashboard Live Updates", false, ex.Message));
        Log.Error($"FAIL: Dashboard Live Updates - {ex.Message}");
        allPassed = false;
        if (abortOnFirstFailure)
        {
            throw;
        }
    }
    finally
    {
        liveUpdateTest.Dispose();
    }

    // Test 5: Version display and Last Updated column
    var versionTest = new VersionAndLastUpdatedTest();
    try
    {
        Log.Information("");
        Log.Information("--- Test: Version and Last Updated ---");
        await versionTest.Run();
        results.Add(("Version and Last Updated", true, null));
        Log.Information("PASS: Version and Last Updated");
    }
    catch (Exception ex)
    {
        results.Add(("Version and Last Updated", false, ex.Message));
        Log.Error($"FAIL: Version and Last Updated - {ex.Message}");
        allPassed = false;
        if (abortOnFirstFailure)
        {
            throw;
        }
    }
    finally
    {
        versionTest.Dispose();
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
