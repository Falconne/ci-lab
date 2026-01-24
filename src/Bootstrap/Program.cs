using Bootstrap.Services.GitLab;
using Bootstrap.Services.TeamCity;
using Bootstrap.Services.Utilities;

Logging.LogSeparator();
Logging.Log.Information("CI Lab Bootstrap - Manual Setup (.NET 9)");
Logging.LogSeparator();

// Determine .env file path relative to the project directory
var envPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", ".env");
var envFullPath = Path.GetFullPath(envPath);

// Load environment variables from .env file if it exists
EnvHelper.LoadEnvFile(envFullPath);

var gitlabUrl = Environment.GetEnvironmentVariable("GITLAB_URL") ?? "http://localhost:8081";
var teamcityUrl = Environment.GetEnvironmentVariable("TEAMCITY_URL") ?? "http://localhost:8111";
Logging.Log.Information($"GitLab URL:   {gitlabUrl}");
Logging.Log.Information($"TeamCity URL: {teamcityUrl}");

using var httpClient =
    new HttpClient(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseCookies = false
        })
    { Timeout = TimeSpan.FromSeconds(10) };

// Create service instances
using var browserService = new PlaywrightService();
var teamCityService = new TeamCityBootstrapService(browserService);
var gitLabService = new GitLabService();

// Wait for TeamCity first (it's often available before GitLab)
Logging.Log.Information("Waiting for TeamCity to become available...");
var teamcityReady = await HttpHelper.WaitForService(
    httpClient,
    teamcityUrl,
    TimeSpan.FromMinutes(5),
    503,
    401);

if (!teamcityReady)
{
    Logging.Log.Error("TeamCity did not become available; exiting");
    return 1;
}

// Automated TeamCity initial setup (Playwright driven)
Logging.LogSection("TeamCity Automated Initial Setup");

var gitlabRootPassword = Environment.GetEnvironmentVariable("GITLAB_ROOT_PASSWORD") ?? "changeme123";
var teamcitySetupSuccess = await teamCityService.Execute(
    httpClient,
    teamcityUrl,
    "root",
    gitlabRootPassword);

if (!teamcitySetupSuccess)
{
    Logging.Log.Error("TeamCity automated setup failed");
    Logging.Log.Error("Check screenshots in data/screenshots/ directory for details");
    return 1;
}

Logging.Log.Information("TeamCity initial setup completed");

// Ensure TEAMCITY_TOKEN is present and valid; if missing or invalid, attempt API creation
var existingTeamcityToken = Environment.GetEnvironmentVariable("TEAMCITY_TOKEN");
var needCreateToken = string.IsNullOrEmpty(existingTeamcityToken);
if (!needCreateToken && existingTeamcityToken != null)
{
    Logging.Log.Information("Validating existing TEAMCITY_TOKEN...");
    try
    {
        var valid = await teamCityService.ValidateTeamCityToken(
            httpClient,
            teamcityUrl,
            existingTeamcityToken);

        if (!valid)
        {
            Logging.Log.Information(
                "Existing TEAMCITY_TOKEN is invalid or insufficient permissions; will attempt to create a new token via API");

            needCreateToken = true;
        }
        else
        {
            Logging.Log.Information("Existing TEAMCITY_TOKEN is valid");
        }
    }
    catch (Exception ex)
    {
        Logging.Log.Warning($"Error validating existing TEAMCITY_TOKEN: {ex.Message}");
        needCreateToken = true;
    }
}

if (needCreateToken)
{
    Logging.Log.Information("Attempting to create TeamCity token via REST API...");
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
            Logging.Log.Information("TeamCity token created via API and saved to .env");
        }
        else
        {
            Logging.Log.Error(
                "Could not create TeamCity token via API; cannot continue without TEAMCITY_TOKEN");

            return 1;
        }
    }
    catch (Exception ex)
    {
        Logging.Log.Error($"TeamCity API token creation failed: {ex.Message}");
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
    Logging.Log.Error("Failed to obtain valid TeamCity token; exiting");
    return 1;
}

// Ensure GitLab is available before attempting token operations
Logging.Log.Information("Waiting for GitLab to become available...");
var gitlabReady = await HttpHelper.WaitForService(httpClient, gitlabUrl, TimeSpan.FromMinutes(5));
if (!gitlabReady)
{
    Logging.Log.Error("GitLab did not become available; exiting");
    return 1;
}

var gitlabToken = await GetAndValidateTokenAsync(
    httpClient,
    "GitLab",
    gitlabUrl,
    "GITLAB_TOKEN",
    envFullPath,
    GitLabService.ValidateGitLabToken);

if (string.IsNullOrEmpty(gitlabToken))
{
    Logging.Log.Error("Failed to obtain valid GitLab token; exiting");
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

    Logging.Log.Information($"{projectsCreated} GitLab test project(s) ready");
}

// Create TeamCity projects
if (!string.IsNullOrEmpty(teamcityToken))
{
    Logging.LogSection("Setting up TeamCity...");
    var success = await teamCityService.CreateProject(httpClient, teamcityUrl, teamcityToken);
    if (success)
    {
        Logging.Log.Information("TeamCity project created");
    }

    // Authorize agents
    Logging.Log.Information("Authorizing TeamCity agents...");
    var agentsAuthorized = await teamCityService.AuthorizeAgents(httpClient, teamcityUrl, teamcityToken);
    if (agentsAuthorized)
    {
        Logging.Log.Information("TeamCity agents authorized");
    }
    else
    {
        Logging.Log.Warning("Agent authorization incomplete - some agents may need manual approval");
    }
}

Logging.LogSection("Bootstrap complete!");
Logging.Log.Information("Services available at:");
Logging.Log.Information($"  GitLab:   {gitlabUrl}");
Logging.Log.Information($"  TeamCity: {teamcityUrl}");
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

        Logging.Log.Information($"Waiting up to {timeout.TotalMinutes} minutes for GITLAB_TOKEN in .env...");

        while (DateTime.UtcNow < deadline)
        {
            // Reload .env to pick up tokens written by external processes
            EnvHelper.LoadEnvFile(envFilePath);
            var token = Environment.GetEnvironmentVariable(envVarName);

            if (!string.IsNullOrEmpty(token))
            {
                Logging.Log.Information("Found GITLAB_TOKEN in .env; validating...");
                try
                {
                    var isValid = await validator(client, serviceUrl, token);
                    if (isValid)
                    {
                        Logging.Log.Information("GitLab token is valid");
                        // Ensure the .env is updated consistently
                        EnvHelper.SaveOrUpdateEnvFile(envFilePath, envVarName, token);
                        return token;
                    }

                    Logging.Log.Information("GITLAB_TOKEN present but not valid yet; will retry until timeout");
                }
                catch (Exception ex)
                {
                    Logging.Log.Warning($"Error validating GitLab token: {ex.Message}");
                }
            }
            else
            {
                Logging.Log.Information("No GITLAB_TOKEN found yet; polling .env...");
            }

            await Task.Delay(pollInterval);
        }

        Logging.Log.Error(
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
                Logging.Log.Error(
                    "TeamCity token missing and could not be created automatically; cannot proceed.");

                return null;
            }

            Logging.Log.Information($"\n{serviceName} token not found in environment or .env file");
            Logging.Log.Information($"Please create a token in {serviceName}:");
            if (serviceName == "GitLab")
            {
                Logging.Log.Information($"  1. Visit {serviceUrl}/-/profile/personal_access_tokens");
                Logging.Log.Information("  2. Create a token with 'api', 'read_api', 'write_repository' scopes");
            }

            Console.Write($"Enter {serviceName} token: ");
            tokenInteractive = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(tokenInteractive))
            {
                Logging.Log.Error("Token cannot be empty");
                continue;
            }
        }

        Logging.Log.Information($"Validating {serviceName} token...");
        var isValidInteractive = await validator(client, serviceUrl, tokenInteractive);

        if (isValidInteractive)
        {
            Logging.Log.Information($"{serviceName} token is valid");
            EnvHelper.SaveOrUpdateEnvFile(envFilePath, envVarName, tokenInteractive);
            return tokenInteractive;
        }

        Logging.Log.Error($"{serviceName} token is invalid or insufficient permissions");
        tokenInteractive = null; // Force re-prompt
    }
}