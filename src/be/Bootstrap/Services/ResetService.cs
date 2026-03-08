using Bootstrap.Services.GitLab;
using Bootstrap.Services.TeamCity;
using Serilog;

namespace Bootstrap.Services;

public class ResetService
{
    private const string TestGroupName = "Test Group";
    private const string CILabTeamCityProjectId = "CILab";

    private readonly GitLabService _gitlabService;
    private readonly TeamCityService _teamCityService;
    private readonly TeamCityVersionedSettingsService _teamCityVersionedSettingsService;

    public ResetService(
        GitLabService gitLabService,
        TeamCityService teamCityService,
        TeamCityVersionedSettingsService teamCityVersionedSettingsService)
    {
        _gitlabService = gitLabService;
        _teamCityService = teamCityService;
        _teamCityVersionedSettingsService = teamCityVersionedSettingsService;
    }

    /// <summary>
    /// Deletes all CI Lab projects from GitLab and TeamCity, leaving the services
    /// themselves configured and ready for a fresh project setup run.
    /// </summary>
    public async Task Execute()
    {
        Log.Information("Starting CI Lab reset: deleting all projects from GitLab and TeamCity...");

        // The CILab project in TeamCity is read-only while versioned settings are enabled.
        // Disable them first so the project can be deleted via the REST API.
        Log.Information("Disabling TeamCity versioned settings to allow project deletion...");
        await _teamCityVersionedSettingsService.DisableVersionedSettings();

        // Delete the CILab project from TeamCity. It will be recreated by ProjectSetupService
        // when it pushes updated Kotlin DSL and re-enables versioned settings.
        // The TeamCityConfig repo is intentionally not deleted: when versioned settings are
        // re-enabled, TeamCity will overwrite it with current settings (without CILab),
        // and SetupLabBuilds will then add CILab back with fresh configuration.
        await _teamCityService.DeleteProject(CILabTeamCityProjectId);

        // Delete the test projects group (cascades to primary-1..3 and secondary-1..4).
        await _gitlabService.DeleteGroup(TestGroupName);

        // GitLab group deletion is asynchronous. Wait until it is confirmed gone
        // before returning so that the subsequent project setup does not collide
        // with a deletion still in progress.
        await _gitlabService.WaitForGroupDeletion(TestGroupName);

        Log.Information("CI Lab reset complete. Ready for fresh project setup.");
    }
}
