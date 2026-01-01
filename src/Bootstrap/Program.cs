using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bootstrap.Services.Utilities;
using Bootstrap.Services.TeamCity;
using Bootstrap.Services.GitLab;

Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine("CI Lab Bootstrap - Manual Setup (.NET 9)");
Console.WriteLine("=".PadRight(60, '='));

// Determine .env file path relative to the project directory
var envPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", ".env");
var envFullPath = Path.GetFullPath(envPath);

// Load environment variables from .env file if it exists
EnvHelper.LoadEnvFile(envFullPath);

var gitlabUrl = Environment.GetEnvironmentVariable("GITLAB_URL") ?? "http://localhost:8081";
var teamcityUrl = Environment.GetEnvironmentVariable("TEAMCITY_URL") ?? "http://localhost:8111";
Log($"GitLab URL:   {gitlabUrl}");
Log($"TeamCity URL: {teamcityUrl}");

using var httpClient = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    UseCookies = false
})
{
    Timeout = TimeSpan.FromSeconds(10)
};

// Create service instances
var teamCityService = new TeamCityService();
var gitLabService = new GitLabService();

// Wait for TeamCity first (it's often available before GitLab)
Log("Waiting for TeamCity to become available...");
var teamcityReady = await HttpHelper.WaitForServiceAsync(httpClient, teamcityUrl, TimeSpan.FromMinutes(5));
if (!teamcityReady)
{
    LogError("TeamCity did not become available; exiting");
    return 1;
}

// Automated TeamCity initial setup (Playwright driven)
Log("=".PadRight(60, '='));
Log("TeamCity Automated Initial Setup");
Log("=".PadRight(60, '='));

var gitlabRootPassword = Environment.GetEnvironmentVariable("GITLAB_ROOT_PASSWORD") ?? "changeme123";
var teamcitySetupSuccess = await teamCityService.AutomateTeamCitySetupAsync(httpClient, teamcityUrl, "root", gitlabRootPassword);

if (!teamcitySetupSuccess)
{
    LogError("TeamCity automated setup failed");
    LogError("Check screenshots in data/screenshots/ directory for details");
    return 1;
}

Log("✓ TeamCity initial setup completed");

// Ensure TEAMCITY_TOKEN is present and valid; if missing or invalid, attempt API creation
var existingTeamcityToken = Environment.GetEnvironmentVariable("TEAMCITY_TOKEN");
var needCreateToken = string.IsNullOrEmpty(existingTeamcityToken);
if (!needCreateToken)
{
    Log("Validating existing TEAMCITY_TOKEN...");
    try
    {
        var valid = await teamCityService.ValidateTeamCityTokenAsync(httpClient, teamcityUrl, existingTeamcityToken);
        if (!valid)
        {
            Log("Existing TEAMCITY_TOKEN is invalid or insufficient permissions; will attempt to create a new token via API");
            needCreateToken = true;
        }
        else
        {
            Log("Existing TEAMCITY_TOKEN is valid");
        }
    }
    catch (Exception ex)
    {
        LogWarning($"Error validating existing TEAMCITY_TOKEN: {ex.Message}");
        needCreateToken = true;
    }
}

if (needCreateToken)
{
    Log("Attempting to create TeamCity token via REST API...");
    try
    {
        var createdToken = await teamCityService.TryCreateTokenViaApiAsync(httpClient, teamcityUrl, "root", gitlabRootPassword, "bootstrap-automation");
        if (!string.IsNullOrEmpty(createdToken))
        {
            EnvHelper.SaveOrUpdateEnvFile(envFullPath, "TEAMCITY_TOKEN", createdToken);
            Log("✓ TeamCity token created via API and saved to .env");
        }
        else
        {
            Log("Could not create TeamCity token via API; falling back to UI/interactive flow");
        }
    }
    catch (Exception ex)
    {
        LogWarning($"TeamCity API token creation failed: {ex.Message}");
    }
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
    teamCityService.ValidateTeamCityTokenAsync);

if (string.IsNullOrEmpty(teamcityToken))
{
    LogError("Failed to obtain valid TeamCity token; exiting");
    return 1;
}

// Ensure GitLab is available before attempting token operations
Log("Waiting for GitLab to become available...");
var gitlabReady = await HttpHelper.WaitForServiceAsync(httpClient, gitlabUrl, TimeSpan.FromMinutes(5));
if (!gitlabReady)
{
    LogError("GitLab did not become available; exiting");
    return 1;
}

var gitlabToken = await GetAndValidateTokenAsync(
    httpClient,
    "GitLab",
    gitlabUrl,
    "GITLAB_TOKEN",
    envFullPath,
    gitLabService.ValidateGitLabTokenAsync);

if (string.IsNullOrEmpty(gitlabToken))
{
    LogError("Failed to obtain valid GitLab token; exiting");
    return 1;
}

// Create GitLab projects
if (!string.IsNullOrEmpty(gitlabToken))
{
    Log("=".PadRight(60, '='));
    Log("Setting up GitLab test projects...");

    var projectsCreated = 0;
    for (var i = 1; i <= 5; i++)
    {
        var projectName = $"test-project-{i}";
        var created = await gitLabService.CreateAndPopulateGitLabProjectAsync(httpClient, gitlabUrl, gitlabToken, projectName, i);
        if (created)
        {
            projectsCreated++;
        }
    }

    Log($"✓ {projectsCreated} GitLab test project(s) ready");
}

// Create TeamCity projects
if (!string.IsNullOrEmpty(teamcityToken))
{
    Log("=".PadRight(60, '='));
    Log("Setting up TeamCity...");
    var success = await teamCityService.CreateProjectAsync(httpClient, teamcityUrl, teamcityToken);
    if (success)
    {
        Log("✓ TeamCity project created");
    }

    // Authorize agents
    Log("Authorizing TeamCity agents...");
    var agentsAuthorized = await teamCityService.AuthorizeAgentsAsync(httpClient, teamcityUrl, teamcityToken);
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
            EnvHelper.SaveOrUpdateEnvFile(envFilePath, envVarName, token);
            return token;
        }

        LogError($"✗ {serviceName} token is invalid or insufficient permissions");
        token = null; // Force re-prompt
    }
}

static void Log(string message) => Console.WriteLine($"[bootstrap] {message}");
static void LogError(string message) => Console.Error.WriteLine($"[bootstrap] ERROR: {message}");
static void LogWarning(string message) => Console.WriteLine($"[bootstrap] WARNING: {message}");
