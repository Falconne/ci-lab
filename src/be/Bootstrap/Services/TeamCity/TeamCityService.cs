using RestSharp;
using RestSharp.Authenticators;
using Serilog;
using System.Net;
using System.Text.Json;

namespace Bootstrap.Services.TeamCity;

public class TeamCityService : IDisposable
{
    private readonly RestClient _client;
    private readonly string _teamcityURL;

    public TeamCityService(string teamcityURL, string username, string password)
    {
        _teamcityURL = teamcityURL.TrimEnd('/');

        Log.Debug($"Initializing TeamCityService with URL: {_teamcityURL}, user: {username}");

        _client = new RestClient(
            new RestClientOptions($"{_teamcityURL}/app/rest")
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
    }

    /// <summary>
    ///     Creates a user in TeamCity. Returns true if created, false if already exists.
    /// </summary>
    public async Task<bool> CreateUser(string username, string name, string email, string password)
    {
        Log.Information($"Creating TeamCity user '{username}'...");

        // Check if user already exists
        var checkRequest = new RestRequest($"users/username:{username}");
        var checkResponse = await _client.ExecuteGetAsync(checkRequest);

        if (checkResponse.StatusCode == HttpStatusCode.OK)
        {
            Log.Information($"User '{username}' already exists in TeamCity");
            return false;
        }

        // Create the user
        var createRequest = new RestRequest("users", Method.Post)
            .AddJsonBody(new
            {
                username = username,
                name = name,
                email = email,
                password = password
            });

        var createResponse = await _client.ExecuteAsync(createRequest);

        if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            Log.Information($"User '{username}' created successfully in TeamCity");
            return true;
        }

        Log.Error($"Failed to create TeamCity user '{username}': {(int)createResponse.StatusCode} - {createResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to create TeamCity user '{username}': {(int)createResponse.StatusCode} - {createResponse.Content}");
    }

}
