using Serilog;

namespace Bootstrap.Services.Gitlab;

public class ProjectSetupService
{
    private readonly GitlabService _gitlabService;

    public ProjectSetupService(GitlabService gitlabService)
    {
        _gitlabService = gitlabService;
    }

    public async Task<bool> Execute()
    {
        Log.Information("Setting up GitLab test projects...");

        var gitlabProjectService = _gitlabService;

        // Create the test group
        var testGroup = await gitlabProjectService.CreateGroup("Test Group");

        foreach (var i in Enumerable.Range(1, 3))
        {
            var projectName = $"top-level-project-{i}";
            var created = await gitlabProjectService.CreateTopLevelProject(projectName, testGroup.Id);
            if (!created)
            {
                Log.Error($"Failed to create GitLab project: {projectName}");
                return false;
            }
        }

        foreach (var i in Enumerable.Range(1, 4))
        {
            var projectName = $"sub-project-{i}";
            var created = await gitlabProjectService.CreateSubProject(projectName, testGroup.Id);
            if (!created)
            {
                Log.Error($"Failed to create GitLab sub-project: {projectName}");
                return false;
            }
        }

        Log.Information("GitLab test projects ready");
        return true;
    }
}
