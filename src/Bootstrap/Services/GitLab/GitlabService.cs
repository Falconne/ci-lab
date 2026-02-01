using Bootstrap.Entities.Gitlab;
using LibGit2Sharp;
using RestSharp;
using Serilog;
using System.Net;

namespace Bootstrap.Services.Gitlab;

public class GitlabService : IDisposable
{
    private readonly RestClient _client;

    private readonly string _token;

    public GitlabService(string gitlabURL, string token)
    {
        _token = token;
        _client = new RestClient(
            new RestClientOptions($"{gitlabURL.TrimEnd('/')}/api/v4")
            {
                ThrowOnAnyError = false,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                Timeout = TimeSpan.FromSeconds(30)
            });

        _client.AddDefaultHeader("PRIVATE-TOKEN", token);
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<GitlabGroup> CreateGroup(string groupName)
    {
        Log.Information($"Creating GitLab group '{groupName}'");

        var searchRequest = new RestRequest("groups")
            .AddQueryParameter("search", groupName);

        var searchResponse = await _client.ExecuteGetAsync<GitlabGroup[]>(searchRequest);

        if (searchResponse is { IsSuccessful: true, Data: not null })
        {
            foreach (var grp in searchResponse.Data)
            {
                if (grp.Name == groupName)
                {
                    Log.Information($"Group '{groupName}' already exists");
                    return grp;
                }
            }
        }

        var createRequest = new RestRequest("groups", Method.Post)
            .AddJsonBody(new { name = groupName, path = groupName.ToLower().Replace(" ", "-"), visibility = "public" });

        var createResponse = await _client.ExecutePostAsync<GitlabGroup>(createRequest);

        if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && createResponse.Data is not null)
        {
            Log.Information($"Group '{groupName}' created");
            return createResponse.Data;
        }

        Log.Error($"GitLab API error {(int)createResponse.StatusCode}: {createResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to create GitLab group '{groupName}': {(int)createResponse.StatusCode} {createResponse.StatusCode} - {createResponse.Content}");
    }

    public async Task<GitlabProject> CreateProject(string projectName, int? namespaceId = null)
    {
        Log.Information($"Creating Gitlab project '{projectName}'");

        var searchRequest = new RestRequest("projects")
            .AddQueryParameter("search", projectName);

        var searchResponse = await _client.ExecuteGetAsync<GitlabProject[]>(searchRequest);

        if (searchResponse is { IsSuccessful: true, Data: not null })
        {
            foreach (var proj in searchResponse.Data)
            {
                if (proj.Name == projectName)
                {
                    Log.Information($"Project '{projectName}' already exists");
                    return proj;
                }
            }
        }

        var requestBody = namespaceId.HasValue
            ? new { name = projectName, initialize_with_readme = false, namespace_id = namespaceId.Value }
            : (object)new { name = projectName, initialize_with_readme = false };

        var createRequest = new RestRequest("projects", Method.Post)
            .AddJsonBody(requestBody);

        var createResponse = await _client.ExecutePostAsync<GitlabProject>(createRequest);

        if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && createResponse.Data is not null)
        {
            Log.Information($"Project '{projectName}' created");
            return createResponse.Data;
        }

        Log.Error($"Gitlab API error {(int)createResponse.StatusCode}: {createResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to create Gitlab project '{projectName}': {(int)createResponse.StatusCode} {createResponse.StatusCode} - {createResponse.Content}");
    }

    public async Task AddProjectMember(int projectId, string username, int accessLevel = 50)
    {
        Log.Information($"Adding user '{username}' to project {projectId} with access level {accessLevel}");

        // First, get the user ID by username
        var userSearchRequest = new RestRequest("users")
            .AddQueryParameter("username", username);

        var userSearchResponse = await _client.ExecuteGetAsync<GitlabUser[]>(userSearchRequest);

        if (userSearchResponse is not { IsSuccessful: true, Data: not null } || userSearchResponse.Data.Length == 0)
        {
            Log.Error($"User '{username}' not found");
            throw new InvalidOperationException($"User '{username}' not found in GitLab");
        }

        var userId = userSearchResponse.Data[0].Id;

        // Check if user is already a member
        var checkRequest = new RestRequest($"projects/{projectId}/members/{userId}");
        var checkResponse = await _client.ExecuteGetAsync<GitlabProjectMember>(checkRequest);

        if (checkResponse.IsSuccessful && checkResponse.Data is not null)
        {
            // User is already a member, check if we need to update access level
            if (checkResponse.Data.AccessLevel == accessLevel)
            {
                Log.Information($"User '{username}' is already a member with correct access level");
                return;
            }

            Log.Information($"User '{username}' is already a member, updating access level");
            var updateRequest = new RestRequest($"projects/{projectId}/members/{userId}", Method.Put)
                .AddJsonBody(new { access_level = accessLevel });

            var updateResponse = await _client.ExecuteAsync<GitlabProjectMember>(updateRequest);

            if (updateResponse.StatusCode is HttpStatusCode.OK && updateResponse.Data is not null)
            {
                Log.Information($"Updated access level for user '{username}' to {accessLevel}");
                return;
            }

            Log.Error($"Failed to update project member: {(int)updateResponse.StatusCode} - {updateResponse.Content}");
            throw new InvalidOperationException(
                $"Failed to update project member '{username}': {(int)updateResponse.StatusCode} - {updateResponse.Content}");
        }

        // Add user as a new member
        var addRequest = new RestRequest($"projects/{projectId}/members", Method.Post)
            .AddJsonBody(new
            {
                user_id = userId,
                access_level = accessLevel
            });

        var addResponse = await _client.ExecutePostAsync<GitlabProjectMember>(addRequest);

        if (addResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created && addResponse.Data is not null)
        {
            Log.Information($"User '{username}' added to project with access level {accessLevel}");
            return;
        }

        Log.Error($"Failed to add project member: {(int)addResponse.StatusCode} - {addResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to add project member '{username}': {(int)addResponse.StatusCode} - {addResponse.Content}");
    }

    public async Task<GitlabProject> CreateTopLevelProject(string projectName, int? namespaceId = null)
    {
        return await CreateAndPopulateProject(
            projectName,
            namespaceId,
            async tempDir =>
            {
                var random = new Random();
                var sleepDuration = random.Next(60, 120);

                var buildShContent = $"""
                                      #!/bin/bash
                                      set -e

                                      echo "=========================================="
                                      echo "Starting build for {projectName}"
                                      echo "Build started at: $(date)"
                                      echo "=========================================="
                                      echo ""
                                      echo "Running build steps..."
                                      echo "- Preparing environment..."
                                      echo "- Compiling sources..."
                                      echo ""

                                      # Simulated build time
                                      sleep {sleepDuration}

                                      echo ""
                                      echo "=========================================="
                                      echo "Build completed successfully!"
                                      echo "Build finished at: $(date)"
                                      echo "Total build time: {sleepDuration} seconds"
                                      echo "=========================================="

                                      """;

                var buildShPath = Path.Combine(tempDir, "build.sh");
                await File.WriteAllTextAsync(buildShPath, buildShContent);

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        buildShPath,
                        UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead
                        | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead
                        | UnixFileMode.OtherExecute);
                }
            });
    }

    public async Task<GitlabProject> CreateRegularProject(string projectName, int? namespaceId = null)
    {
        return await CreateAndPopulateProject(projectName, namespaceId);
    }

    private async Task<bool> CheckProjectHasCommits(int projectId)
    {
        var request = new RestRequest($"projects/{projectId}/repository/commits");

        var response = await _client.ExecuteGetAsync<GitlabCommit[]>(request);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        return response is { IsSuccessful: true, Data.Length: > 0 };
    }

    public async Task<bool> CheckProjectHasCommitsPublic(int projectId)
    {
        return await CheckProjectHasCommits(projectId);
    }

    public async Task CreateFileInRepo(
        int projectId,
        string filePath,
        string content,
        string commitMessage,
        string branch = "main")
    {
        Log.Information($"Creating file '{filePath}' in project {projectId}");

        var request = new RestRequest($"projects/{projectId}/repository/files/{Uri.EscapeDataString(filePath)}", Method.Post)
            .AddJsonBody(new
            {
                branch = branch,
                content = content,
                commit_message = commitMessage
            });

        var response = await _client.ExecuteAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            Log.Information($"File '{filePath}' created successfully");
            return;
        }

        // If file already exists, that's okay
        if (response.StatusCode == HttpStatusCode.BadRequest && response.Content?.Contains("already exists") == true)
        {
            Log.Information($"File '{filePath}' already exists");
            return;
        }

        Log.Error($"Failed to create file: {(int)response.StatusCode} - {response.Content}");
        throw new InvalidOperationException(
            $"Failed to create file '{filePath}': {(int)response.StatusCode} - {response.Content}");
    }

    private async Task<GitlabProject> CreateAndPopulateProject(
        string projectName,
        int? namespaceId = null,
        Func<string, Task>? populateSpecificFiles = null)
    {
        var project = await CreateProject(projectName, namespaceId);

        var hasCommits = await CheckProjectHasCommits(project.Id);
        if (hasCommits)
        {
            Log.Information($"Project '{projectName}' already has commits, skipping repo population");
            return project;
        }

        var tempDir = CreateTempDirectory(projectName);

        try
        {
            if (populateSpecificFiles != null)
            {
                await populateSpecificFiles(tempDir);
            }

            const string readmeContent = """
                                         # Generic Readme

                                         This is a project for the CI lab environment.

                                         """;

            var readmePath = Path.Combine(tempDir, "README.md");
            await File.WriteAllTextAsync(readmePath, readmeContent);

            InitializeAndPushRepository(tempDir, project, projectName);

            Log.Information($"Repository populated and pushed to '{projectName}'");
            return project;
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
    }

    private string CreateTempDirectory(string projectName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"CILab-{projectName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private void CleanupTempDirectory(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - cleanup errors shouldn't abort the bootstrap
            Log.Warning($"Could not clean up temp directory '{tempDir}': {ex.Message}");
        }
    }

    private void InitializeAndPushRepository(
        string tempDir,
        GitlabProject project,
        string projectName)
    {
        Repository.Init(tempDir);
        using var repo = new Repository(tempDir);

        Commands.Stage(repo, "*");

        var signature = new Signature("CI Lab Bootstrap", "bootstrap@CILab.local", DateTimeOffset.Now);
        repo.Commit($"Initial commit for {projectName}", signature, signature);

        // Rename the default branch to 'main'
        var currentBranch = repo.Head;
        if (currentBranch.FriendlyName != "main")
        {
            var mainBranch = repo.CreateBranch("main");
            Commands.Checkout(repo, mainBranch);
        }

        var repoURL = project.HttpURLToRepo.Replace("http://", $"http://root:{_token}@");
        repo.Network.Remotes.Add("origin", repoURL);

        var pushOptions = new PushOptions
        {
            CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
            {
                Username = "root",
                Password = _token
            }
        };

        var localBranch = repo.Head;
        var localName = localBranch.FriendlyName;
        var remoteName = localName;

        try
        {
            var remote = repo.Network.Remotes["origin"];
            var refSpec = $"refs/heads/{localName}:refs/heads/{remoteName}";
            repo.Network.Push(remote, refSpec, pushOptions);

            repo.Branches.Update(
                localBranch,
                b =>
                {
                    b.Remote = "origin";
                    b.UpstreamBranch = $"refs/heads/{remoteName}";
                });
        }
        catch (Exception ex)
        {
            throw new Exception($"Git push failed for branch '{localName}': {ex.Message}", ex);
        }
    }
}