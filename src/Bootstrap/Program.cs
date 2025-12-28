using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNetEnv;

// Automated bootstrap script for the CI lab
// Waits for GitLab and TeamCity, auto-generates tokens, and creates sample projects

Env.Load("/.env");
Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine("CI Lab Bootstrap - Automated Setup (.NET 9)");
Console.WriteLine("=".PadRight(60, '='));

var gitlabUrl = Environment.GetEnvironmentVariable("GITLAB_URL") ?? "http://gitlab";
var teamcityUrl = Environment.GetEnvironmentVariable("TEAMCITY_URL") ?? "http://teamcity:8111";
var gitlabRootPassword = Environment.GetEnvironmentVariable("GITLAB_ROOT_PASSWORD");

using var httpClient = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
})
{
    Timeout = TimeSpan.FromSeconds(10)
};

// Wait for services
Log("Waiting for services to become available...");
var gitlabReady = await WaitForServiceAsync(httpClient, gitlabUrl, TimeSpan.FromMinutes(10));
var teamcityReady = await WaitForServiceAsync(httpClient, teamcityUrl, TimeSpan.FromMinutes(5));

if (!gitlabReady || !teamcityReady)
{
    LogError("One or more services did not become available; exiting");
    return 1;
}

// Wait for TeamCity API to be fully operational
Log("Waiting for TeamCity API to become operational...");
var teamcityApiReady = await WaitForTeamCityApiAsync(httpClient, teamcityUrl, TimeSpan.FromMinutes(5));
if (!teamcityApiReady)
{
    LogWarning("TeamCity API did not become available, agent authorization may fail");
}

// Auto-generate or retrieve tokens
var gitlabToken = Environment.GetEnvironmentVariable("GITLAB_TOKEN");
var teamcityToken = Environment.GetEnvironmentVariable("TEAMCITY_TOKEN");

if (string.IsNullOrEmpty(gitlabToken) && !string.IsNullOrEmpty(gitlabRootPassword))
{
    Log("No GITLAB_TOKEN provided; attempting auto-generation...");
    gitlabToken = await GetGitLabTokenAsync(httpClient, gitlabUrl, gitlabRootPassword);
}

if (string.IsNullOrEmpty(teamcityToken))
{
    Log("No TEAMCITY_TOKEN provided; attempting auto-detection...");
    teamcityToken = await GetTeamCityTokenAsync(httpClient, teamcityUrl);
}

// Create GitLab projects
if (!string.IsNullOrEmpty(gitlabToken))
{
    Log("=".PadRight(60, '='));
    Log("Setting up GitLab...");
    var project = await CreateGitLabProjectAsync(httpClient, gitlabUrl, gitlabToken, "sample-repo");
    if (project is not null)
    {
        var url = project.GetProperty("web_url").GetString() ??
                  project.GetProperty("http_url_to_repo").GetString() ??
                  "Created";
        Log($"✓ GitLab project ready: {url}");
    }
}
else
{
    LogWarning("⚠ GitLab setup skipped - no token available");
    Log("   Manual setup: http://localhost:8081/-/profile/personal_access_tokens");
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
else
{
    LogWarning("⚠ TeamCity setup skipped - no token available");
    Log("   Manual setup: http://localhost:8111");
}

Log("=".PadRight(60, '='));
Log("Bootstrap complete!");
Log("=".PadRight(60, '='));
Log("Services available at:");
Log("  GitLab:   http://localhost:8081 (root / <your-password>)");
Log("  TeamCity: http://localhost:8111");
Log("=".PadRight(60, '='));

return 0;

// Helper methods

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

static async Task<string?> GetGitLabTokenAsync(HttpClient client, string gitlabUrl, string password)
{
    Log("Attempting to create GitLab Personal Access Token for user root");

    try
    {
        var apiUrl = $"{gitlabUrl.TrimEnd('/')}/api/v4/user/personal_access_tokens";
        var tokenData = new
        {
            name = "bootstrap-automation",
            scopes = new[] { "api", "read_api", "write_repository" }
        };

        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"root:{password}"));
        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = JsonContent.Create(tokenData),
            Headers = { Authorization = new AuthenticationHeaderValue("Basic", credentials) }
        };

        var response = await client.SendAsync(request);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            var token = result.GetProperty("token").GetString();
            Log("GitLab Personal Access Token created successfully");
            return token;
        }

        // Fallback: try OAuth
        var oauthUrl = $"{gitlabUrl.TrimEnd('/')}/oauth/token";
        var oauthData = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = "root",
            ["password"] = password
        };

        var oauthResponse = await client.PostAsync(oauthUrl, new FormUrlEncodedContent(oauthData));
        if (oauthResponse.IsSuccessStatusCode)
        {
            var oauthResult = await oauthResponse.Content.ReadFromJsonAsync<JsonElement>();
            var accessToken = oauthResult.GetProperty("access_token").GetString();
            Log("GitLab OAuth token obtained");
            return accessToken;
        }

        LogError($"Failed to create GitLab token: {(int)response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
        Log("You may need to manually create a Personal Access Token in GitLab UI and set GITLAB_TOKEN");
        return null;
    }
    catch (Exception ex)
    {
        LogError($"Exception creating GitLab token: {ex.Message}");
        Log("Fallback: You can manually create a token at http://localhost:8081/-/profile/personal_access_tokens");
        return null;
    }
}

static async Task<string?> GetTeamCityTokenAsync(HttpClient client, string teamcityUrl)
{
    Log("Attempting to obtain TeamCity authentication token");

    try
    {
        var infoUrl = $"{teamcityUrl.TrimEnd('/')}/app/rest/server";
        var response = await client.GetAsync(infoUrl);

        if (response.IsSuccessStatusCode)
        {
            Log("TeamCity accessible without authentication (initial setup mode)");
            return "SUPERUSER_AUTH_NOT_NEEDED";
        }

        // Try guest auth
        var guestUrl = $"{teamcityUrl.TrimEnd('/')}/guestAuth/app/rest/server";
        var guestResponse = await client.GetAsync(guestUrl);

        if (guestResponse.IsSuccessStatusCode)
        {
            Log("TeamCity guest access available");
            return "GUEST_AUTH";
        }

        Log("TeamCity token extraction not implemented for this setup");
        Log("You may need to manually configure TeamCity and set TEAMCITY_TOKEN");
        return null;
    }
    catch (Exception ex)
    {
        LogError($"Exception getting TeamCity token: {ex.Message}");
        return null;
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

static async Task<bool> WaitForTeamCityApiAsync(HttpClient client, string teamcityUrl, TimeSpan timeout)
{
    var apiUrl = $"{teamcityUrl.TrimEnd('/')}/app/rest/server";
    var startTime = DateTime.UtcNow;
    var interval = TimeSpan.FromSeconds(5);

    while (true)
    {
        try
        {
            var response = await client.GetAsync(apiUrl);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Log("TeamCity API is operational");
                return true;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var elapsed = DateTime.UtcNow - startTime;
            if (elapsed > timeout)
            {
                LogError($"Timeout waiting for TeamCity API: {ex.Message}");
                return false;
            }

            if ((int)elapsed.TotalSeconds % 30 == 0)
            {
                Log($"Still waiting for TeamCity API... ({(int)elapsed.TotalSeconds}s elapsed)");
            }

            await Task.Delay(interval);
        }
    }
}

static async Task<bool> CreateTeamCityProjectAsync(HttpClient client, string teamcityUrl, string token)
{
    var apiUrl = token switch
    {
        "GUEST_AUTH" => $"{teamcityUrl.TrimEnd('/')}/guestAuth/app/rest/projects",
        _ => $"{teamcityUrl.TrimEnd('/')}/app/rest/projects"
    };

    Log($"Creating TeamCity project 'Sample Project' via {apiUrl}");

    try
    {
        var xml = """<newProjectDescription name="Sample Project" id="SampleProject" />""";
        var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
            Headers = { Accept = { MediaTypeWithQualityHeaderValue.Parse("application/json") } }
        };

        if (token is not "GUEST_AUTH" and not "SUPERUSER_AUTH_NOT_NEEDED")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

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
    var apiUrl = token switch
    {
        "GUEST_AUTH" => $"{teamcityUrl.TrimEnd('/')}/guestAuth/app/rest/agents",
        _ => $"{teamcityUrl.TrimEnd('/')}/app/rest/agents"
    };

    try
    {
        // Get list of unauthorized agents
        var listRequest = new HttpRequestMessage(HttpMethod.Get, $"{apiUrl}?locator=authorized:false");
        listRequest.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json"));

        if (token is not "GUEST_AUTH" and not "SUPERUSER_AUTH_NOT_NEEDED")
        {
            listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

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
                Content = new StringContent("true", Encoding.UTF8, "text/plain")
            };

            if (token is not "GUEST_AUTH" and not "SUPERUSER_AUTH_NOT_NEEDED")
            {
                authRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var authResponse = await client.SendAsync(authRequest);
            if (authResponse.IsSuccessStatusCode)
            {
                Log($"  ✓ Agent {agentName} authorized");
                authorizedCount++;

                // Add to default pool
                var poolRequest = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/id:{agentId}/pool")
                {
                    Content = new StringContent("""<agentPool id="0" />""", Encoding.UTF8, "application/xml")
                };

                if (token is not "GUEST_AUTH" and not "SUPERUSER_AUTH_NOT_NEEDED")
                {
                    poolRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

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
