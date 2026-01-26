using Serilog;

namespace Bootstrap.Services.Gitlab;

public class ProjectSetupService
{
    private readonly GitlabService _gitlabService;

    public ProjectSetupService(GitlabService gitlabService)
    {
        _gitlabService = gitlabService;
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
    }
}
