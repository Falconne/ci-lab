using Bootstrap.Services.TeamCity;
using Serilog;

namespace Bootstrap.Services.Gitlab;

public class ProjectSetupService
{
    private readonly string _gitlabInternalUrl;

    private readonly GitlabService _gitlabService;

    private readonly string _gitlabToken;

    private readonly string _gitlabUrl;

    private readonly TeamCityService _teamCityService;

    public ProjectSetupService(
        GitlabService gitlabService,
        TeamCityService teamCityService,
        string gitlabUrl,
        string gitlabToken,
        string? gitlabInternalUrl = null)
    {
        _gitlabService = gitlabService;
        _teamCityService = teamCityService;
        _gitlabUrl = gitlabUrl;
        _gitlabToken = gitlabToken;
        // TeamCity runs inside Docker and needs to access GitLab via its internal network hostname
        _gitlabInternalUrl = gitlabInternalUrl ?? "http://gitlab:8081";
    }

    public async Task Execute()
    {
        await SetupTestRepos();

        await SetupTeamCityConfigRepo();
    }

    private async Task SetupTestRepos()
    {
        Log.Information("Setting up GitLab test projects...");

        // Create the test group
        var testGroup = await _gitlabService.CreateGroup("Test Group");

        foreach (var i in Enumerable.Range(1, 3))
        {
            var projectName = $"top-level-project-{i}";
            await _gitlabService.CreateTopLevelProject(projectName, testGroup.Id);
        }

        foreach (var i in Enumerable.Range(1, 4))
        {
            var projectName = $"sub-project-{i}";
            await _gitlabService.CreateRegularProject(projectName, testGroup.Id);
        }

        Log.Information("GitLab test projects ready");
    }

    private async Task SetupTeamCityConfigRepo()
    {
        Log.Information("Setting up TeamCity configuration under source control...");

        // Create the TeamCityConfig repository in GitLab
        var configProject = await _gitlabService.CreateProject("TeamCityConfig");
        Log.Information($"TeamCityConfig project ID: {configProject.Id}");

        // Create a VCS root in TeamCity for this repository
        // TeamCity needs to use the internal Docker network URL to access GitLab
        var internalGitUrl = configProject.HttpUrlToRepo.Replace(_gitlabUrl, _gitlabInternalUrl);
        Log.Information($"Using internal Git URL for TeamCity: {internalGitUrl}");

        var (vcsRootId, wasUpdated) = await _teamCityService.CreateOrUpdateVcsRoot(
            "TeamCityConfig",
            internalGitUrl,
            "master",
            "root",
            _gitlabToken);

        // Enable versioned settings for the Root project with Kotlin format
        // Force reconfigure if VCS root was updated to ensure proper setup
        await _teamCityService.EnableVersionedSettings(
            vcsRootId,
            "kotlin",
            false,
            wasUpdated);

        // Trigger TeamCity to commit current settings to VCS
        await _teamCityService.CommitCurrentSettingsToVcs();

        // Wait for settings.kts to appear in the GitLab repo (throws on failure)
        await _teamCityService.WaitForSettingsInRepo(
            _gitlabUrl,
            _gitlabToken,
            configProject.Id);

        Log.Information("TeamCity configuration under source control setup complete!");
    }
}