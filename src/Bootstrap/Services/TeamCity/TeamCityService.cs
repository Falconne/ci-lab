using RestSharp;
using RestSharp.Authenticators;
using Serilog;
using System.Net;
using System.Text.Json;

namespace Bootstrap.Services.TeamCity;

public class TeamCityService : IDisposable
{
    private readonly RestClient _client;

    private readonly string _password;

    private readonly string _teamcityUrl;

    private readonly string _username;

    public TeamCityService(string teamcityUrl, string username, string password)
    {
        _teamcityUrl = teamcityUrl.TrimEnd('/');
        _username = username;
        _password = password;

        Log.Debug($"Initializing TeamCityService with URL: {_teamcityUrl}, user: {username}");

        _client = new RestClient(
            new RestClientOptions($"{_teamcityUrl}/app/rest")
            {
                ThrowOnAnyError = false,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                Timeout = TimeSpan.FromSeconds(30),
                Authenticator = new HttpBasicAuthenticator(username, password)
            });

        _client.AddDefaultHeader("Accept", "application/json");
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Creates or updates a VCS root at the _Root project level.
    ///     Returns the VCS root ID and a flag indicating if it was updated.
    /// </summary>
    public async Task<(string vcsRootId, bool wasUpdated)> CreateOrUpdateVCSRootViaAPI(
        string vcsRootName,
        string gitUrl,
        string branch,
        string? username = null,
        string? password = null)
    {
        Log.Information($"Creating/updating VCS root '{vcsRootName}' for URL: {gitUrl}");

        // Check if VCS root already exists
        var (existingVCSRootId, existingUrl) = await GetVCSRootByName(vcsRootName);
        if (existingVCSRootId != null)
        {
            Log.Information($"VCS root '{vcsRootName}' already exists with ID: {existingVCSRootId}");
            Log.Debug($"Existing URL: '{existingUrl}', Requested URL: '{gitUrl}'");

            if (existingUrl == null)
            {
                Log.Error($"Could not retrieve URL for existing VCS root '{vcsRootName}' (ID: {existingVCSRootId}). Aborting.");
                throw new InvalidOperationException(
                    $"Could not retrieve URL for existing VCS root '{vcsRootName}' (ID: {existingVCSRootId}).");
            }

            // Check if the URL needs to be updated
            if (existingUrl != gitUrl)
            {
                await UpdateExistingVCSRoot(existingVCSRootId, gitUrl, username, password);
                return (existingVCSRootId, true);
            }

            Log.Debug("VCS root URL is already correct");
            return (existingVCSRootId, false); // No update needed
        }

        // Create a unique ID from the name
        var vcsRootId = $"Root_{vcsRootName.Replace(" ", "_").Replace("-", "_")}";

        var vcsRootPayload = new
        {
            id = vcsRootId,
            name = vcsRootName,
            vcsName = "jetbrains.git",
            project = new { id = "_Root" },
            properties = new
            {
                property = new object[]
                {
                    new { name = "url", value = gitUrl },
                    new { name = "branch", value = $"refs/heads/{branch}" },
                    new
                    {
                        name = "authMethod",
                        value = username != null ? "PASSWORD" : "ANONYMOUS"
                    },
                    new { name = "username", value = username ?? "" },
                    new { name = "secure:password", value = password ?? "" },
                    new { name = "usernameStyle", value = "USERID" },
                    new { name = "submoduleCheckout", value = "CHECKOUT" },
                    new { name = "userForTags", value = "" },
                    new { name = "ignoreKnownHosts", value = "true" }
                }
            }
        };

        var request = new RestRequest("vcs-roots", Method.Post)
            .AddJsonBody(vcsRootPayload);

        var response = await _client.ExecuteAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            Log.Information($"VCS root '{vcsRootName}' created successfully with ID: {vcsRootId}");
            return (vcsRootId, true);
        }

        Log.Error($"Failed to create VCS root: {(int)response.StatusCode} - {response.Content}");
        throw new InvalidOperationException(
            $"Failed to create VCS root '{vcsRootName}': {(int)response.StatusCode} - {response.Content}");
    }

    /// <summary>
    ///     Updates an existing VCS root's URL and credentials.
    /// </summary>
    private async Task UpdateExistingVCSRoot(
        string vcsRootId,
        string gitUrl,
        string? username,
        string? password)
    {
        Log.Information($"Existing VCS root has different URL, updating to: {gitUrl}");

        var currentSettings = await GetVersionedSettings("_Root");
        var wasVersionedSettingsEnabled = currentSettings != null
                         && currentSettings.Contains("\"synchronizationMode\":\"enabled\"");
        string? originalVCSRootId = null;

        if (wasVersionedSettingsEnabled)
        {
            Log.Information(
                "Temporarily disabling versioned settings to allow VCS root modification...");

            originalVCSRootId = ExtractVCSRootIdFromSettings(currentSettings!);
            await DisableVersionedSettings();
            await Task.Delay(2000);
        }

        await UpdateVCSRootProperty(vcsRootId, "url", gitUrl);

        if (username != null)
        {
            await UpdateVCSRootProperty(vcsRootId, "username", username);
        }

        if (password != null)
        {
            await UpdateVCSRootProperty(vcsRootId, "secure:password", password);
        }

        if (wasVersionedSettingsEnabled && originalVCSRootId != null)
        {
            await ReEnableVersionedSettings(originalVCSRootId);
        }
    }

    /// <summary>
    ///     Re-enables versioned settings that were temporarily disabled.
    /// </summary>
    private async Task ReEnableVersionedSettings(string vcsRootId)
    {
        Log.Information("Re-enabling versioned settings after VCS root modification...");
        
        var versionedSettingsPayload = new
        {
            synchronizationMode = "enabled",
            vcsRootId,
            format = "kotlin",
            allowUIEditing = false,
            storeSecureValuesOutsideVcs = true,
            showSettingsChanges = true,
            importDecision = "overrideInVCS",
            buildSettingsMode = "useFromVCS"
        };

        var request = new RestRequest("projects/_Root/versionedSettings/config", Method.Put)
            .AddJsonBody(versionedSettingsPayload);

        var response = await _client.ExecuteAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.Created)
        {
            Log.Information("Versioned settings re-enabled successfully");
            await Task.Delay(2000);
            return;
        }

        Log.Error($"Failed to re-enable versioned settings: {(int)response.StatusCode} - {response.Content}");
        throw new InvalidOperationException(
            $"Failed to re-enable versioned settings: {(int)response.StatusCode} - {response.Content}");
    }

    /// <summary>
    ///     Extracts the VCS root ID from versioned settings JSON.
    /// </summary>
    private string? ExtractVCSRootIdFromSettings(string settingsJson)
    {
        try
        {
            var json = JsonDocument.Parse(settingsJson);
            if (json.RootElement.TryGetProperty("vcsRootId", out var vcsRootIdElement))
            {
                return vcsRootIdElement.GetString();
            }
        }
        catch (JsonException ex)
        {
            Log.Warning($"Failed to parse VCS root ID from settings: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    ///     Gets a VCS root by name and returns its ID and URL.
    /// </summary>
    private async Task<(string? id, string? url)> GetVCSRootByName(string vcsRootName)
    {
        var request = new RestRequest("vcs-roots")
            .AddQueryParameter("locator", $"name:{vcsRootName}");

        var response = await _client.ExecuteGetAsync(request);

        if (!response.IsSuccessful)
        {
            return (null, null);
        }

        // Parse JSON response to check for existing VCS root
        try
        {
            var json = JsonDocument.Parse(response.Content ?? "{}");
            var count = json.RootElement.TryGetProperty("count", out var countElement)
                ? countElement.GetInt32()
                : 0;

            if (count > 0 && json.RootElement.TryGetProperty("vcs-root", out var vcsRoots))
            {
                var firstRoot = vcsRoots.EnumerateArray().FirstOrDefault();
                if (firstRoot.TryGetProperty("id", out var idElement))
                {
                    var id = idElement.GetString();
                    // Get the URL by fetching the full VCS root details
                    var url = await GetVCSRootUrl(id!);
                    return (id, url);
                }
            }
        }
        catch (JsonException ex)
        {
            Log.Error($"Failed to parse VCS root response: {ex.Message}");
            throw new InvalidOperationException($"Failed to parse VCS root response for '{vcsRootName}': {ex.Message}", ex);
        }

        return (null, null);
    }

    /// <summary>
    ///     Gets the URL of a VCS root.
    /// </summary>
    private async Task<string?> GetVCSRootUrl(string vcsRootId)
    {
        var request = new RestRequest($"vcs-roots/id:{vcsRootId}/properties/url")
            .AddHeader("Accept", "text/plain");

        var response = await _client.ExecuteGetAsync(request);

        if (response.IsSuccessful)
        {
            // Response is just the value text
            return response.Content?.Trim().Trim('"');
        }

        Log.Debug($"Failed to get VCS root URL: {(int)response.StatusCode} - {response.Content}");
        return null;
    }

    /// <summary>
    ///     Updates a VCS root property.
    /// </summary>
    public async Task UpdateVCSRootProperty(string vcsRootId, string propertyName, string value)
    {
        Log.Information($"Updating VCS root '{vcsRootId}' property '{propertyName}'");

        var request = new RestRequest($"vcs-roots/id:{vcsRootId}/properties/{propertyName}", Method.Put)
            .AddHeader("Accept", "text/plain")
            .AddHeader("Content-Type", "text/plain")
            .AddStringBody(value, ContentType.Plain);

        var response = await _client.ExecuteAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
        {
            Log.Information($"VCS root property '{propertyName}' updated successfully");
            return;
        }

        Log.Error($"Failed to update VCS root property: {(int)response.StatusCode} - {response.Content}");
        throw new InvalidOperationException(
            $"Failed to update VCS root property '{propertyName}': {(int)response.StatusCode} - {response.Content}");
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

            if (postResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent
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
    private async Task<string?> GetVersionedSettings(string projectId)
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

        if (response.IsSuccessful && response.Content != null)
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
    public async Task WaitForSettingsInRepo(
        string gitlabUrl,
        string gitlabToken,
        int projectId,
        int timeoutSeconds = 120)
    {
        Log.Information($"Waiting for Kotlin settings file to appear in GitLab project {projectId}...");

        using var gitlabClient = new RestClient(
            new RestClientOptions($"{gitlabUrl.TrimEnd('/')}/api/v4")
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
            Log.Warning($"Could not check versioned settings status: {(int)response.StatusCode}");
            return;
        }

        if (response.Content == null)
        {
            return;
        }

        // Check for compilation errors or warnings
        if (response.Content.Contains("Compilation error", StringComparison.OrdinalIgnoreCase) ||
            response.Content.Contains("Failed to apply changes", StringComparison.OrdinalIgnoreCase))
        {
            Log.Error($"Kotlin DSL compilation errors detected in project {projectId}");
            Log.Error($"Status response: {response.Content}");
            throw new InvalidOperationException($"Kotlin DSL has compilation errors in project {projectId}. Check TeamCity logs for details.");
        }

        Log.Information("No Kotlin DSL compilation errors detected");
    }

    /// <summary>
    ///     Gets a project by its ID. Returns null if not found.
    /// </summary>
    public async Task<JsonElement?> GetProject(string projectId)
    {
        var request = new RestRequest($"projects/id:{projectId}");

        var response = await _client.ExecuteGetAsync(request);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessful)
        {
            Log.Error($"Failed to get project: {(int)response.StatusCode} - {response.Content}");
            throw new InvalidOperationException(
                $"Failed to get project '{projectId}': {(int)response.StatusCode} - {response.Content}");
        }

        return JsonDocument.Parse(response.Content ?? "{}").RootElement;
    }

    /// <summary>
    ///     Gets all build types (build configurations) for a project.
    /// </summary>
    public async Task<List<(string id, string name)>> GetBuildTypes(string projectId)
    {
        var request = new RestRequest($"projects/id:{projectId}/buildTypes");

        var response = await _client.ExecuteGetAsync(request);

        if (!response.IsSuccessful)
        {
            Log.Error($"Failed to get build types: {(int)response.StatusCode} - {response.Content}");
            throw new InvalidOperationException(
                $"Failed to get build types for project '{projectId}': {(int)response.StatusCode} - {response.Content}");
        }

        var result = new List<(string id, string name)>();
        try
        {
            var json = JsonDocument.Parse(response.Content ?? "{}");
            if (json.RootElement.TryGetProperty("buildType", out var buildTypes))
            {
                foreach (var bt in buildTypes.EnumerateArray())
                {
                    var id = bt.GetProperty("id").GetString() ?? "";
                    var name = bt.GetProperty("name").GetString() ?? "";
                    result.Add((id, name));
                }
            }
        }
        catch (JsonException ex)
        {
            Log.Error($"Failed to parse build types response: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    ///     Gets all VCS roots for a project.
    /// </summary>
    public async Task<List<(string id, string name)>> GetVCSRoots(string projectId)
    {
        var request = new RestRequest("vcs-roots")
            .AddQueryParameter("locator", $"project:(id:{projectId})");

        var response = await _client.ExecuteGetAsync(request);

        if (!response.IsSuccessful)
        {
            Log.Error($"Failed to get VCS roots: {(int)response.StatusCode} - {response.Content}");
            throw new InvalidOperationException(
                $"Failed to get VCS roots for project '{projectId}': {(int)response.StatusCode} - {response.Content}");
        }

        var result = new List<(string id, string name)>();
        try
        {
            var json = JsonDocument.Parse(response.Content ?? "{}");
            if (json.RootElement.TryGetProperty("vcs-root", out var vcsRoots))
            {
                foreach (var vr in vcsRoots.EnumerateArray())
                {
                    var id = vr.GetProperty("id").GetString() ?? "";
                    var name = vr.GetProperty("name").GetString() ?? "";
                    result.Add((id, name));
                }
            }
        }
        catch (JsonException ex)
        {
            Log.Error($"Failed to parse VCS roots response: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    ///     Triggers a build for a specific build type.
    /// </summary>
    public async Task<string> TriggerBuild(string buildTypeId)
    {
        Log.Information($"Triggering build for build type: {buildTypeId}");

        var payload = new { buildType = new { id = buildTypeId } };

        var request = new RestRequest("buildQueue", Method.Post)
            .AddJsonBody(payload);

        var response = await _client.ExecuteAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            try
            {
                var json = JsonDocument.Parse(response.Content ?? "{}");
                var buildId = json.RootElement.GetProperty("id").GetInt32().ToString();
                Log.Information($"Build triggered successfully, build ID: {buildId}");
                return buildId;
            }
            catch (JsonException ex)
            {
                Log.Error($"Failed to parse build trigger response: {ex.Message}");
                throw new InvalidOperationException($"Failed to parse build trigger response: {ex.Message}");
            }
        }

        Log.Error($"Failed to trigger build: {(int)response.StatusCode} - {response.Content}");
        throw new InvalidOperationException(
            $"Failed to trigger build for '{buildTypeId}': {(int)response.StatusCode} - {response.Content}");
    }

    /// <summary>
    ///     Gets the status of a build by its ID.
    /// </summary>
    public async Task<(string state, string status)> GetBuildStatus(string buildId)
    {
        var request = new RestRequest($"builds/id:{buildId}");

        var response = await _client.ExecuteGetAsync(request);

        if (!response.IsSuccessful)
        {
            Log.Error($"Failed to get build status: {(int)response.StatusCode} - {response.Content}");
            throw new InvalidOperationException(
                $"Failed to get build status for '{buildId}': {(int)response.StatusCode} - {response.Content}");
        }

        try
        {
            var json = JsonDocument.Parse(response.Content ?? "{}");
            var state = json.RootElement.TryGetProperty("state", out var stateElem)
                ? stateElem.GetString() ?? "unknown"
                : "unknown";
            var status = json.RootElement.TryGetProperty("status", out var statusElem)
                ? statusElem.GetString() ?? "unknown"
                : "unknown";
            return (state, status);
        }
        catch (JsonException ex)
        {
            Log.Error($"Failed to parse build status response: {ex.Message}");
            throw new InvalidOperationException($"Failed to parse build status response: {ex.Message}");
        }
    }

    /// <summary>
    ///     Waits for a build to complete and returns whether it succeeded.
    /// </summary>
    public async Task<bool> WaitForBuildCompletion(string buildId, int timeoutSeconds = 300)
    {
        Log.Information($"Waiting for build {buildId} to complete...");

        var startTime = DateTime.UtcNow;
        while ((DateTime.UtcNow - startTime).TotalSeconds < timeoutSeconds)
        {
            var (state, status) = await GetBuildStatus(buildId);

            if (state == "finished")
            {
                Log.Information($"Build {buildId} finished with status: {status}");
                return status == "SUCCESS";
            }

            Log.Debug($"Build {buildId} state: {state}, status: {status}");
            await Task.Delay(5000);
        }

        Log.Error($"Timeout waiting for build {buildId} to complete after {timeoutSeconds} seconds");
        return false;
    }

    /// <summary>
    ///     Polls until a project appears in TeamCity (after Kotlin DSL change).
    /// </summary>
    public async Task<bool> WaitForProject(string projectId, int timeoutSeconds = 120)
    {
        Log.Information($"Waiting for project '{projectId}' to appear in TeamCity...");

        var startTime = DateTime.UtcNow;
        while ((DateTime.UtcNow - startTime).TotalSeconds < timeoutSeconds)
        {
            var project = await GetProject(projectId);
            if (project.HasValue)
            {
                Log.Information($"Project '{projectId}' found in TeamCity");
                return true;
            }

            Log.Debug($"Project '{projectId}' not found yet, waiting 5 seconds...");
            await Task.Delay(5000);
        }

        Log.Error($"Timeout waiting for project '{projectId}' to appear after {timeoutSeconds} seconds");
        return false;
    }}