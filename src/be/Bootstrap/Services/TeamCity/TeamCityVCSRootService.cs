using System.Net;
using System.Text.Json;
using RestSharp;
using Serilog;

namespace Bootstrap.Services.TeamCity;

public class TeamCityVCSRootService
{
    private readonly RestClient _client;

    private readonly TeamCityVersionedSettingsService _versionedSettingsService;

    public TeamCityVCSRootService(
        RestClient client,
        TeamCityVersionedSettingsService versionedSettingsService)
    {
        _client = client;
        _versionedSettingsService = versionedSettingsService;
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
        var currentSettings = await _versionedSettingsService.GetVersionedSettings("_Root");
        var versionedSettingsEnabled = currentSettings != null
                                       && currentSettings.Contains("\"synchronizationMode\":\"enabled\"");

        if (versionedSettingsEnabled)
        {
            Log.Information(
                "Temporarily disabling versioned settings to allow VCS root modification...");

            await _versionedSettingsService.DisableVersionedSettings();
            await Task.Delay(2000);
        }

        Log.Information($"Creating/updating VCS root '{vcsRootName}' for URL: {gitUrl}");

        // Check if VCS root already exists
        var (existingVCSRootId, existingUrl) = await GetVCSRootByName(vcsRootName);
        if (existingVCSRootId != null)
        {
            Log.Information($"VCS root '{vcsRootName}' already exists with ID: {existingVCSRootId}");
            Log.Debug($"Existing URL: '{existingUrl}', Requested URL: '{gitUrl}'");

            if (existingUrl == null)
            {
                Log.Error(
                    $"Could not retrieve URL for existing VCS root '{vcsRootName}' (ID: {existingVCSRootId}). Aborting.");

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

        await UpdateVCSRootProperty(vcsRootId, "url", gitUrl);

        if (username != null)
        {
            await UpdateVCSRootProperty(vcsRootId, "username", username);
        }

        if (password != null)
        {
            await UpdateVCSRootProperty(vcsRootId, "secure:password", password);
        }
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
                    var url = await GetVCSRootURL(id!);
                    return (id, url);
                }
            }
        }
        catch (JsonException ex)
        {
            Log.Error($"Failed to parse VCS root response: {ex.Message}");
            throw new InvalidOperationException(
                $"Failed to parse VCS root response for '{vcsRootName}': {ex.Message}",
                ex);
        }

        return (null, null);
    }

    /// <summary>
    ///     Gets the URL of a VCS root.
    /// </summary>
    private async Task<string?> GetVCSRootURL(string vcsRootId)
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
    private async Task UpdateVCSRootProperty(string vcsRootId, string propertyName, string value)
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

        return result;
    }
}