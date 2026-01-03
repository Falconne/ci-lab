using Bootstrap.Services.GitLab;
using Bootstrap.Services.TeamCity;
using Bootstrap.Services.Utilities;

Logging.LogSeparator();
Logging.Log("CI Lab Bootstrap - Manual Setup (.NET 9)");
Logging.LogSeparator();

// Determine .env file path relative to the project directory
var envPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", ".env");
var envFullPath = Path.GetFullPath(envPath);

// Load environment variables from .env file if it exists
EnvHelper.LoadEnvFile(envFullPath);

var gitlabUrl = Environment.GetEnvironmentVariable("GITLAB_URL") ?? "http://localhost:8081";
var teamcityUrl = Environment.GetEnvironmentVariable("TEAMCITY_URL") ?? "http://localhost:8111";
Logging.Log($"GitLab URL:   {gitlabUrl}");
Logging.Log($"TeamCity URL: {teamcityUrl}");

using var httpClient =
    new HttpClient(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseCookies = false
        })
    { Timeout = TimeSpan.FromSeconds(10) };

// Create service instances
var teamCityService = new TeamCityService();
var gitLabService = new GitLabService();

// Wait for TeamCity first (it's often available before GitLab)
Logging.Log("Waiting for TeamCity to become available...");
var teamcityReady = await HttpHelper.WaitForService(
    httpClient,
    teamcityUrl,
    TimeSpan.FromMinutes(5),
    503,
    401);

if (!teamcityReady)
{
    Logging.LogError("TeamCity did not become available; exiting");
    return 1;
}

// Automated TeamCity initial setup (Playwright driven)
Logging.LogSection("TeamCity Automated Initial Setup");

var gitlabRootPassword = Environment.GetEnvironmentVariable("GITLAB_ROOT_PASSWORD") ?? "changeme123";
var teamcitySetupSuccess = await teamCityService.AutomateTeamCitySetup(
    httpClient,
    teamcityUrl,
    "root",
    gitlabRootPassword);

if (!teamcitySetupSuccess)
{
    Logging.LogError("TeamCity automated setup failed");
    Logging.LogError("Check screenshots in data/screenshots/ directory for details");
    return 1;
}

Logging.LogSuccess("TeamCity initial setup completed");

// Ensure TEAMCITY_TOKEN is present and valid; if missing or invalid, attempt API creation
var existingTeamcityToken = Environment.GetEnvironmentVariable("TEAMCITY_TOKEN");
var needCreateToken = string.IsNullOrEmpty(existingTeamcityToken);
if (!needCreateToken && existingTeamcityToken != null)
{
Logging.Log("Validating existing TEAMCITY_TOKEN...");
    try
    {
        var valid = await teamCityService.ValidateTeamCityToken(
            httpClient,
            teamcityUrl,
            existingTeamcityToken);

        if (!valid)
        {
            Logging.Log(
                "Existing TEAMCITY_TOKEN is invalid or insufficient permissions; will attempt to create a new token via API");

            needCreateToken = true;
        }
        else
        {
            Logging.Log("Existing TEAMCITY_TOKEN is valid");
        }
    }
    catch (Exception ex)
    {
        Logging.LogWarning($"Error validating existing TEAMCITY_TOKEN: {ex.Message}");
        needCreateToken = true;
    }
}

if (needCreateToken)
{
    Logging.Log("Attempting to create TeamCity token via REST API...");
    try
    {
        var createdToken = await teamCityService.TryCreateTokenViaApi(
            httpClient,
            teamcityUrl,
            "root",
            gitlabRootPassword,
            "bootstrap-automation");

        if (!string.IsNullOrEmpty(createdToken))
        {
            EnvHelper.SaveOrUpdateEnvFile(envFullPath, "TEAMCITY_TOKEN", createdToken);
            Logging.LogSuccess("TeamCity token created via API and saved to .env");
        }
        else
        {
            Logging.LogError(
                "Could not create TeamCity token via API; cannot continue without TEAMCITY_TOKEN");

            return 1;
        }
    }
    catch (Exception ex)
    {
        Logging.LogError($"TeamCity API token creation failed: {ex.Message}");
        return 1;
    }
}

// Get and validate tokens
Logging.LogSection("Token Setup");

var teamcityToken = await GetAndValidateTokenAsync(
    httpClient,
    "TeamCity",
    teamcityUrl,
    "TEAMCITY_TOKEN",
    envFullPath,
    teamCityService.ValidateTeamCityToken);

if (string.IsNullOrEmpty(teamcityToken))
{
Logging.LogError("Failed to obtain valid TeamCity token; exiting");
    return 1;
}

// Ensure GitLab is available before attempting token operations
Logging.Log("Waiting for GitLab to become available...");
var gitlabReady = await HttpHelper.WaitForService(httpClient, gitlabUrl, TimeSpan.FromMinutes(5));
if (!gitlabReady)
{
Logging.LogError("GitLab did not become available; exiting");
    return 1;
}

var gitlabToken = await GetAndValidateTokenAsync(
    httpClient,
    "GitLab",
    gitlabUrl,
    "GITLAB_TOKEN",
    envFullPath,
    gitLabService.ValidateGitLabToken);

if (string.IsNullOrEmpty(gitlabToken))
{
Logging.LogError("Failed to obtain valid GitLab token; exiting");
    return 1;
}

// Create GitLab projects
if (!string.IsNullOrEmpty(gitlabToken))
{
Logging.LogSection("Setting up GitLab test projects...");

    var projectsCreated = 0;
    for (var i = 1; i <= 5; i++)
    {
        var projectName = $"test-project-{i}";
        var created = await gitLabService.CreateAndPopulateGitLabProject(
            httpClient,
            gitlabUrl,
            gitlabToken,
            projectName,
            i);

        if (created)
        {
            projectsCreated++;
        }
    }

            Logging.LogSuccess($"{projectsCreated} GitLab test project(s) ready");
}

// Create TeamCity projects
if (!string.IsNullOrEmpty(teamcityToken))
{
    Logging.LogSection("Setting up TeamCity...");
    var success = await teamCityService.CreateProject(httpClient, teamcityUrl, teamcityToken);
    if (success)
    {
        Logging.LogSuccess("TeamCity project created");
    }

    // Authorize agents
    Logging.Log("Authorizing TeamCity agents...");
    var agentsAuthorized = await teamCityService.AuthorizeAgents(httpClient, teamcityUrl, teamcityToken);
    if (agentsAuthorized)
    {
        Logging.LogSuccess("TeamCity agents authorized");
    }
    else
    {
        Logging.LogWarning("⚠ Agent authorization incomplete - some agents may need manual approval");
    }
}

Logging.LogSection("Bootstrap complete!");
Logging.Log("Services available at:");
Logging.Log($"  GitLab:   {gitlabUrl}");
Logging.Log($"  TeamCity: {teamcityUrl}");
Logging.LogSeparator();

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

        Logging.Log($"Waiting up to {timeout.TotalMinutes} minutes for GITLAB_TOKEN in .env...");

        while (DateTime.UtcNow < deadline)
        {
            // Reload .env to pick up tokens written by external processes
            EnvHelper.LoadEnvFile(envFilePath);
            var token = Environment.GetEnvironmentVariable(envVarName);

            if (!string.IsNullOrEmpty(token))
            {
                Logging.Log("Found GITLAB_TOKEN in .env; validating...");
                try
                {
                    var isValid = await validator(client, serviceUrl, token);
                    if (isValid)
                    {
                    Logging.LogSuccess("GitLab token is valid");
                        // Ensure the .env is updated consistently
                        EnvHelper.SaveOrUpdateEnvFile(envFilePath, envVarName, token);
                        return token;
                    }

                    Logging.Log("GITLAB_TOKEN present but not valid yet; will retry until timeout");
                }
                catch (Exception ex)
                {
                    Logging.LogWarning($"Error validating GitLab token: {ex.Message}");
                }
            }
            else
            {
                Logging.Log("No GITLAB_TOKEN found yet; polling .env...");
            }

            await Task.Delay(pollInterval);
        }

        Logging.LogError(
            $"Timed out waiting for a valid GITLAB_TOKEN after {timeout.TotalMinutes} minutes");

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
                    Logging.LogError(
                        "TeamCity token missing and could not be created automatically; cannot proceed.");

                    return null;
                }

            Logging.Log($"\n{serviceName} token not found in environment or .env file");
            Logging.Log($"Please create a token in {serviceName}:");
                if (serviceName == "GitLab")
                {
                    Logging.Log($"  1. Visit {serviceUrl}/-/profile/personal_access_tokens");
                    Logging.Log("  2. Create a token with 'api', 'read_api', 'write_repository' scopes");
                }

            Console.Write($"Enter {serviceName} token: ");
            tokenInteractive = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(tokenInteractive))
            {
                Logging.LogError("Token cannot be empty");
                continue;
            }
        }

        Logging.Log($"Validating {serviceName} token...");
        var isValidInteractive = await validator(client, serviceUrl, tokenInteractive);

        if (isValidInteractive)
        {
            Logging.LogSuccess($"{serviceName} token is valid");
            EnvHelper.SaveOrUpdateEnvFile(envFilePath, envVarName, tokenInteractive);
            return tokenInteractive;
        }

        Logging.LogError($"✗ {serviceName} token is invalid or insufficient permissions");
        tokenInteractive = null; // Force re-prompt
    }
}