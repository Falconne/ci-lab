using Bootstrap.Services.TeamCity;
using Bootstrap.Services.Gitlab;
using LibGit2Sharp;
using Serilog;
using System.Text;

namespace Bootstrap.Services;

public class ProjectSetupService
{
    private readonly string _gitlabInternalURL;

    private readonly GitlabService _gitlabService;

    private readonly string _gitlabToken;

    private readonly string _gitlabURL;

    private readonly TeamCityService _teamCityService;

    // Store project info for reuse
    private readonly List<string> _primaryRepos = new();
    private readonly List<string> _secondaryRepos = new();

    public ProjectSetupService(
        GitlabService gitlabService,
        TeamCityService teamCityService,
        string gitlabURL,
        string gitlabToken,
        string? gitlabInternalURL = null)
    {
        _gitlabService = gitlabService;
        _teamCityService = teamCityService;
        _gitlabURL = gitlabURL;
        _gitlabToken = gitlabToken;
        // TeamCity runs inside Docker and needs to access GitLab via its internal network hostname
        _gitlabInternalURL = gitlabInternalURL ?? "http://gitlab:8081";
    }

    public async Task Execute()
    {
        await SetupTestRepos();

        await SetupTeamCityConfigRepo();

        await SetupLabBuilds();
    }

    private async Task SetupTestRepos()
    {
        Log.Information("Setting up GitLab test projects...");

        // Create the test group
        var testGroup = await _gitlabService.CreateGroup("Test Group");

        // Create some primary repos
        foreach (var i in Enumerable.Range(1, 3))
        {
            var projectName = $"primary-{i}";
            await _gitlabService.CreateTopLevelProject(projectName, testGroup.Id);
            _primaryRepos.Add(projectName);
        }

        // Create some secondary repos
        foreach (var i in Enumerable.Range(1, 4))
        {
            var projectName = $"secondary-{i}";
            await _gitlabService.CreateRegularProject(projectName, testGroup.Id);
            _secondaryRepos.Add(projectName);
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
        var internalGitURL = configProject.HttpURLToRepo.Replace(_gitlabURL, _gitlabInternalURL);
        Log.Information($"Using internal Git URL for TeamCity: {internalGitURL}");

        var (vcsRootId, wasUpdated) = await _teamCityService.CreateOrUpdateVCSRootViaAPI(
            "TeamCityConfig",
            internalGitURL,
            "main",
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
        await _teamCityService.CommitCurrentSettingsToVCS();

        // Wait for settings.kts to appear in the GitLab repo (throws on failure)
        await _teamCityService.WaitForSettingsInRepo(
            _gitlabURL,
            _gitlabToken,
            configProject.Id);

        Log.Information("TeamCity configuration under source control setup complete!");
    }

    private async Task SetupLabBuilds()
    {
        Log.Information("Setting up CI Lab builds in TeamCity configuration...");

        // Get the TeamCityConfig project info
        var configProject = await GetTeamCityConfigProject();
        if (configProject == null)
        {
            throw new InvalidOperationException("TeamCityConfig project not found in GitLab");
        }

        var repoURL = configProject.HttpURLToRepo;
        var authenticatedURL = repoURL.Replace("http://", $"http://root:{_gitlabToken}@");

        // Clone the repository to a temp directory
        var tempDir = Path.Combine(Path.GetTempPath(), $"tc-config-{Guid.NewGuid():N}");
        try
        {
            Log.Information($"Cloning TeamCityConfig repository to {tempDir}...");
            Repository.Clone(authenticatedURL, tempDir, new CloneOptions
            {
                BranchName = "main"
            });

            using var repo = new Repository(tempDir);

            // Read the existing settings.kts
            var settingsPath = Path.Combine(tempDir, ".teamcity", "settings.kts");
            if (!File.Exists(settingsPath))
            {
                throw new InvalidOperationException("settings.kts not found in TeamCityConfig repository");
            }

            var existingContent = await File.ReadAllTextAsync(settingsPath);

            // Check if CI Lab is already configured
            if (existingContent.Contains("CiLab") || existingContent.Contains("CI Lab"))
            {
                Log.Information("CI Lab project already exists in settings.kts");
            }
            else
            {
                // Generate the CI Lab Kotlin configuration and append to settings.kts
                var ciLabKotlin = GenerateCiLabKotlin();
                var modifiedContent = ModifyRootSettings(existingContent, ciLabKotlin);

                await File.WriteAllTextAsync(settingsPath, modifiedContent);
                Log.Information("Updated settings.kts with CI Lab configuration");

                // Stage and commit
                Commands.Stage(repo, "*");

                var signature = new Signature("CI Lab Bootstrap", "bootstrap@cilab.local", DateTimeOffset.Now);
                repo.Commit("Add CI Lab project with builds", signature, signature);

                // Push
                var remote = repo.Network.Remotes["origin"];
                var pushOptions = new PushOptions
                {
                    CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
                    {
                        Username = "root",
                        Password = _gitlabToken
                    }
                };

                repo.Network.Push(remote, "refs/heads/main:refs/heads/main", pushOptions);
                Log.Information("Pushed CI Lab configuration to TeamCityConfig repository");
            }
        }
        finally
        {
            // Clean up temp directory
            try
            {
                if (Directory.Exists(tempDir))
                {
                    // Need to remove read-only attributes from git files
                    SetAttributesNormal(new DirectoryInfo(tempDir));
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not clean up temp directory '{tempDir}': {ex.Message}");
            }
        }

        // Check for Kotlin compilation errors after pushing changes
        await Task.Delay(3000); // Give TeamCity a moment to process the changes
        await _teamCityService.CheckForKotlinErrors();

        // Wait for TeamCity to pick up the changes and verify
        await VerifyLabBuildsCreated();

        // Trigger builds and verify success
        await TriggerAndVerifyBuilds();

        Log.Information("CI Lab builds setup complete!");
    }

    private async Task<Entities.Gitlab.GitlabProject?> GetTeamCityConfigProject()
    {
        // Search for the project
        var project = await _gitlabService.CreateProject("TeamCityConfig");
        return project;
    }

    private string ModifyRootSettings(string existingContent, string ciLabContent)
    {
        // We need to add the CI Lab subproject to the existing settings.kts
        // First, add the necessary imports if not present
        // Then add template reference and subProject reference inside the root project block
        // Finally, append all the CI Lab definitions and template

        var sb = new StringBuilder();
        var lines = existingContent.Split('\n');
        var inRootProject = false;
        var rootProjectBraceCount = 0;
        var insertedSubProject = false;
        var addedImports = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmedLine = line.Trim();

            // Add imports after the existing imports section
            if (!addedImports && trimmedLine.StartsWith("import ") && i + 1 < lines.Length)
            {
                var nextLine = lines[i + 1].Trim();
                // Check if next line is not an import (end of import section)
                if (!nextLine.StartsWith("import "))
                {
                    sb.AppendLine(line);
                    // Add our additional imports
                    sb.AppendLine("import jetbrains.buildServer.configs.kotlin.buildSteps.script");
                    sb.AppendLine("import jetbrains.buildServer.configs.kotlin.vcs.GitVcsRoot");
                    sb.AppendLine("import jetbrains.buildServer.configs.kotlin.buildFeatures.commitStatusPublisher");
                    addedImports = true;
                    continue;
                }
            }

            // Track when we enter the root project block
            if (!inRootProject && (trimmedLine.StartsWith("project {") || trimmedLine == "project{"))
            {
                inRootProject = true;
                rootProjectBraceCount = 1;
                sb.AppendLine(line);
                continue;
            }

            if (inRootProject && !insertedSubProject)
            {
                // Count braces to find when we exit the project block
                rootProjectBraceCount += line.Count(c => c == '{');
                rootProjectBraceCount -= line.Count(c => c == '}');

                // Insert template and subProject references just before closing the root project
                if (rootProjectBraceCount == 0)
                {
                    // Add params block with gitlab.token at Root level for template access
                    sb.AppendLine();
                    sb.AppendLine("    params {");
                    sb.AppendLine($@"        password(""gitlab.token"", ""{_gitlabToken}"", display = ParameterDisplay.HIDDEN)");
                    sb.AppendLine("    }");
                    sb.AppendLine();
                    // Insert template and subProject before the closing brace
                    sb.AppendLine("    template(GitlabPipelinePublishing)");
                    sb.AppendLine("    subProject(CiLab)");
                    insertedSubProject = true;
                    inRootProject = false;
                }
            }

            sb.AppendLine(line);
        }

        // Append the GitLab Pipeline Publishing template definition
        sb.AppendLine();
        sb.Append(GenerateGitlabPipelinePublishingTemplate());

        // Append the CI Lab project definition at the end
        sb.AppendLine();
        sb.AppendLine(ciLabContent);

        return sb.ToString();
    }

    private string GenerateGitlabPipelinePublishingTemplate()
    {
        var sb = new StringBuilder();

        // GitLab Pipeline Publishing template definition at Root level
        sb.AppendLine("object GitlabPipelinePublishing : Template({");
        sb.AppendLine(@"    id(""GitlabPipelinePublishing"")");
        sb.AppendLine(@"    name = ""GitLab Pipeline Publishing""");
        sb.AppendLine();
        sb.AppendLine("    features {");
        sb.AppendLine("        commitStatusPublisher {");
        sb.AppendLine("            publisher = gitlab {");
        sb.AppendLine($@"                gitlabApiUrl = ""{_gitlabInternalURL}/api/v4""");
        sb.AppendLine(@"                accessToken = ""%gitlab.token%""");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("})");
        sb.AppendLine();

        return sb.ToString();
    }

    private string GenerateCiLabKotlin()
    {
        var sb = new StringBuilder();

        // CI Lab project definition as an object
        sb.AppendLine("object CiLab : Project({");
        sb.AppendLine(@"    id(""CiLab"")");
        sb.AppendLine(@"    name = ""CI Lab""");
        sb.AppendLine();

        // Register VCS roots for all repos
        foreach (var repo in _primaryRepos)
        {
            var vcsId = GetVCSRootId(repo);
            sb.AppendLine($"    vcsRoot({vcsId})");
        }
        foreach (var repo in _secondaryRepos)
        {
            var vcsId = GetVCSRootId(repo);
            sb.AppendLine($"    vcsRoot({vcsId})");
        }
        sb.AppendLine();

        // Register build types
        for (var i = 0; i < _primaryRepos.Count; i++)
        {
            var buildId = $"CiLabBuild{i + 1}";
            sb.AppendLine($"    buildType({buildId})");
        }
        sb.AppendLine("})");
        sb.AppendLine();

        // Generate VCS roots for all repos
        foreach (var repo in _primaryRepos.Concat(_secondaryRepos))
        {
            sb.Append(GenerateVCSRoot(repo));
        }

        // Generate build types
        // Build 1: primary-1 + all 4 secondary repos
        sb.Append(GenerateBuildType(1, _primaryRepos[0], _secondaryRepos.ToArray()));

        // Build 2: primary-2 + first 2 secondary repos
        sb.Append(GenerateBuildType(2, _primaryRepos[1], _secondaryRepos.Take(2).ToArray()));

        // Build 3: primary-3 + only 4th secondary repo
        sb.Append(GenerateBuildType(3, _primaryRepos[2], new[] { _secondaryRepos[3] }));

        return sb.ToString();
    }

    private string GetVCSRootId(string repoName)
    {
        var sanitized = repoName.Replace("-", "_");
        return $"CiLabVCS_{sanitized}";
    }

    private string GenerateVCSRoot(string repoName)
    {
        var vcsId = GetVCSRootId(repoName);
        var internalURL = $"{_gitlabInternalURL}/test-group/{repoName}.git";

        // Reference the project parameter for the password
        // Set polling interval to 4 seconds for fast feedback
        return $@"object {vcsId} : GitVcsRoot({{
    id(""{vcsId}"")
    name = ""{repoName}""
    url = ""{internalURL}""
    branch = ""refs/heads/main""
    branchSpec = ""+:refs/heads/*""
    pollInterval = 4
    authMethod = password {{
        userName = ""root""
        password = ""%gitlab.token%""
    }}
}})

";
    }

    private string GenerateBuildType(int buildNumber, string primaryRepo, string[] secondaryRepos)
    {
        var buildId = $"CiLabBuild{buildNumber}";
        var primaryVcsId = GetVCSRootId(primaryRepo);

        var sb = new StringBuilder();
        sb.AppendLine($"object {buildId} : BuildType({{");
        sb.AppendLine($@"    id(""{buildId}"")");
        sb.AppendLine($@"    name = ""Build {buildNumber} - {primaryRepo}""");
        sb.AppendLine();

        // Apply the GitLab Pipeline Publishing template
        sb.AppendLine("    templates(GitlabPipelinePublishing)");
        sb.AppendLine();

        // Build 1 depends on Builds 2 and 3 (snapshot dependencies)
        if (buildNumber == 1)
        {
            sb.AppendLine("    dependencies {");
            sb.AppendLine("        snapshot(CiLabBuild2) {");
            sb.AppendLine("            onDependencyFailure = FailureAction.FAIL_TO_START");
            sb.AppendLine("        }");
            sb.AppendLine("        snapshot(CiLabBuild3) {");
            sb.AppendLine("            onDependencyFailure = FailureAction.FAIL_TO_START");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("    vcs {");
        sb.AppendLine($@"        root({primaryVcsId}, ""+:. => primary"")");

        foreach (var secondary in secondaryRepos)
        {
            var secondaryVcsId = GetVCSRootId(secondary);
            sb.AppendLine($@"        root({secondaryVcsId}, ""+:. => {secondary}"")");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    steps {");
        sb.AppendLine("        script {");
        sb.AppendLine(@"            name = ""Run build script""");
        sb.AppendLine(@"            scriptContent = """"""");
        sb.AppendLine("                #!/bin/bash");
        sb.AppendLine("                set -e");
        sb.AppendLine($@"                echo ""Running build for {primaryRepo}""");
        sb.AppendLine("                cd primary");
        sb.AppendLine("                chmod +x build.sh");
        sb.AppendLine("                ./build.sh");
        sb.AppendLine(@"            """""".trimIndent()");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("})");
        sb.AppendLine();

        return sb.ToString();
    }

    private async Task VerifyLabBuildsCreated()
    {
        Log.Information("Verifying CI Lab project was created in TeamCity...");

        // Poll for the CI Lab project to appear (TeamCity needs time to compile Kotlin DSL)
        // The project ID in TeamCity will be "Root_CiLab" since it's a subproject of _Root
        var projectFound = await _teamCityService.WaitForProject("CiLab", 60);
        if (!projectFound)
        {
            throw new InvalidOperationException(
                "CI Lab project did not appear in TeamCity within 60 seconds - Kotlin DSL may have errors");
        }

        // Verify build types exist
        var buildTypes = await _teamCityService.GetBuildTypes("CiLab");
        Log.Information($"Found {buildTypes.Count} build types in CI Lab project");

        if (buildTypes.Count < 3)
        {
            throw new InvalidOperationException($"Expected 3 build types in CI Lab project, found {buildTypes.Count}");
        }

        var expectedBuilds = new[] { "CiLabBuild1", "CiLabBuild2", "CiLabBuild3" };
        foreach (var expectedBuild in expectedBuilds)
        {
            if (!buildTypes.Any(bt => bt.id.Contains(expectedBuild)))
            {
                throw new InvalidOperationException($"Expected build type '{expectedBuild}' not found in CI Lab project");
            }
        }

        // Verify VCS roots exist
        var vcsRoots = await _teamCityService.GetVCSRoots("CiLab");
        Log.Information($"Found {vcsRoots.Count} VCS roots in CI Lab project");

        var expectedVCSCount = _primaryRepos.Count + _secondaryRepos.Count;
        if (vcsRoots.Count < expectedVCSCount)
        {
            Log.Warning($"Expected {expectedVCSCount} VCS roots, found {vcsRoots.Count} - some may be in a different format");
        }

        Log.Information("CI Lab project verification passed!");
    }

    private async Task TriggerAndVerifyBuilds()
    {
        Log.Information("Triggering CI Lab builds...");

        var buildTypes = await _teamCityService.GetBuildTypes("CiLab");
        var buildIds = new List<string>();

        foreach (var (btId, btName) in buildTypes)
        {
            Log.Information($"Triggering build: {btName}");
            var buildId = await _teamCityService.TriggerBuild(btId);
            buildIds.Add(buildId);
        }

        Log.Information($"Triggered {buildIds.Count} builds, waiting for completion...");

        // Wait for all builds to complete (increased timeout since build.sh has sleep)
        foreach (var buildId in buildIds)
        {
            var success = await _teamCityService.WaitForBuildCompletion(buildId, 600);
            if (!success)
            {
                throw new InvalidOperationException($"Build {buildId} did not complete successfully");
            }
        }

        Log.Information("All CI Lab builds completed successfully!");
    }

    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
        {
            SetAttributesNormal(subDir);
            subDir.Attributes = FileAttributes.Normal;
        }
        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }
    }
}
