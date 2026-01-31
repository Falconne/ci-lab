using Bootstrap.Services;
using Bootstrap.Services.Gitlab;
using Bootstrap.Services.TeamCity;
using Bootstrap.Utilities;
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

    var gitlabURL = envService.GetValue("GITLAB_URL") ?? "http://localhost:8081";
    var teamcityURL = envService.GetValue("TEAMCITY_URL") ?? "http://localhost:8111";
    Log.Information($"Gitlab URL:   {gitlabURL}");
    Log.Information($"TeamCity URL: {teamcityURL}");

    // Create service instances
    using var browserService = new PlaywrightService();
    var gitlabRootPassword = envService.GetValue("GITLAB_ROOT_PASSWORD") ?? "changeme123";
    using var teamCityBootstrapService = new TeamCityBootstrapService(
        browserService,
        envService,
        teamcityURL,
        "root",
        gitlabRootPassword);

    Logging.LogSection("TeamCity Automated Setup");
    await teamCityBootstrapService.Execute();
    Log.Information("TeamCity initial setup completed");

    Logging.LogSection("Gitlab Automated Setup");
    using var gitlabBootstrapService = new GitlabBootstrapService(gitlabURL, envService);
    await gitlabBootstrapService.Execute();

    Logging.LogSection("Running Initial Project Setup");
    var gitlabToken = envService.GetValue("GITLAB_TOKEN");
    var teamcityToken = envService.GetValue("TEAMCITY_TOKEN");
    using var gitlabService = new GitlabService(gitlabURL, gitlabToken!);
    using var teamCityService = new TeamCityService(teamcityURL, "root", gitlabRootPassword);
    var projectSetupService = new ProjectSetupService(gitlabService, teamCityService, gitlabURL, gitlabToken!);
    await projectSetupService.Execute();

    Logging.LogSection("Bootstrap complete!");
    Log.Information("Services available at:");
    Log.Information($"  GitLab:   {gitlabURL}");
    Log.Information($"  TeamCity: {teamcityURL}");
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
    try
    {
        Log.CloseAndFlush();
    }
    catch
    {
    }
}