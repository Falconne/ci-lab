using Bootstrap.Services;
using Bootstrap.Services.Gitlab;
using Bootstrap.Services.TeamCity;
using Bootstrap.Services.Utilities;
using Serilog;

Logging.Init();

Logging.LogSeparator();
Log.Information("CI Lab Bootstrap");
Logging.LogSeparator();

// Determine .env file path relative to the project directory
var envPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", ".env");
var envFullPath = Path.GetFullPath(envPath);

// Create EnvService instance
var envService = new EnvService(envFullPath);

// Load environment variables from .env file if it exists
envService.Load();

var gitlabUrl = Environment.GetEnvironmentVariable("GITLAB_URL") ?? "http://localhost:8081";
var teamcityUrl = Environment.GetEnvironmentVariable("TEAMCITY_URL") ?? "http://localhost:8111";
Log.Information($"Gitlab URL:   {gitlabUrl}");
Log.Information($"TeamCity URL: {teamcityUrl}");

using var httpClient =
    new HttpClient(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseCookies = false
        });

httpClient.Timeout = TimeSpan.FromSeconds(10);

// Create service instances
using var browserService = new PlaywrightService();
var gitlabRootPassword = Environment.GetEnvironmentVariable("GITLAB_ROOT_PASSWORD") ?? "changeme123";
var teamCityBootstrapService = new TeamCityBootstrapService(
    browserService,
    envService,
    teamcityUrl,
    httpClient,
    "root",
    gitlabRootPassword);

var gitlabService = new GitlabService(gitlabUrl);

// Wait for TeamCity first (it will be available before GitLab)
Log.Information("Waiting for TeamCity to become available...");
var teamcityReady = await HttpHelper.WaitForService(
    httpClient,
    teamcityUrl,
    TimeSpan.FromMinutes(5),
    503,
    401);

if (!teamcityReady)
{
    Log.Error("TeamCity did not become available; exiting");
    return 1;
}

Logging.LogSection("TeamCity Automated Initial Setup");

var teamcitySetupSuccess = await teamCityBootstrapService.Execute();

if (!teamcitySetupSuccess)
{
    Log.Error("TeamCity automated setup failed");
    Log.Error("Check screenshots in data/screenshots/ directory for details");
    return 1;
}

Logging.LogSection("Token Setup");

// Ensure TEAMCITY_TOKEN is present and valid
var teamcityTokenFromService = await teamCityBootstrapService.EnsureValidToken();

if (string.IsNullOrEmpty(teamcityTokenFromService))
{
    Log.Error("Failed to obtain or validate TeamCity token; exiting");
    return 1;
}

Log.Information("TeamCity initial setup completed");

// Ensure GitLab is available before attempting token operations
Log.Information("Waiting for Gitlab to become available...");
var gitlabReady = await HttpHelper.WaitForService(httpClient, gitlabUrl, TimeSpan.FromMinutes(5));
if (!gitlabReady)
{
    Log.Error("GitLab did not become available; exiting");
    return 1;
}

var gitlabToken = await GetAndValidateTokenAsync(
    httpClient,
    "Gitlab",
    gitlabUrl,
    "GITLAB_TOKEN",
    envService,
    (client, serviceUrl, token) => gitlabService.ValidateGitlabToken(client, token));

if (string.IsNullOrEmpty(gitlabToken))
{
    Log.Error("Failed to obtain valid GitLab token; exiting");
    return 1;
}

// Create GitLab projects
if (!string.IsNullOrEmpty(gitlabToken))
{
    Logging.LogSection("Setting up Gitlab test projects...");

    var projectsCreated = 0;
    for (var i = 1; i <= 5; i++)
    {
        var projectName = $"test-project-{i}";
        var created = await gitlabService.CreateAndPopulateGitlabProject(
            httpClient,
            gitlabToken,
            projectName,
            i);

        if (created)
        {
            projectsCreated++;
        }
    }

    Log.Information($"{projectsCreated} Gitlab test project(s) ready");
}

// Create TeamCity projects
if (!string.IsNullOrEmpty(teamcityTokenFromService))
{
    Logging.LogSection("Setting up TeamCity...");
    var success = await teamCityBootstrapService.CreateProject(teamcityTokenFromService);

    if (success)
    {
        Log.Information("TeamCity project created");
    }

    // Authorize agents
    Log.Information("Authorizing TeamCity agents...");
    var agentsAuthorized = await teamCityBootstrapService.AuthorizeAgents(teamcityTokenFromService);

    if (agentsAuthorized)
    {
        Log.Information("TeamCity agents authorized");
    }
    else
    {
        Log.Warning("Agent authorization incomplete - some agents may need manual approval");
    }
}

Logging.LogSection("Bootstrap complete!");
Log.Information("Services available at:");
Log.Information($"  GitLab:   {gitlabUrl}");
Log.Information($"  TeamCity: {teamcityUrl}");
Logging.LogSeparator();

return 0;

// Helper methods

static async Task<string?> GetAndValidateTokenAsync(
    HttpClient client,
    string serviceName,
    string serviceUrl,
    string envVarName,
    EnvService envService,
    Func<HttpClient, string, string, Task<bool>> validator)
{
    // Special handling for GitLab: poll the .env file for a token and validate it
    if (serviceName == "Gitlab")
    {
        var timeout = TimeSpan.FromMinutes(7);
        var deadline = DateTime.UtcNow + timeout;
        var pollInterval = TimeSpan.FromSeconds(5);

        Log.Information($"Waiting up to {timeout.TotalMinutes} minutes for GITLAB_TOKEN in .env...");

        while (DateTime.UtcNow < deadline)
        {
            // Reload .env to pick up tokens written by external processes
            envService.Load();
            var token = Environment.GetEnvironmentVariable(envVarName);

            if (!string.IsNullOrEmpty(token))
            {
                Log.Information("Found GITLAB_TOKEN in .env; validating...");
                try
                {
                    var isValid = await validator(client, serviceUrl, token);
                    if (isValid)
                    {
                        Log.Information("Gitlab token is valid");
                        // Ensure the .env is updated consistently
                        envService.SaveOrUpdateEnvFile(envVarName, token);
                        return token;
                    }

                    Log.Information("GITLAB_TOKEN present but not valid yet; will retry until timeout");
                }
                catch (Exception ex)
                {
                    Log.Warning($"Error validating Gitlab token: {ex.Message}");
                }
            }
            else
            {
                Log.Information("No GITLAB_TOKEN found yet; polling .env...");
            }

            await Task.Delay(pollInterval);
        }

        Log.Error(
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
                Log.Error(
                    "TeamCity token missing and could not be created automatically; cannot proceed.");

                return null;
            }

            Log.Information($"\n{serviceName} token not found in environment or .env file");
            Log.Information($"Please create a token in {serviceName}:");
            if (serviceName == "Gitlab")
            {
                Log.Information($"  1. Visit {serviceUrl}/-/profile/personal_access_tokens");
                Log.Information("  2. Create a token with 'api', 'read_api', 'write_repository' scopes");
            }

            Console.Write($"Enter {serviceName} token: ");
            tokenInteractive = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(tokenInteractive))
            {
                Log.Error("Token cannot be empty");
                continue;
            }
        }

        Log.Information($"Validating {serviceName} token...");
        var isValidInteractive = await validator(client, serviceUrl, tokenInteractive);

        if (isValidInteractive)
        {
            Log.Information($"{serviceName} token is valid");
            envService.SaveOrUpdateEnvFile(envVarName, tokenInteractive);
            return tokenInteractive;
        }

        Log.Error($"{serviceName} token is invalid or insufficient permissions");
        tokenInteractive = null; // Force re-prompt
    }
}