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
        Log.Information("Testing version endpoints, dashboard card layout markers, and app bar title zone...");

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

        // Verify HomeView.vue source includes card layout markers and last updated text marker.
        var homeViewPath = Path.Combine(TestConfig.RepositoryRoot, "src", "fe", "src", "views", "HomeView.vue");
        if (!File.Exists(homeViewPath))
        {
            throw new Exception($"HomeView.vue not found at: {homeViewPath}");
        }

        var homeViewContent = await File.ReadAllTextAsync(homeViewPath);
        if (!homeViewContent.Contains("merge-group-card") || !homeViewContent.Contains("repo-last-updated"))
        {
            throw new Exception("HomeView.vue does not contain expected dashboard card layout markers");
        }

        Log.Information("HomeView.vue confirmed to include dashboard card and last-updated markers");

        // Verify AppBar.vue includes version display and route-driven page title zone.
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

        if (!appBarContent.Contains("page-title-text") || !appBarContent.Contains("title-divider"))
        {
            throw new Exception("AppBar.vue does not contain the expected page title zone and divider");
        }

        Log.Information("AppBar.vue confirmed to include version display and page title zone");

        Log.Information("Version and dashboard UI source checks passed");
    }

    private record VersionResponse(string Version);
}

