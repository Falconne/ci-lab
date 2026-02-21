using Serilog;
using System.Net.Http.Json;

namespace IntegrationTest.Tests;

/// <summary>
/// Verifies that version endpoints are accessible and the UI includes required elements.
/// This is a lightweight test that doesn't require full browser authentication.
/// </summary>
public class VersionAndLastUpdatedTest : IDisposable
{
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public async Task Run()
    {
        Log.Information("Testing version endpoints and Last Updated column...");

        // Test backend version endpoint
        using var httpClient = new HttpClient();
        var backendVersionResponse = await httpClient.GetAsync($"{TestConfig.MergicianUrl}/api/version");

        if (!backendVersionResponse.IsSuccessStatusCode)
        {
            throw new Exception($"Backend version endpoint returned status: {backendVersionResponse.StatusCode}");
        }

        var backendVersionData = await backendVersionResponse.Content.ReadFromJsonAsync<VersionResponse>();
        if (backendVersionData == null || string.IsNullOrWhiteSpace(backendVersionData.Version))
        {
            throw new Exception("Backend version endpoint returned empty or null version");
        }

        Log.Information($"Backend version: {backendVersionData.Version}");

        // Test frontend version.json
        var frontendVersionResponse = await httpClient.GetAsync($"{TestConfig.MergicianUrl}/version.json");

        if (!frontendVersionResponse.IsSuccessStatusCode)
        {
            throw new Exception($"Frontend version.json returned status: {frontendVersionResponse.StatusCode}");
        }

        var frontendVersionData = await frontendVersionResponse.Content.ReadFromJsonAsync<VersionResponse>();
        if (frontendVersionData == null || string.IsNullOrWhiteSpace(frontendVersionData.Version))
        {
            throw new Exception("Frontend version.json returned empty or null version");
        }

        Log.Information($"Frontend version: {frontendVersionData.Version}");

        // Verify the HomeView.vue source includes Last Updated column
        // (This is a code verification rather than runtime since the column is conditionally rendered based on data)
        var homeViewPath = Path.Combine(TestConfig.RepositoryRoot, "src", "fe", "src", "views", "HomeView.vue");
        if (!File.Exists(homeViewPath))
        {
            throw new Exception($"HomeView.vue not found at: {homeViewPath}");
        }

        var homeViewContent = await File.ReadAllTextAsync(homeViewPath);
        if (!homeViewContent.Contains("lastUpdated") || !homeViewContent.Contains("formatTimeAgo"))
        {
            throw new Exception("HomeView.vue does not contain lastUpdated field and formatTimeAgo rendering");
        }

        Log.Information("HomeView.vue confirmed to include last-updated time display");

        // Verify AppBar.vue includes version display
        var appBarPath = Path.Combine(TestConfig.RepositoryRoot, "src", "fe", "src", "components", "AppBar.vue");
        if (!File.Exists(appBarPath))
        {
            throw new Exception($"AppBar.vue not found at: {appBarPath}");
        }

        var appBarContent = await File.ReadAllTextAsync(appBarPath);
        if (!appBarContent.Contains("fe:") || !appBarContent.Contains("be:"))
        {
            throw new Exception("AppBar.vue does not contain version display with fe: and be: labels");
        }

        if (!appBarContent.Contains("page-title"))
        {
            throw new Exception("AppBar.vue does not contain the page title zone");
        }

        Log.Information("AppBar.vue confirmed to include version display and page title zone");

        Log.Information("Version and Last Updated test passed");
    }

    private record VersionResponse(string Version);
}

