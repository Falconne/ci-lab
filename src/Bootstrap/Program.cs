using Bootstrap.Services;
using Bootstrap.Services.Gitlab;
using Bootstrap.Services.TeamCity;
using Bootstrap.Services.Utilities;
using Serilog;

Logging.Init();

Logging.LogSeparator();
Log.Information("CI Lab Bootstrap");
Logging.LogSeparator();

try
{
    // Determine .env file path relative to the project directory
    var envPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", ".env");
    var envFullPath = Path.GetFullPath(envPath);

    // Create EnvFileService instance
    var envService = new EnvFileService(envFullPath);

    // Load environment variables from .env file if it exists
    envService.Load();

    var gitlabUrl = envService.GetValue("GITLAB_URL") ?? "http://localhost:8081";
    var teamcityUrl = envService.GetValue("TEAMCITY_URL") ?? "http://localhost:8111";
    Log.Information($"Gitlab URL:   {gitlabUrl}");
    Log.Information($"TeamCity URL: {teamcityUrl}");

    // Create service instances
    using var browserService = new PlaywrightService();
    var gitlabRootPassword = envService.GetValue("GITLAB_ROOT_PASSWORD") ?? "changeme123";
    using var teamCityBootstrapService = new TeamCityBootstrapService(
        browserService,
        envService,
        teamcityUrl,
        "root",
        gitlabRootPassword);

    Logging.LogSection("TeamCity Automated Initial Setup");

    var teamcitySetupSuccess = await teamCityBootstrapService.Execute();

    if (!teamcitySetupSuccess)
    {
        Log.Error("TeamCity automated setup failed");
        Log.Error("Check screenshots in data/screenshots/ directory for details");
        return 1;
    }

    Log.Information("Setting up TeamCity token if needed");
    var teamcityTokenFromService = await teamCityBootstrapService.EnsureValidToken();

    if (string.IsNullOrEmpty(teamcityTokenFromService))
    {
        Log.Error("Failed to obtain or validate TeamCity token; exiting");
        return 1;
    }

    Log.Information("Authorizing TeamCity agents...");
    if (!await teamCityBootstrapService.AuthorizeAgents(teamcityTokenFromService))
    {
        return 1;
    }

    Log.Information("TeamCity initial setup completed");

    Logging.LogSection("Gitlab Setup");

    // Ensure GitLab is available before attempting token operations
    Log.Information("Waiting for Gitlab to become available...");
    var gitlabReady = await HttpHelper.WaitForService(gitlabUrl, TimeSpan.FromMinutes(5));
    if (!gitlabReady)
    {
        Log.Error("GitLab did not become available; exiting");
        return 1;
    }

    var gitlabToken = await GetAndValidateGitlabToken(gitlabUrl, envService);

    if (string.IsNullOrEmpty(gitlabToken))
    {
        Log.Error("Failed to obtain valid GitLab token; exiting");
        return 1;
    }

    // Create GitLab projects
    Logging.LogSection("Setting up Gitlab test projects...");

    using var gitlabProjectService = new GitlabService(gitlabUrl, gitlabToken);

    var projectsCreated = 0;
    for (var i = 1; i <= 5; i++)
    {
        var projectName = $"test-project-{i}";
        var created = await gitlabProjectService.CreateTopLevelProject(
            projectName);

        if (created)
        {
            projectsCreated++;
        }
    }

    Log.Information($"{projectsCreated} Gitlab test project(s) ready");

    Logging.LogSection("Bootstrap complete!");
    Log.Information("Services available at:");
    Log.Information($"  GitLab:   {gitlabUrl}");
    Log.Information($"  TeamCity: {teamcityUrl}");
    Logging.LogSeparator();

    return 0;
}
catch (Exception ex)
{
    // Log unexpected exceptions to the logfile before aborting
    Log.Fatal(ex, "Unexpected exception during bootstrap");
    Logging.LogSeparator();
    return 1;
}
finally
{
    // Ensure any buffered logs are written out
    try
    {
        Log.CloseAndFlush();
    }
    catch
    {
        // Swallow any errors during log shutdown to avoid masking original exception
    }
}

// Helper method for GitLab token validation with polling
static async Task<string?> GetAndValidateGitlabToken(
    string gitlabUrl,
    EnvFileService envFileService)
{
    var timeout = TimeSpan.FromMinutes(7);
    var deadline = DateTime.UtcNow + timeout;
    var pollInterval = TimeSpan.FromSeconds(5);

    Log.Information($"Waiting up to {timeout.TotalMinutes} minutes for GITLAB_TOKEN in .env...");

    while (DateTime.UtcNow < deadline)
    {
        // Reload .env to pick up tokens written by external processes
        envFileService.Load();
        var token = envFileService.GetValue("GITLAB_TOKEN");

        if (!string.IsNullOrEmpty(token))
        {
            Log.Information("Found GITLAB_TOKEN in .env; validating...");
            try
            {
                using var tempGitlabService = new GitlabBootstrapService(gitlabUrl, token);
                var isValid = await tempGitlabService.ValidateGitlabToken();
                if (isValid)
                {
                    Log.Information("Gitlab token is valid");
                    envFileService.SaveOrUpdateEnvFile("GITLAB_TOKEN", token);
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