using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bootstrap.Services.Utilities;
using Bootstrap.Services.TeamCity;
using Bootstrap.Services.GitLab;

LogHelper.LogSeparator();
LogHelper.Log("CI Lab Bootstrap - Manual Setup (.NET 9)");
LogHelper.LogSeparator();

// Determine .env file path relative to the project directory
var envPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", ".env");
var envFullPath = Path.GetFullPath(envPath);

// Load environment variables from .env file if it exists
EnvHelper.LoadEnvFile(envFullPath);

var gitlabUrl = Environment.GetEnvironmentVariable("GITLAB_URL") ?? "http://localhost:8081";
var teamcityUrl = Environment.GetEnvironmentVariable("TEAMCITY_URL") ?? "http://localhost:8111";
LogHelper.Log($"GitLab URL:   {gitlabUrl}");
LogHelper.Log($"TeamCity URL: {teamcityUrl}");

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
LogHelper.Log("Waiting for TeamCity to become available...");
var teamcityReady = await HttpHelper.WaitForServiceAsync(httpClient, teamcityUrl, TimeSpan.FromMinutes(5), allow503: true);
if (!teamcityReady)
{
    LogHelper.LogError("TeamCity did not become available; exiting");
    return 1;
}

// Automated TeamCity initial setup (Playwright driven)
LogHelper.LogSection("TeamCity Automated Initial Setup");

var gitlabRootPassword = Environment.GetEnvironmentVariable("GITLAB_ROOT_PASSWORD") ?? "changeme123";
var teamcitySetupSuccess = await teamCityService.AutomateTeamCitySetupAsync(httpClient, teamcityUrl, "root", gitlabRootPassword);

if (!teamcitySetupSuccess)
{
    LogHelper.LogError("TeamCity automated setup failed");
    LogHelper.LogError("Check screenshots in data/screenshots/ directory for details");
    return 1;
}

LogHelper.LogSuccess("TeamCity initial setup completed");

// Ensure TEAMCITY_TOKEN is present and valid; if missing or invalid, attempt API creation
var existingTeamcityToken = Environment.GetEnvironmentVariable("TEAMCITY_TOKEN");
var needCreateToken = string.IsNullOrEmpty(existingTeamcityToken);
if (!needCreateToken && existingTeamcityToken != null)
{
    LogHelper.Log("Validating existing TEAMCITY_TOKEN...");
    try
    {
        var valid = await teamCityService.ValidateTeamCityTokenAsync(httpClient, teamcityUrl, existingTeamcityToken);
        if (!valid)
        {
            LogHelper.Log("Existing TEAMCITY_TOKEN is invalid or insufficient permissions; will attempt to create a new token via API");
            needCreateToken = true;
        }
        else
        {
            LogHelper.Log("Existing TEAMCITY_TOKEN is valid");
        }
    }
    catch (Exception ex)
    {
        LogHelper.LogWarning($"Error validating existing TEAMCITY_TOKEN: {ex.Message}");
        needCreateToken = true;
    }
}

if (needCreateToken)
{
    LogHelper.Log("Attempting to create TeamCity token via REST API...");
    try
    {
        var createdToken = await teamCityService.TryCreateTokenViaApiAsync(httpClient, teamcityUrl, "root", gitlabRootPassword, "bootstrap-automation");
        if (!string.IsNullOrEmpty(createdToken))
        {
            EnvHelper.SaveOrUpdateEnvFile(envFullPath, "TEAMCITY_TOKEN", createdToken);
            LogHelper.LogSuccess("TeamCity token created via API and saved to .env");
        }
        else
        {
            LogHelper.LogError("Could not create TeamCity token via API; cannot continue without TEAMCITY_TOKEN");
            return 1;
        }
    }
    catch (Exception ex)
    {
        LogHelper.LogError($"TeamCity API token creation failed: {ex.Message}");
        return 1;
    }
}

// Get and validate tokens
LogHelper.LogSection("Token Setup");

var teamcityToken = await GetAndValidateTokenAsync(
    httpClient,
    "TeamCity",
    teamcityUrl,
    "TEAMCITY_TOKEN",
    envFullPath,
    teamCityService.ValidateTeamCityTokenAsync);

if (string.IsNullOrEmpty(teamcityToken))
{
    LogHelper.LogError("Failed to obtain valid TeamCity token; exiting");
    return 1;
}

// Ensure GitLab is available before attempting token operations
LogHelper.Log("Waiting for GitLab to become available...");
var gitlabReady = await HttpHelper.WaitForServiceAsync(httpClient, gitlabUrl, TimeSpan.FromMinutes(5));
if (!gitlabReady)
{
    LogHelper.LogError("GitLab did not become available; exiting");
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
    LogHelper.LogError("Failed to obtain valid GitLab token; exiting");
    return 1;
}

// Create GitLab projects
if (!string.IsNullOrEmpty(gitlabToken))
{
    LogHelper.LogSection("Setting up GitLab test projects...");

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

    LogHelper.LogSuccess($"{projectsCreated} GitLab test project(s) ready");
}

// Create TeamCity projects
if (!string.IsNullOrEmpty(teamcityToken))
{
    LogHelper.LogSection("Setting up TeamCity...");
    var success = await teamCityService.CreateProjectAsync(httpClient, teamcityUrl, teamcityToken);
    if (success)
    {
        LogHelper.LogSuccess("TeamCity project created");
    }

    // Authorize agents
    LogHelper.Log("Authorizing TeamCity agents...");
    var agentsAuthorized = await teamCityService.AuthorizeAgentsAsync(httpClient, teamcityUrl, teamcityToken);
    if (agentsAuthorized)
    {
        LogHelper.LogSuccess("TeamCity agents authorized");
    }
    else
    {
        LogHelper.LogWarning("⚠ Agent authorization incomplete - some agents may need manual approval");
    }
}

LogHelper.LogSection("Bootstrap complete!");
LogHelper.Log("Services available at:");
LogHelper.Log($"  GitLab:   {gitlabUrl}");
LogHelper.Log($"  TeamCity: {teamcityUrl}");
LogHelper.LogSeparator();

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
    // Special handling for GitLab: poll the .env file for a token and validate it
    if (serviceName == "GitLab")
    {
        var timeout = TimeSpan.FromMinutes(7);
        var deadline = DateTime.UtcNow + timeout;
        var pollInterval = TimeSpan.FromSeconds(5);

        LogHelper.Log($"Waiting up to {timeout.TotalMinutes} minutes for GITLAB_TOKEN in .env...");

        while (DateTime.UtcNow < deadline)
        {
            // Reload .env to pick up tokens written by external processes
            EnvHelper.LoadEnvFile(envFilePath);
            var token = Environment.GetEnvironmentVariable(envVarName);

            if (!string.IsNullOrEmpty(token))
            {
                LogHelper.Log("Found GITLAB_TOKEN in .env; validating...");
                try
                {
                    var isValid = await validator(client, serviceUrl, token);
                    if (isValid)
                    {
                        LogHelper.LogSuccess("GitLab token is valid");
                        // Ensure the .env is updated consistently
                        EnvHelper.SaveOrUpdateEnvFile(envFilePath, envVarName, token);
                        return token;
                    }
                    else
                    {
                        LogHelper.Log("GITLAB_TOKEN present but not valid yet; will retry until timeout");
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.LogWarning($"Error validating GitLab token: {ex.Message}");
                }
            }
            else
            {
                LogHelper.Log("No GITLAB_TOKEN found yet; polling .env...");
            }

            await Task.Delay(pollInterval);
        }

        LogHelper.LogError($"Timed out waiting for a valid GITLAB_TOKEN after {timeout.TotalMinutes} minutes");
        return null;
    }

    // Default behavior for other services (interactive TeamCity flow)
    var tokenInteractive = Environment.GetEnvironmentVariable(envVarName);

    while (true)
    {
        if (string.IsNullOrEmpty(tokenInteractive))
        {
            if (serviceName == "TeamCity")
            {
                LogHelper.LogError("TeamCity token missing and could not be created automatically; cannot proceed.");
                return null;
            }

            LogHelper.Log($"\n{serviceName} token not found in environment or .env file");
            LogHelper.Log($"Please create a token in {serviceName}:");
            if (serviceName == "GitLab")
            {
                LogHelper.Log($"  1. Visit {serviceUrl}/-/profile/personal_access_tokens");
                LogHelper.Log("  2. Create a token with 'api', 'read_api', 'write_repository' scopes");
            }

            Console.Write($"Enter {serviceName} token: ");
            tokenInteractive = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(tokenInteractive))
            {
                LogHelper.LogError("Token cannot be empty");
                continue;
            }
        }

        LogHelper.Log($"Validating {serviceName} token...");
        var isValidInteractive = await validator(client, serviceUrl, tokenInteractive);

        if (isValidInteractive)
        {
            LogHelper.LogSuccess($"{serviceName} token is valid");
            EnvHelper.SaveOrUpdateEnvFile(envFilePath, envVarName, tokenInteractive);
            return tokenInteractive;
        }

        LogHelper.LogError($"✗ {serviceName} token is invalid or insufficient permissions");
        tokenInteractive = null; // Force re-prompt
    }
}
