using RestSharp;
using Serilog;
using System.Net;

namespace Bootstrap.Services.TeamCity;

public class TeamCityVersionedSettingsService
{
    private readonly RestClient _client;

    public TeamCityVersionedSettingsService(RestClient client)
    {
        _client = client;
    }

    /// <summary>
    ///     Enables versioned settings (config under source control) for the Root project.
    /// </summary>
    public async Task EnableVersionedSettings(
        string vcsRootId,
        string settingsFormat = "kotlin",
        bool allowUiEditing = false,
        bool forceReconfigure = false)
    {
        Log.Information($"Enabling versioned settings for _Root project using VCS root: {vcsRootId}");

        // Check if versioned settings status has errors
        var statusHasErrors = await VersionedSettingsHasErrors("_Root");
        if (statusHasErrors)
        {
            Log.Information("Versioned settings status shows errors, will reconfigure");
            forceReconfigure = true;
        }

        // Check current versioned settings status
        var currentSettings = await GetVersionedSettings("_Root");
        if (!forceReconfigure
            && currentSettings != null
            && currentSettings.Contains("\"synchronizationMode\":\"enabled\""))
        {
            // Check if it's using the correct VCS root
            if (currentSettings.Contains($"\"vcsRootId\":\"{vcsRootId}\""))
            {
                Log.Information("Versioned settings already enabled for _Root project with correct VCS root");
                return;
            }

            Log.Information("Versioned settings enabled but with different VCS root, will reconfigure");
        }

        // If we're reconfiguring, disable first to reset state
        if (forceReconfigure
            && currentSettings != null
            && currentSettings.Contains("\"synchronizationMode\":\"enabled\""))
        {
            Log.Information("Disabling versioned settings to reset state before reconfiguring...");
            await DisableVersionedSettings();
            await Task.Delay(3000); // Wait for TeamCity to process the change
        }

        // TeamCity requires specific payload structure for versioned settings
        // Use "overrideInVCS" to export current settings to VCS (overwrite what's in VCS)
        var versionedSettingsPayload = new
        {
            synchronizationMode = "enabled",
            vcsRootId,
            format = settingsFormat,
            allowUIEditing = allowUiEditing,
            storeSecureValuesOutsideVcs = true,
            showSettingsChanges = true,
            importDecision = "overrideInVCS", // Export current settings to VCS, overwriting existing
            buildSettingsMode = "useFromVCS" // Use settings from VCS for builds
        };

        var request = new RestRequest("projects/_Root/versionedSettings/config", Method.Put)
            .AddJsonBody(versionedSettingsPayload);

        var response = await _client.ExecuteAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.Created)
        {
            Log.Information("Versioned settings enabled successfully for _Root project");
            return;
        }

        // If PUT fails, try POST
        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            Log.Information("PUT failed, trying POST method...");
            var postRequest = new RestRequest("projects/_Root/versionedSettings/config", Method.Post)
                .AddJsonBody(versionedSettingsPayload);

            var postResponse = await _client.ExecuteAsync(postRequest);

            if (postResponse.StatusCode is HttpStatusCode.OK
                or HttpStatusCode.NoContent
                or HttpStatusCode.Created)
            {
                Log.Information("Versioned settings enabled successfully via POST");
                return;
            }

            Log.Error(
                $"Failed to enable versioned settings via POST: {(int)postResponse.StatusCode} - {postResponse.Content}");

            throw new InvalidOperationException(
                $"Failed to enable versioned settings: {(int)postResponse.StatusCode} - {postResponse.Content}");
        }

        Log.Error($"Failed to enable versioned settings: {(int)response.StatusCode} - {response.Content}");
        throw new InvalidOperationException(
            $"Failed to enable versioned settings: {(int)response.StatusCode} - {response.Content}");
    }

    /// <summary>
    ///     Disables versioned settings for a project.
    /// </summary>
    public async Task DisableVersionedSettings(string projectId = "_Root")
    {
        Log.Information($"Disabling versioned settings for project: {projectId}");

        var disablePayload = new { synchronizationMode = "disabled" };

        var request = new RestRequest($"projects/{projectId}/versionedSettings/config", Method.Put)
            .AddJsonBody(disablePayload);

        var response = await _client.ExecuteAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.Created)
        {
            Log.Information("Versioned settings disabled successfully");
            return;
        }

        Log.Error($"Failed to disable versioned settings: {(int)response.StatusCode} - {response.Content}");
        throw new InvalidOperationException(
            $"Failed to disable versioned settings for project '{projectId}': {(int)response.StatusCode} - {response.Content}");
    }

    /// <summary>
    ///     Gets the current versioned settings configuration for a project.
    ///     Returns null if no versioned settings are configured (404).
    /// </summary>
    public async Task<string?> GetVersionedSettings(string projectId)
    {
        var request = new RestRequest($"projects/{projectId}/versionedSettings/config");

        var response = await _client.ExecuteGetAsync(request);

        if (response.IsSuccessful)
        {
            return response.Content;
        }

        // 404 means no versioned settings configured, which is expected for new projects
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        Log.Error($"Failed to get versioned settings: {(int)response.StatusCode} - {response.Content}");
        throw new InvalidOperationException(
            $"Failed to get versioned settings for project '{projectId}': {(int)response.StatusCode} - {response.Content}");
    }

    /// <summary>
    ///     Checks if the versioned settings status has errors.
    /// </summary>
    private async Task<bool> VersionedSettingsHasErrors(string projectId)
    {
        var request = new RestRequest($"projects/{projectId}/versionedSettings/status");

        var response = await _client.ExecuteGetAsync(request);

        if (response is { IsSuccessful: true, Content: not null })
        {
            // If status contains "message" with "Failed", there's an error
            if (response.Content.Contains("Failed")
                || response.Content.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug($"Versioned settings status: {response.Content}");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Triggers TeamCity to commit current settings to the VCS.
    /// </summary>
    public async Task CommitCurrentSettingsToVCS(string projectId = "_Root")
    {
        Log.Information($"Triggering commit of current settings to VCS for project: {projectId}");

        // The commit is done via a specific action endpoint
        var request = new RestRequest(
            $"projects/{projectId}/versionedSettings/commitCurrentSettings",
            Method.Post);

        var response = await _client.ExecuteAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.Accepted)
        {
            Log.Information("Settings commit triggered successfully");
            return;
        }

        // 409 Conflict might mean there's nothing new to commit
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            Log.Information("Settings commit returned conflict - settings may already be committed");
            return;
        }

        // Unexpected response: fail fast so bootstrap aborts and the issue can be diagnosed
        Log.Error($"Failed to trigger settings commit: {(int)response.StatusCode} - {response.Content}");
        throw new InvalidOperationException(
            $"Failed to commit current settings to VCS for project '{projectId}': {(int)response.StatusCode} - {response.Content}");
    }

    /// <summary>
    ///     Waits for the settings.kts file to appear in the GitLab repository.
    ///     Throws an exception if the file does not appear within the timeout.
    /// </summary>
    public static async Task WaitForSettingsInRepo(
        string gitlabURL,
        string gitlabToken,
        int projectId,
        int timeoutSeconds = 120)
    {
        Log.Information($"Waiting for Kotlin settings file to appear in GitLab project {projectId}...");

        using var gitlabClient = new RestClient(
            new RestClientOptions($"{gitlabURL.TrimEnd('/')}/api/v4")
            {
                ThrowOnAnyError = false,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                Timeout = TimeSpan.FromSeconds(30)
            });

        gitlabClient.AddDefaultHeader("PRIVATE-TOKEN", gitlabToken);

        var startTime = DateTime.UtcNow;
        var checkPaths = new[] { ".teamcity/settings.kts", ".teamcity" };

        while ((DateTime.UtcNow - startTime).TotalSeconds < timeoutSeconds)
        {
            foreach (var path in checkPaths)
            {
                var request = new RestRequest($"projects/{projectId}/repository/tree")
                    .AddQueryParameter("path", path);

                var response = await gitlabClient.ExecuteGetAsync(request);

                if (response.IsSuccessful
                    && !string.IsNullOrEmpty(response.Content)
                    && response.Content != "[]")
                {
                    Log.Information($"Found TeamCity settings at path: {path}");
                    Log.Debug($"Repository tree response: {response.Content}");
                    return;
                }
            }

            Log.Debug("Settings not found yet, waiting 5 seconds...");
            await Task.Delay(5000);
        }

        var msg = $"Timeout waiting for settings.kts to appear after {timeoutSeconds} seconds";
        Log.Error(msg);
        throw new InvalidOperationException(msg);
    }

    /// <summary>
    ///     Checks for Kotlin DSL compilation errors in versioned settings.
    ///     Throws an exception if errors are found.
    /// </summary>
    public async Task CheckForKotlinErrors(string projectId = "_Root")
    {
        Log.Information($"Checking for Kotlin DSL compilation errors in project {projectId}...");

        var request = new RestRequest($"projects/{projectId}/versionedSettings/status");
        var response = await _client.ExecuteGetAsync(request);

        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"Could not check versioned settings status: {(int)response.StatusCode}");
        }

        if (response.Content == null)
        {
            return;
        }

        // Check for compilation errors or warnings
        if (response.Content.Contains("Compilation error", StringComparison.OrdinalIgnoreCase)
            || response.Content.Contains("Failed to apply changes", StringComparison.OrdinalIgnoreCase))
        {
            Log.Error($"Kotlin DSL compilation errors detected in project {projectId}");
            Log.Error($"Status response: {response.Content}");
            throw new InvalidOperationException(
                $"Kotlin DSL has compilation errors in project {projectId}. Check TeamCity logs for details.");
        }

        Log.Information("No Kotlin DSL compilation errors detected");
    }
}