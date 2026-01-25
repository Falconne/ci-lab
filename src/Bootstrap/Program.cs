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

// Create service instances
using var browserService = new PlaywrightService();
var gitlabRootPassword = Environment.GetEnvironmentVariable("GITLAB_ROOT_PASSWORD") ?? "changeme123";
using var teamCityBootstrapService = new TeamCityBootstrapService(
    browserService,
    envService,
    teamcityUrl,
    "root",
    gitlabRootPassword);

using var gitlabService = new GitlabBootstrapService(gitlabUrl);

// Wait for TeamCity first (it will be available before GitLab)
Log.Information("Waiting for TeamCity to become available...");
var teamcityReady = await HttpHelper.WaitForService(
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
var gitlabReady = await HttpHelper.WaitForService(gitlabUrl, TimeSpan.FromMinutes(5));
if (!gitlabReady)
{
    Log.Error("GitLab did not become available; exiting");
    return 1;
}

var gitlabToken = await GetAndValidateGitlabToken(gitlabService, envService);

if (string.IsNullOrEmpty(gitlabToken))
{
    Log.Error("Failed to obtain valid GitLab token; exiting");
    return 1;
}

// Create GitLab projects
Logging.LogSection("Setting up Gitlab test projects...");

var projectsCreated = 0;
for (var i = 1; i <= 5; i++)
{
    var projectName = $"test-project-{i}";
    var created = await gitlabService.CreateAndPopulateGitlabProject(
        gitlabToken,
        projectName,
        i);

    if (created)
    {
        projectsCreated++;
    }
}

Log.Information($"{projectsCreated} Gitlab test project(s) ready");

// Create TeamCity projects
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

Logging.LogSection("Bootstrap complete!");
Log.Information("Services available at:");
Log.Information($"  GitLab:   {gitlabUrl}");
Log.Information($"  TeamCity: {teamcityUrl}");
Logging.LogSeparator();

return 0;

// Helper method for GitLab token validation with polling
static async Task<string?> GetAndValidateGitlabToken(
    GitlabBootstrapService gitlabService,
    EnvService envService)
{
    var timeout = TimeSpan.FromMinutes(7);
    var deadline = DateTime.UtcNow + timeout;
    var pollInterval = TimeSpan.FromSeconds(5);

    Log.Information($"Waiting up to {timeout.TotalMinutes} minutes for GITLAB_TOKEN in .env...");

    while (DateTime.UtcNow < deadline)
    {
        // Reload .env to pick up tokens written by external processes
        envService.Load();
        var token = Environment.GetEnvironmentVariable("GITLAB_TOKEN");

        if (!string.IsNullOrEmpty(token))
        {
            Log.Information("Found GITLAB_TOKEN in .env; validating...");
            try
            {
                var isValid = await gitlabService.ValidateGitlabToken(token);
                if (isValid)
                {
                    Log.Information("Gitlab token is valid");
                    envService.SaveOrUpdateEnvFile("GITLAB_TOKEN", token);
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

    Log.Error($"Timed out waiting for a valid GITLAB_TOKEN after {timeout.TotalMinutes} minutes");
    return null;
}
