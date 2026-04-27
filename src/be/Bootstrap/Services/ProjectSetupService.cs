using System.Text;
using Bootstrap.Entities.GitLab;
using Bootstrap.Services.GitLab;
using Bootstrap.Services.TeamCity;
using LibGit2Sharp;
using Serilog;

namespace Bootstrap.Services;

public class ProjectSetupService
{
    private readonly EnvFileService _envFileService;

    private readonly string _gitlabInternalURL;

    private readonly GitLabService _gitlabService;

    private readonly string _gitlabToken;

    private readonly string _gitlabURL;

    // Store project info (name -> project ID) for reuse
    private readonly Dictionary<string, int> _primaryRepos = new();

    private readonly Dictionary<string, int> _secondaryRepos = new();

    // Project ID for the GitLab CI-only test repo (no TeamCity VCS root)
    private int _ciTestProjectId;

    private readonly TeamCityService _teamCityService;

    private readonly TeamCityVCSRootService _teamCityVCSRootService;

    private readonly TeamCityVersionedSettingsService _teamCityVersionedSettingsService;

    // Store user PATs for creating MRs/approvals as specific users
    private readonly Dictionary<string, string> _userTokens = new();

    public ProjectSetupService(
        GitLabService gitLabService,
        TeamCityService teamCityService,
        TeamCityVCSRootService teamCityVCSRootService,
        TeamCityVersionedSettingsService teamCityVersionedSettingsService,
        EnvFileService envFileService,
        string gitlabURL,
        string gitlabToken,
        string? gitlabInternalURL = null)
    {
        _gitlabService = gitLabService;
        _teamCityService = teamCityService;
        _teamCityVCSRootService = teamCityVCSRootService;
        _teamCityVersionedSettingsService = teamCityVersionedSettingsService;
        _envFileService = envFileService;
        _gitlabURL = gitlabURL;
        _gitlabToken = gitlabToken;
        // TeamCity runs inside Docker and needs to access GitLab via its internal network hostname
        _gitlabInternalURL = gitlabInternalURL ?? "http://gitlab:8081";
    }

    public async Task Execute()
    {
        await SetupTestAccounts();

        await SetupTestRepos();

        await SetupOAuthApplication();

        await SetupTeamCityConfigRepo();

        await SetupLabBuilds();

        await SetupTestBranchData();
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
            var project = await _gitlabService.CreateTopLevelProject(projectName, testGroup.Id);
            _primaryRepos[projectName] = project.Id;

            // Add Bob Builder as owner
            await _gitlabService.AddProjectMember(project.Id, "b.builder");

            // Configure merge request settings
            await _gitlabService.ConfigureProjectMergeRequestSettings(project.Id);
        }

        // Create some secondary repos
        foreach (var i in Enumerable.Range(1, 4))
        {
            var projectName = $"secondary-{i}";
            var project = await _gitlabService.CreateRegularProject(projectName, testGroup.Id);
            _secondaryRepos[projectName] = project.Id;

            // Add Bob Builder as owner
            await _gitlabService.AddProjectMember(project.Id, "b.builder");

            // Configure merge request settings
            await _gitlabService.ConfigureProjectMergeRequestSettings(project.Id);
        }

        // Add test users (test1, test2, test3) as Developers to the test group
        Log.Information("Adding test users as Developers to the test group...");
        for (var i = 1; i <= 3; i++)
        {
            var username = $"test{i}";
            await _gitlabService.AddGroupMember(testGroup.Id, username);
        }

        // Create a GitLab CI-only test repo for pipeline filtering tests.
        // NOT added to _primaryRepos or _secondaryRepos so TeamCity does not generate
        // a VCS root for it — GitLab CI is the sole pipeline source for this project.
        const string gitlabCiYaml = """
                                    manual-deploy:
                                      script:
                                        - echo "Manual deployment"
                                      when: manual
                                    """;
        var ciTestProject = await _gitlabService.CreateProjectWithCIConfig(
            "gitlab-ci-test", testGroup.Id, gitlabCiYaml);
        _ciTestProjectId = ciTestProject.Id;
        await _gitlabService.AddProjectMember(ciTestProject.Id, "b.builder");
        await _gitlabService.ConfigureProjectMergeRequestSettings(ciTestProject.Id);

        Log.Information("GitLab test projects ready");
    }

    private async Task SetupOAuthApplication()
    {
        Log.Information("Setting up GitLab OAuth application for Mergician...");

        // Register Mergician as an OAuth application in GitLab
        // Use multiple redirect URIs for backend (port 5000) and native Vue dev server (port 5173)
        var redirectUri = "http://localhost:5000/api/auth/callback\nhttp://localhost:5173/api/auth/callback";
        var oauthApp = await _gitlabService.CreateOAuthApplication(
            "Mergician",
            redirectUri);

        var clientId = oauthApp.ApplicationId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            Log.Information(
                "OAuth application response did not include client ID; reusing existing value from .env");

            clientId = _envFileService.GetValue("MERGICIAN_GITLAB_OAUTH_CLIENT_ID") ?? "";
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            Log.Error("No GitLab OAuth client ID is available after OAuth application setup");
            throw new InvalidOperationException(
                "No GitLab OAuth client ID is available. Ensure the OAuth app exists and MERGICIAN_GITLAB_OAUTH_CLIENT_ID is set in .env.");
        }

        var clientSecret = oauthApp.Secret;
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            Log.Information(
                "OAuth application secret was not returned by GitLab; reusing existing value from .env");

            clientSecret = _envFileService.GetValue("MERGICIAN_GITLAB_OAUTH_CLIENT_SECRET") ?? "";
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            Log.Error("No GitLab OAuth client secret is available after OAuth application setup");
            throw new InvalidOperationException(
                "No GitLab OAuth client secret is available. Existing GitLab OAuth apps do not return secrets from the list API, so MERGICIAN_GITLAB_OAUTH_CLIENT_SECRET must already exist in .env.");
        }

        // Save OAuth credentials to .env for Mergician to read
        _envFileService.SaveOrUpdateEnvFile("MERGICIAN_GITLAB_OAUTH_CLIENT_ID", clientId);
        _envFileService.SaveOrUpdateEnvFile("MERGICIAN_GITLAB_OAUTH_CLIENT_SECRET", clientSecret);

        Log.Information("GitLab OAuth application for Mergician configured");
    }

    private async Task SetupTeamCityConfigRepo()
    {
        Log.Information("Setting up TeamCity configuration under source control...");

        // Create the TeamCityConfig repository in GitLab
        var configProject = await _gitlabService.CreateProject("TeamCityConfig");
        Log.Information($"TeamCityConfig project ID: {configProject.Id}");

        // Add Bob Builder as owner
        await _gitlabService.AddProjectMember(configProject.Id, "b.builder");

        // Configure merge request settings
        await _gitlabService.ConfigureProjectMergeRequestSettings(configProject.Id);

        // Create a VCS root in TeamCity for this repository
        // TeamCity needs to use the internal Docker network URL to access GitLab
        var internalGitURL = configProject.HttpURLToRepo.Replace(_gitlabURL, _gitlabInternalURL);
        Log.Information($"Using internal Git URL for TeamCity: {internalGitURL}");

        var (vcsRootId, wasUpdated) = await _teamCityVCSRootService.CreateOrUpdateVCSRootViaAPI(
            "TeamCityConfig",
            internalGitURL,
            "main",
            "root",
            _gitlabToken);

        // Enable versioned settings for the Root project with Kotlin format
        // Force reconfigure if VCS root was updated to ensure proper setup
        await _teamCityVersionedSettingsService.EnableVersionedSettings(
            vcsRootId,
            "kotlin",
            false,
            wasUpdated);

        // Trigger TeamCity to commit current settings to VCS
        await _teamCityVersionedSettingsService.CommitCurrentSettingsToVCS();

        // Wait for settings.kts to appear in the GitLab repo (throws on failure)
        await TeamCityVersionedSettingsService.WaitForSettingsInRepo(
            _gitlabURL,
            _gitlabToken,
            configProject.Id);

        Log.Information("TeamCity configuration under source control setup complete!");
    }

    private async Task SetupLabBuilds()
    {
        Log.Information("Setting up CI Lab builds in TeamCity configuration...");

        // Get the TeamCityConfig project info
        var configProject = await GetOrCreateTeamCityConfigProject();
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
            Repository.Clone(authenticatedURL, tempDir, new CloneOptions { BranchName = "main" });

            using var repo = new Repository(tempDir);

            // Read the existing settings.kts
            var settingsPath = Path.Combine(tempDir, ".teamcity", "settings.kts");
            if (!File.Exists(settingsPath))
            {
                throw new InvalidOperationException("settings.kts not found in TeamCityConfig repository");
            }

            var existingContent = await File.ReadAllTextAsync(settingsPath);

            // Check if CI Lab is already configured
            if (existingContent.Contains("CILab") || existingContent.Contains("CI Lab"))
            {
                Log.Information("CI Lab project already exists in settings.kts");
            }
            else
            {
                // Generate the CI Lab Kotlin configuration and append to settings.kts
                var ciLabKotlin = GenerateCILabKotlin();
                var modifiedContent = ModifyRootSettings(existingContent, ciLabKotlin);

                await File.WriteAllTextAsync(settingsPath, modifiedContent);
                Log.Information("Updated settings.kts with CI Lab configuration");

                // Stage and commit
                Commands.Stage(repo, "*");

                var signature = new Signature(
                    "CI Lab Bootstrap",
                    "bootstrap@CILab.local",
                    DateTimeOffset.Now);

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
        await _teamCityVersionedSettingsService.CheckForKotlinErrors();

        // Wait for TeamCity to pick up the changes and verify
        await VerifyLabBuildsCreated();

        // Trigger builds and verify success
        //await TriggerAndVerifyBuilds();

        Log.Information("CI Lab builds setup complete!");
    }

    private async Task<GitLabProject?> GetOrCreateTeamCityConfigProject()
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
                    // Add our additional imports, skipping any that are already present
                    var importsToAdd = new[]
                    {
                        "import jetbrains.buildServer.configs.kotlin.buildSteps.script",
                        "import jetbrains.buildServer.configs.kotlin.vcs.GitVcsRoot",
                        "import jetbrains.buildServer.configs.kotlin.buildFeatures.commitStatusPublisher",
                        "import jetbrains.buildServer.configs.kotlin.triggers.vcs"
                    };

                    foreach (var import in importsToAdd)
                    {
                        if (!existingContent.Contains(import))
                        {
                            sb.AppendLine(import);
                        }
                    }

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
                    // Only add params block if gitlab.token is not already defined
                    if (!existingContent.Contains("gitlab.token"))
                    {
                        sb.AppendLine();
                        sb.AppendLine("    params {");
                        sb.AppendLine(
                            $@"        password(""gitlab.token"", ""{_gitlabToken}"", display = ParameterDisplay.HIDDEN)");

                        sb.AppendLine("    }");
                    }

                    sb.AppendLine();

                    // Only add template(GitlabPipelinePublishing) if not already present
                    if (!existingContent.Contains("template(GitlabPipelinePublishing)"))
                    {
                        sb.AppendLine("    template(GitlabPipelinePublishing)");
                    }

                    // subProject(CILab) is the reason we're here - always add it
                    sb.AppendLine("    subProject(CILab)");
                    insertedSubProject = true;
                    inRootProject = false;
                }
            }

            sb.AppendLine(line);
        }

        // Append the GitLab Pipeline Publishing template definition only if not already present
        if (!existingContent.Contains("object GitlabPipelinePublishing"))
        {
            sb.AppendLine();
            sb.Append(GenerateGitlabPipelinePublishingTemplate());
        }

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

    private string GenerateCILabKotlin()
    {
        var sb = new StringBuilder();

        // CI Lab project definition as an object
        sb.AppendLine("object CILab : Project({");
        sb.AppendLine(@"    id(""CILab"")");
        sb.AppendLine(@"    name = ""CI Lab""");
        sb.AppendLine();

        // Register VCS roots for all repos
        foreach (var repo in _primaryRepos.Keys)
        {
            var vcsId = GetVCSRootId(repo);
            sb.AppendLine($"    vcsRoot({vcsId})");
        }

        foreach (var repo in _secondaryRepos.Keys)
        {
            var vcsId = GetVCSRootId(repo);
            sb.AppendLine($"    vcsRoot({vcsId})");
        }

        sb.AppendLine();

        // Register build types
        for (var i = 0; i < _primaryRepos.Count; i++)
        {
            var buildId = $"CILabBuild{i + 1}";
            sb.AppendLine($"    buildType({buildId})");
        }

        var primaryRepoNames = _primaryRepos.Keys.ToList();
        var secondaryRepoNames = _secondaryRepos.Keys.ToList();
        sb.AppendLine("})");
        sb.AppendLine();

        // Generate VCS roots for all repos
        foreach (var repo in _primaryRepos.Keys.Concat(_secondaryRepos.Keys))
        {
            sb.Append(GenerateVCSRoot(repo));
        }

        // Generate build types
        // Build 1: primary-1 + all 4 secondary repos
        sb.Append(GenerateBuildType(1, primaryRepoNames[0], secondaryRepoNames.ToArray()));

        // Build 2: primary-2 + first 2 secondary repos
        sb.Append(GenerateBuildType(2, primaryRepoNames[1], secondaryRepoNames.Take(2).ToArray()));

        // Build 3: primary-3 + only 4th secondary repo
        sb.Append(GenerateBuildType(3, primaryRepoNames[2], new[] { secondaryRepoNames[3] }));

        return sb.ToString();
    }

    private string GetVCSRootId(string repoName)
    {
        var sanitized = repoName.Replace("-", "_");
        return $"CILabVCS_{sanitized}";
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
        var buildId = $"CILabBuild{buildNumber}";
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
            sb.AppendLine("        snapshot(CILabBuild2) {");
            sb.AppendLine("            onDependencyFailure = FailureAction.FAIL_TO_START");
            sb.AppendLine("        }");
            sb.AppendLine("        snapshot(CILabBuild3) {");
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
        sb.AppendLine();
        sb.AppendLine("    triggers {");
        sb.AppendLine("        vcs {");
        sb.AppendLine(@"            branchFilter = ""+:*""");
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
        // The project ID in TeamCity will be "Root_CILab" since it's a subproject of _Root
        var projectFound = await _teamCityService.WaitForProject("CILab");
        if (!projectFound)
        {
            throw new InvalidOperationException(
                "CI Lab project did not appear in TeamCity within 120 seconds - Kotlin DSL may have errors");
        }

        // Verify build types exist
        var buildTypes = await _teamCityService.GetBuildTypes("CILab");
        Log.Information($"Found {buildTypes.Count} build types in CI Lab project");

        if (buildTypes.Count < 3)
        {
            throw new InvalidOperationException(
                $"Expected 3 build types in CI Lab project, found {buildTypes.Count}");
        }

        var expectedBuilds = new[] { "CILabBuild1", "CILabBuild2", "CILabBuild3" };
        foreach (var expectedBuild in expectedBuilds)
        {
            if (!buildTypes.Any(bt => bt.id.Contains(expectedBuild)))
            {
                throw new InvalidOperationException(
                    $"Expected build type '{expectedBuild}' not found in CI Lab project");
            }
        }

        // Verify VCS roots exist
        var vcsRoots = await _teamCityVCSRootService.GetVCSRoots("CILab");
        Log.Information($"Found {vcsRoots.Count} VCS roots in CI Lab project");

        var expectedVCSCount = _primaryRepos.Count + _secondaryRepos.Count;
        if (vcsRoots.Count < expectedVCSCount)
        {
            throw new InvalidDataException(
                $"Expected {expectedVCSCount} VCS roots, found {vcsRoots.Count} - some may be in a different format");
        }

        Log.Information("CI Lab project verification passed!");
    }

    private async Task TriggerAndVerifyBuilds()
    {
        Log.Information("Triggering CI Lab builds...");

        var buildTypes = await _teamCityService.GetBuildTypes("CILab");
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

    private async Task SetupTestAccounts()
    {
        Log.Information("Creating Bob Builder account in GitLab...");
        const string password = "changeme123";

        // Create Bob Builder in GitLab
        await _gitlabService.CreateUser("b.builder", "Bob Builder", "b.builder@CILab.local", password);

        // Generate a service access token for b.builder with full permissions
        Log.Information("Generating service access token for b.builder...");
        var serviceToken = await _gitlabService.CreatePersonalAccessToken(
            "b.builder",
            "mergician-service",
            new[] { "api" });

        _envFileService.SaveOrUpdateEnvFile("GITLAB_SERVICE_TOKEN", serviceToken.Token);
        Log.Information("Service access token written to .env as GITLAB_SERVICE_TOKEN");

        Log.Information("Creating test accounts in TeamCity...");

        for (var i = 1; i <= 3; i++)
        {
            var username = $"test{i}";
            var name = $"Test Account {i}";
            var email = $"test{i}@CILab.local";

            await _gitlabService.CreateUser(username, name, email, password);
            await _teamCityService.CreateUser(username, name, email, password);

            // Create a PAT for each test user so we can create MRs/approvals as them
            var pat = await _gitlabService.CreatePersonalAccessToken(
                username,
                "bootstrap-test-data",
                ["api", "read_user", "read_api"]);

            _userTokens[username] = pat.Token;

            var envKey = $"GITLAB_TEST{i}_TOKEN";
            _envFileService.SaveOrUpdateEnvFile(envKey, pat.Token);
            Log.Information("Created PAT for '{Username}', written to .env as {EnvKey}", username, envKey);
        }

        Log.Information("TeamCity test accounts created");
    }

    /// <summary>
    ///     Creates deterministic branch/MR/approval test data for the dashboard.
    ///     Test data layout:
    ///     - test1 creates branch "feature/alpha" in primary-1 and secondary-1, with MRs.
    ///     test2 approves the MR in primary-1.
    ///     - test1 creates branch "feature/beta" in primary-2 only, with an MR (no approval).
    ///     - test2 creates branch "feature/gamma" in primary-1, secondary-1, secondary-2, with MRs in all.
    ///     test3 approves the MR in secondary-1.
    ///     - test3 creates branch "feature/delta" in secondary-3 only, no MR.
    /// </summary>
    private async Task SetupTestBranchData()
    {
        Log.Information("Setting up deterministic test branch data for the dashboard...");

        var allRepos = new Dictionary<string, int>(_primaryRepos);
        foreach (var (name, id) in _secondaryRepos)
        {
            allRepos[name] = id;
        }

        // Resolve project IDs by name
        int ProjectId(string name)
        {
            return allRepos[name];
        }

        // ── test1: feature/alpha in primary-1 and secondary-1 ──
        var test1Token = _userTokens["test1"];

        await CreateBranchWithCommit(ProjectId("primary-1"), "feature/alpha", "test1");
        await CreateBranchWithCommit(ProjectId("secondary-1"), "feature/alpha", "test1");

        var mrAlpha1 = await _gitlabService.CreateMergeRequest(
            ProjectId("primary-1"),
            "feature/alpha",
            "main",
            "Alpha changes in primary-1",
            test1Token);

        var mrAlpha2 = await _gitlabService.CreateMergeRequest(
            ProjectId("secondary-1"),
            "feature/alpha",
            "main",
            "Alpha changes in secondary-1",
            test1Token);

        // test2 approves alpha MR in primary-1
        await _gitlabService.ApproveMergeRequest(
            ProjectId("primary-1"),
            mrAlpha1.Iid,
            _userTokens["test2"]);

        // ── test1: feature/beta in primary-2 only, MR but no approval ──
        await CreateBranchWithCommit(ProjectId("primary-2"), "feature/beta", "test1");

        await _gitlabService.CreateMergeRequest(
            ProjectId("primary-2"),
            "feature/beta",
            "main",
            "Beta changes in primary-2",
            test1Token);

        // ── test2: feature/gamma in primary-1, secondary-1, secondary-2 ──
        var test2Token = _userTokens["test2"];

        await CreateBranchWithCommit(ProjectId("primary-1"), "feature/gamma", "test2");
        await CreateBranchWithCommit(ProjectId("secondary-1"), "feature/gamma", "test2");
        await CreateBranchWithCommit(ProjectId("secondary-2"), "feature/gamma", "test2");

        await _gitlabService.CreateMergeRequest(
            ProjectId("primary-1"),
            "feature/gamma",
            "main",
            "Gamma changes in primary-1",
            test2Token);

        var mrGammaSec1 = await _gitlabService.CreateMergeRequest(
            ProjectId("secondary-1"),
            "feature/gamma",
            "main",
            "Gamma changes in secondary-1",
            test2Token);

        await _gitlabService.CreateMergeRequest(
            ProjectId("secondary-2"),
            "feature/gamma",
            "main",
            "Gamma changes in secondary-2",
            test2Token);

        // test3 approves gamma MR in secondary-1
        await _gitlabService.ApproveMergeRequest(
            ProjectId("secondary-1"),
            mrGammaSec1.Iid,
            _userTokens["test3"]);

        // ── test3: feature/delta in secondary-3, no MR ──
        await CreateBranchWithCommit(ProjectId("secondary-3"), "feature/delta", "test3");

        // ── test1: feature/epsilon in secondary-4, with draft MR ──
        // Draft MRs are permanently Blocked in Mergician, providing a stable blocked group
        // with all branches having MRs for the auto-merge toggle test.
        await CreateBranchWithCommit(ProjectId("secondary-4"), "feature/epsilon", "test1");
        await _gitlabService.CreateMergeRequest(
            ProjectId("secondary-4"),
            "feature/epsilon",
            "main",
            "Epsilon changes in secondary-4",
            test1Token,
            draft: true);

        Log.Information("Test branch data setup complete!");
    }

    /// <summary>
    ///     Helper: creates a branch and a commit on it so the branch has changes vs main.
    ///     Uses the user's token so the commit and push event are attributed to them.
    /// </summary>
    private async Task CreateBranchWithCommit(int projectId, string branchName, string username)
    {
        await _gitlabService.CreateBranch(projectId, branchName);

        // Use the user's token to create the commit so it registers as their push event
        var userToken = _userTokens[username];
        await _gitlabService.CreateCommitOnBranchAsUser(
            projectId,
            branchName,
            $"changes/{branchName.Replace("/", "-")}.txt",
            $"Changes for {branchName} by {username} at {DateTime.UtcNow:O}",
            $"Add changes for {branchName}",
            userToken);
    }
}