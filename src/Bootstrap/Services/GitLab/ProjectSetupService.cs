using Bootstrap.Services.TeamCity;
using Serilog;

namespace Bootstrap.Services.Gitlab;

public class ProjectSetupService
{
    private readonly GitlabService _gitlabService;
    private readonly TeamCityService _teamCityService;
    private readonly string _gitlabUrl;
    private readonly string _gitlabToken;
    private readonly string _gitlabInternalUrl;

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
        Log.Information("Setting up GitLab test projects...");

        var gitlabProjectService = _gitlabService;

        // Create the test group
        var testGroup = await gitlabProjectService.CreateGroup("Test Group");

        foreach (var i in Enumerable.Range(1, 3))
        {
            var projectName = $"top-level-project-{i}";
            await gitlabProjectService.CreateTopLevelProject(projectName, testGroup.Id);
        }

        foreach (var i in Enumerable.Range(1, 4))
        {
            var projectName = $"sub-project-{i}";
            await gitlabProjectService.CreateRegularProject(projectName, testGroup.Id);
        }

        Log.Information("GitLab test projects ready");

        // Set up TeamCity configuration under source control
        await SetupTeamCityConfigRepo();
    }

    private async Task SetupTeamCityConfigRepo()
    {
        Log.Information("Setting up TeamCity configuration under source control...");

        // Step 1: Create the TeamCityConfig repository in GitLab
        var configProject = await _gitlabService.CreateProject("TeamCityConfig");
        Log.Information($"TeamCityConfig project ID: {configProject.Id}");

        // Step 2: Ensure the repo has an initial commit (required for TeamCity VCS root)
        await EnsureRepoHasInitialCommit(configProject);

        // Step 3: Create a VCS root in TeamCity for this repository
        // TeamCity needs to use the internal Docker network URL to access GitLab
        var internalGitUrl = configProject.HttpUrlToRepo.Replace(_gitlabUrl, _gitlabInternalUrl);
        Log.Information($"Using internal Git URL for TeamCity: {internalGitUrl}");

        var (vcsRootId, wasUpdated) = await _teamCityService.CreateOrUpdateVcsRoot(
            vcsRootName: "TeamCityConfig",
            gitUrl: internalGitUrl,
            branch: "master",
            username: "root",
            password: _gitlabToken);

        // Step 4: Enable versioned settings for the Root project with Kotlin format
        // Force reconfigure if VCS root was updated to ensure proper setup
        await _teamCityService.EnableVersionedSettings(
            vcsRootId: vcsRootId,
            settingsFormat: "kotlin",
            allowUiEditing: false,
            forceReconfigure: wasUpdated);

        // Step 5: Trigger TeamCity to commit current settings to VCS
        await _teamCityService.CommitCurrentSettingsToVcs("_Root");

        // Step 6: Wait and verify that settings.kts appears in the GitLab repo
        var settingsAppeared = await _teamCityService.WaitForSettingsInRepo(
            gitlabUrl: _gitlabUrl,
            gitlabToken: _gitlabToken,
            projectId: configProject.Id,
            timeoutSeconds: 120);

        if (settingsAppeared)
        {
            Log.Information("TeamCity configuration under source control setup complete!");
        }
        else
        {
            Log.Warning("TeamCity settings.kts file did not appear in the expected time.");
            Log.Warning("Manual verification may be required.");
        }
    }

    private async Task EnsureRepoHasInitialCommit(Entities.Gitlab.GitlabProject project)
    {
        // Check if the project already has commits
        var hasCommits = await _gitlabService.CheckProjectHasCommitsPublic(project.Id);
        if (hasCommits)
        {
            Log.Information($"Project '{project.Name}' already has commits");
            return;
        }

        Log.Information($"Creating initial commit for '{project.Name}'...");

        // Create a simple README file via the GitLab API
        await _gitlabService.CreateFileInRepo(
            projectId: project.Id,
            filePath: "README.md",
            content: "# TeamCity Configuration\n\nThis repository stores TeamCity versioned settings.\n",
            commitMessage: "Initial commit - TeamCity configuration repository",
            branch: "master");

        Log.Information($"Initial commit created for '{project.Name}'");
    }
}
