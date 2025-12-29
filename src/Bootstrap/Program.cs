using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

// Manual bootstrap script for the CI lab
// Prompts for and validates GitLab and TeamCity tokens, then creates sample projects

Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine("CI Lab Bootstrap - Manual Setup (.NET 9)");
Console.WriteLine("=".PadRight(60, '='));

// Determine .env file path relative to the project directory
var envPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", ".env");
var envFullPath = Path.GetFullPath(envPath);

// Load environment variables from .env file if it exists
LoadEnvFile(envFullPath);

var gitlabUrl = Environment.GetEnvironmentVariable("GITLAB_URL") ?? "http://localhost:8081";
var teamcityUrl = Environment.GetEnvironmentVariable("TEAMCITY_URL") ?? "http://localhost:8111";

using var httpClient = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
})
{
    Timeout = TimeSpan.FromSeconds(10)
};

// Wait for services
Log("Waiting for services to become available...");
var gitlabReady = await WaitForServiceAsync(httpClient, gitlabUrl, TimeSpan.FromMinutes(5));
var teamcityReady = await WaitForServiceAsync(httpClient, teamcityUrl, TimeSpan.FromMinutes(5));

if (!gitlabReady || !teamcityReady)
{
    LogError("One or more services did not become available; exiting");
    return 1;
}

// Get and validate tokens
Log("=".PadRight(60, '='));
Log("Token Setup");
Log("=".PadRight(60, '='));

var teamcityToken = await GetAndValidateTokenAsync(
    httpClient,
    "TeamCity",
    teamcityUrl,
    "TEAMCITY_TOKEN",
    envFullPath,
    ValidateTeamCityTokenAsync);

if (string.IsNullOrEmpty(teamcityToken))
{
    LogError("Failed to obtain valid TeamCity token; exiting");
    return 1;
}

var gitlabToken = await GetAndValidateTokenAsync(
    httpClient,
    "GitLab",
    gitlabUrl,
    "GITLAB_TOKEN",
    envFullPath,
    ValidateGitLabTokenAsync);

if (string.IsNullOrEmpty(gitlabToken))
{
    LogError("Failed to obtain valid GitLab token; exiting");
    return 1;
}

// Create GitLab projects
if (!string.IsNullOrEmpty(gitlabToken))
{
    Log("=".PadRight(60, '='));
    Log("Setting up GitLab...");
    var project = await CreateGitLabProjectAsync(httpClient, gitlabUrl, gitlabToken, "sample-repo");
    if (project is not null)
    {
        var url = project.Value.GetProperty("web_url").GetString() ??
                  project.Value.GetProperty("http_url_to_repo").GetString() ??
                  "Created";
        Log($"✓ GitLab project ready: {url}");
    }
}

// Create TeamCity projects
if (!string.IsNullOrEmpty(teamcityToken))
{
    Log("=".PadRight(60, '='));
    Log("Setting up TeamCity...");
    var success = await CreateTeamCityProjectAsync(httpClient, teamcityUrl, teamcityToken);
    if (success)
    {
        Log("✓ TeamCity project created");
    }

    // Authorize agents
    Log("Authorizing TeamCity agents...");
    var agentsAuthorized = await AuthorizeTeamCityAgentsAsync(httpClient, teamcityUrl, teamcityToken);
    if (agentsAuthorized)
    {
        Log("✓ TeamCity agents authorized");
    }
    else
    {
        LogWarning("⚠ Agent authorization incomplete - some agents may need manual approval");
    }
}

Log("=".PadRight(60, '='));
Log("Bootstrap complete!");
Log("=".PadRight(60, '='));
Log("Services available at:");
Log($"  GitLab:   {gitlabUrl}");
Log($"  TeamCity: {teamcityUrl}");
Log("=".PadRight(60, '='));

return 0;

// Helper methods

static void LoadEnvFile(string envPath)
{
    if (!File.Exists(envPath))
    {
        Log($"No .env file found at {envPath}");
        return;
    }

    Log($"Loading environment from {envPath}");
    foreach (var line in File.ReadAllLines(envPath))
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            continue;

        var parts = trimmed.Split('=', 2);
        if (parts.Length == 2)
        {
            var key = parts[0].Trim();
            var value = parts[1].Trim().Trim('"');
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

static void SaveOrUpdateEnvFile(string envPath, string key, string value)
{
    var lines = File.Exists(envPath) ? File.ReadAllLines(envPath).ToList() : new List<string>();
    var keyPrefix = $"{key}=";
    var lineIndex = lines.FindIndex(l => l.Trim().StartsWith(keyPrefix));

    var newLine = $"{key}=\"{value}\"";

    if (lineIndex >= 0)
    {
        lines[lineIndex] = newLine;
        Log($"Updated {key} in .env file");
    }
    else
    {
        lines.Add(newLine);
        Log($"Added {key} to .env file");
    }

    File.WriteAllLines(envPath, lines);
    Environment.SetEnvironmentVariable(key, value);
}

static async Task<string?> GetAndValidateTokenAsync(
    HttpClient client,
    string serviceName,
    string serviceUrl,
    string envVarName,
    string envFilePath,
    Func<HttpClient, string, string, Task<bool>> validator)
{
    var token = Environment.GetEnvironmentVariable(envVarName);

    while (true)
    {
        if (string.IsNullOrEmpty(token))
        {
            Log($"\n{serviceName} token not found in environment or .env file");
            Log($"Please create a token in {serviceName}:");
            if (serviceName == "GitLab")
            {
                Log($"  1. Visit {serviceUrl}/-/profile/personal_access_tokens");
                Log("  2. Create a token with 'api', 'read_api', 'write_repository' scopes");
            }
            else if (serviceName == "TeamCity")
            {
                Log($"  1. Visit {serviceUrl}/profile.html?item=accessTokens");
                Log("  2. Create a token with appropriate permissions");
            }

            Console.Write($"Enter {serviceName} token: ");
            token = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(token))
            {
                LogError("Token cannot be empty");
                continue;
            }
        }

        Log($"Validating {serviceName} token...");
        var isValid = await validator(client, serviceUrl, token);

        if (isValid)
        {
            Log($"✓ {serviceName} token is valid");
            SaveOrUpdateEnvFile(envFilePath, envVarName, token);
            return token;
        }

        LogError($"✗ {serviceName} token is invalid or insufficient permissions");
        token = null; // Force re-prompt
    }
}

static async Task<bool> ValidateGitLabTokenAsync(HttpClient client, string gitlabUrl, string token)
{
    try
    {
        var apiUrl = $"{gitlabUrl.TrimEnd('/')}/api/v4/user";
        var request = new HttpRequestMessage(HttpMethod.Get, apiUrl)
        {
            Headers = { { "PRIVATE-TOKEN", token } }
        };

        var response = await client.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            var userData = await response.Content.ReadFromJsonAsync<JsonElement>();
            var username = userData.GetProperty("username").GetString();
            Log($"  Authenticated as: {username}");
            return true;
        }

        return false;
    }
    catch (Exception ex)
    {
        LogError($"  Validation error: {ex.Message}");
        return false;
    }
}

static async Task<bool> ValidateTeamCityTokenAsync(HttpClient client, string teamcityUrl, string token)
{
    try
    {
        var apiUrl = $"{teamcityUrl.TrimEnd('/')}/app/rest/server";
        var request = new HttpRequestMessage(HttpMethod.Get, apiUrl)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        };

        var response = await client.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            var serverData = await response.Content.ReadAsStringAsync();
            Log("  Token authentication successful");
            return true;
        }

        return false;
    }
    catch (Exception ex)
    {
        LogError($"  Validation error: {ex.Message}");
        return false;
    }
}

static async Task<bool> WaitForServiceAsync(HttpClient client, string url, TimeSpan timeout)
{
    Log($"Waiting for {url} (timeout {timeout.TotalSeconds}s)");
    var startTime = DateTime.UtcNow;
    var interval = TimeSpan.FromSeconds(10);

    while (true)
    {
        try
        {
            var response = await client.GetAsync(url);
            Log($"{url} responded: {(int)response.StatusCode}");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var elapsed = DateTime.UtcNow - startTime;
            if (elapsed > timeout)
            {
                LogError($"Timeout waiting for {url}: {ex.Message}");
                return false;
            }

            if ((int)elapsed.TotalSeconds % 30 == 0)
            {
                Log($"Still waiting for {url}... ({(int)elapsed.TotalSeconds}s elapsed)");
            }

            await Task.Delay(interval);
        }
    }
}

static async Task<JsonElement?> CreateGitLabProjectAsync(HttpClient client, string gitlabUrl, string token, string projectName)
{
    var apiUrl = $"{gitlabUrl.TrimEnd('/')}/api/v4/projects";
    Log($"Creating GitLab project '{projectName}' via {apiUrl}");

    try
    {
        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = JsonContent.Create(new { name = projectName, initialize_with_readme = true }),
            Headers = { { "PRIVATE-TOKEN", token } }
        };

        var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            Log("GitLab project created successfully");
            return JsonSerializer.Deserialize<JsonElement>(content);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            Log("GitLab project already exists");
            return string.IsNullOrEmpty(content) ? null : JsonSerializer.Deserialize<JsonElement>(content);
        }

        LogError($"GitLab API error {(int)response.StatusCode}: {content}");
        return null;
    }
    catch (Exception ex)
    {
        LogError($"Failed to call GitLab API: {ex.Message}");
        return null;
    }
}

static async Task<bool> CreateTeamCityProjectAsync(HttpClient client, string teamcityUrl, string token)
{
    var apiUrl = $"{teamcityUrl.TrimEnd('/')}/app/rest/projects";

    Log($"Creating TeamCity project 'Sample Project' via {apiUrl}");

    try
    {
        var xml = """<newProjectDescription name="Sample Project" id="SampleProject" />""";
        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
            Headers =
            {
                Accept = { MediaTypeWithQualityHeaderValue.Parse("application/json") },
                Authorization = new AuthenticationHeaderValue("Bearer", token)
            }
        };

        var response = await client.SendAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            Log("TeamCity project created successfully");
            return true;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            Log("TeamCity project already exists");
            return true;
        }

        LogError($"TeamCity API error {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return false;
    }
    catch (Exception ex)
    {
        LogError($"Failed to call TeamCity API: {ex.Message}");
        return false;
    }
}

static async Task<bool> AuthorizeTeamCityAgentsAsync(HttpClient client, string teamcityUrl, string token)
{
    var apiUrl = $"{teamcityUrl.TrimEnd('/')}/app/rest/agents";

    try
    {
        // Get list of unauthorized agents
        var listRequest = new HttpRequestMessage(HttpMethod.Get, $"{apiUrl}?locator=authorized:false")
        {
            Headers =
            {
                Accept = { MediaTypeWithQualityHeaderValue.Parse("application/json") },
                Authorization = new AuthenticationHeaderValue("Bearer", token)
            }
        };

        var listResponse = await client.SendAsync(listRequest);
        if (!listResponse.IsSuccessStatusCode)
        {
            LogError($"Failed to get agents list: {(int)listResponse.StatusCode}");
            return false;
        }

        var agentsData = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        if (!agentsData.TryGetProperty("agent", out var agents))
        {
            Log("No unauthorized agents found");
            return true;
        }

        var authorizedCount = 0;
        foreach (var agent in agents.EnumerateArray())
        {
            var agentId = agent.GetProperty("id").GetInt32();
            var agentName = agent.TryGetProperty("name", out var name) ? name.GetString() : $"agent-{agentId}";

            Log($"Authorizing agent: {agentName} (ID: {agentId})");

            // Authorize the agent
            var authRequest = new HttpRequestMessage(HttpMethod.Put, $"{apiUrl}/id:{agentId}/authorized")
            {
                Content = new StringContent("true", Encoding.UTF8, "text/plain"),
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
            };

            var authResponse = await client.SendAsync(authRequest);
            if (authResponse.IsSuccessStatusCode)
            {
                Log($"  ✓ Agent {agentName} authorized");
                authorizedCount++;

                // Add to default pool
                var poolRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/id:{agentId}/pool")
                {
                    Content = new StringContent("""<agentPool id="0" />""", Encoding.UTF8, "application/xml"),
                    Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
                };

                var poolResponse = await client.SendAsync(poolRequest);
                if (poolResponse.IsSuccessStatusCode)
                {
                    Log($"  ✓ Agent {agentName} added to default pool");
                }
                else
                {
                    LogWarning($"  Could not add agent {agentName} to pool: {(int)poolResponse.StatusCode}");
                }
            }
            else
            {
                LogWarning($"  Failed to authorize agent {agentName}: {(int)authResponse.StatusCode}");
            }
        }

        Log($"Authorized {authorizedCount} agent(s)");
        return authorizedCount > 0;
    }
    catch (Exception ex)
    {
        LogError($"Failed to authorize agents: {ex.Message}");
        return false;
    }
}

static void Log(string message) => Console.WriteLine($"[bootstrap] {message}");
static void LogError(string message) => Console.Error.WriteLine($"[bootstrap] ERROR: {message}");
static void LogWarning(string message) => Console.WriteLine($"[bootstrap] WARNING: {message}");
