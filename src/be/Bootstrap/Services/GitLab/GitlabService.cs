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

    /// <summary>
    /// Creates a RestClient authenticated as a specific user (via their PAT).
    /// Caller is responsible for disposing the returned client.
    /// </summary>
    private RestClient CreateUserClient(string token)
    {
        var client = new RestClient(
            new RestClientOptions(_client.Options.BaseUrl!.ToString())
            {
                ThrowOnAnyError = false,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                Timeout = TimeSpan.FromSeconds(30)
            });
        client.AddDefaultHeader("PRIVATE-TOKEN", token);
        return client;
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

    public async Task AddGroupMember(int groupId, string username, int accessLevel = 30)
    {
        Log.Information($"Adding user '{username}' to group {groupId} with access level {accessLevel}");

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
        var checkRequest = new RestRequest($"groups/{groupId}/members/{userId}");
        var checkResponse = await _client.ExecuteGetAsync<GitlabProjectMember>(checkRequest);

        if (checkResponse.IsSuccessful && checkResponse.Data is not null)
        {
            if (checkResponse.Data.AccessLevel >= accessLevel)
            {
                Log.Information($"User '{username}' is already a group member with sufficient access level");
                return;
            }

            Log.Information($"User '{username}' is already a group member, updating access level");
            var updateRequest = new RestRequest($"groups/{groupId}/members/{userId}", Method.Put)
                .AddJsonBody(new { access_level = accessLevel });

            var updateResponse = await _client.ExecuteAsync<GitlabProjectMember>(updateRequest);

            if (updateResponse.StatusCode is HttpStatusCode.OK && updateResponse.Data is not null)
            {
                Log.Information($"Updated group access level for user '{username}' to {accessLevel}");
                return;
            }

            Log.Error($"Failed to update group member: {(int)updateResponse.StatusCode} - {updateResponse.Content}");
            throw new InvalidOperationException(
                $"Failed to update group member '{username}': {(int)updateResponse.StatusCode} - {updateResponse.Content}");
        }

        // Add user as a new group member
        var addRequest = new RestRequest($"groups/{groupId}/members", Method.Post)
            .AddJsonBody(new
            {
                user_id = userId,
                access_level = accessLevel
            });

        var addResponse = await _client.ExecutePostAsync<GitlabProjectMember>(addRequest);

        if (addResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created && addResponse.Data is not null)
        {
            Log.Information($"User '{username}' added to group with access level {accessLevel}");
            return;
        }

        Log.Error($"Failed to add group member: {(int)addResponse.StatusCode} - {addResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to add group member '{username}': {(int)addResponse.StatusCode} - {addResponse.Content}");
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

    /// <summary>
    ///     Creates a user in GitLab. Returns true if created, false if already exists.
    /// </summary>
    public async Task<bool> CreateUser(string username, string name, string email, string password)
    {
        Log.Information($"Creating GitLab user '{username}'...");

        // Check if user already exists
        var searchRequest = new RestRequest("users")
            .AddQueryParameter("username", username);

        var searchResponse = await _client.ExecuteGetAsync<GitlabUser[]>(searchRequest);

        if (searchResponse is { IsSuccessful: true, Data: not null } && searchResponse.Data.Length > 0)
        {
            Log.Information($"User '{username}' already exists");
            return false;
        }

        // Create the user
        var createRequest = new RestRequest("users", Method.Post)
            .AddJsonBody(new
            {
                username = username,
                name = name,
                email = email,
                password = password,
                skip_confirmation = true
            });

        var createResponse = await _client.ExecutePostAsync<GitlabUser>(createRequest);

        if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && createResponse.Data is not null)
        {
            Log.Information($"User '{username}' created successfully");
            return true;
        }

        Log.Error($"Failed to create user '{username}': {(int)createResponse.StatusCode} - {createResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to create GitLab user '{username}': {(int)createResponse.StatusCode} {createResponse.StatusCode} - {createResponse.Content}");
    }

    public async Task<GitLabOAuthApplication> CreateOAuthApplication(
        string name,
        string redirectUri,
        string scopes = "read_user read_api")
    {
        Log.Information($"Creating GitLab OAuth application '{name}'...");

        // Check if the application already exists (by name)
        var listRequest = new RestRequest("applications");
        var listResponse = await _client.ExecuteGetAsync<GitLabOAuthApplication[]>(listRequest);

        if (listResponse is { IsSuccessful: true, Data: not null })
        {
            foreach (var app in listResponse.Data)
            {
                // Always delete existing OAuth apps that use our callback path.
                // The GitLab list API never returns the secret, so we can't reuse
                // an existing app — we must recreate to get a fresh secret.
                if (app.CallbackUrl.Contains("/api/auth/callback"))
                {
                    Log.Information($"Deleting existing OAuth application (id={app.Id}) to recreate with fresh credentials");
                    var deleteRequest = new RestRequest($"applications/{app.Id}", Method.Delete);
                    await _client.ExecuteAsync(deleteRequest);
                }
            }
        }

        var createRequest = new RestRequest("applications", Method.Post)
            .AddJsonBody(new
            {
                name,
                redirect_uri = redirectUri,
                scopes,
                confidential = true
            });

        var createResponse = await _client.ExecutePostAsync<GitLabOAuthApplication>(createRequest);

        if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && createResponse.Data is not null)
        {
            Log.Information($"OAuth application '{name}' created successfully");
            return createResponse.Data;
        }

        Log.Error($"Failed to create OAuth application: {(int)createResponse.StatusCode} - {createResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to create OAuth application: {(int)createResponse.StatusCode} - {createResponse.Content}");
    }

    public async Task ConfigureProjectMergeRequestSettings(int projectId)
    {
        Log.Information($"Configuring merge request settings for project {projectId}");

        var updateRequest = new RestRequest($"projects/{projectId}", Method.Put)
            .AddJsonBody(new
            {
                only_allow_merge_if_pipeline_succeeds = true,
                approvals_before_merge = 1
            });

        var updateResponse = await _client.ExecuteAsync(updateRequest);

        if (updateResponse.StatusCode is HttpStatusCode.OK)
        {
            Log.Information($"Merge request settings configured for project {projectId}");
            return;
        }

        Log.Error($"Failed to configure project merge request settings: {(int)updateResponse.StatusCode} - {updateResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to configure project merge request settings: {(int)updateResponse.StatusCode} - {updateResponse.Content}");
    }

    /// <summary>
    /// Creates a branch from the given ref (defaults to "main").
    /// Returns the branch if created or already exists.
    /// </summary>
    public async Task<GitlabBranch> CreateBranch(int projectId, string branchName, string fromRef = "main")
    {
        Log.Information($"Creating branch '{branchName}' in project {projectId} from '{fromRef}'");

        // Check if branch already exists
        var encodedBranch = Uri.EscapeDataString(branchName);
        var checkRequest = new RestRequest($"projects/{projectId}/repository/branches/{encodedBranch}");
        var checkResponse = await _client.ExecuteGetAsync<GitlabBranch>(checkRequest);

        if (checkResponse.IsSuccessful && checkResponse.Data is not null)
        {
            Log.Information($"Branch '{branchName}' already exists in project {projectId}");
            return checkResponse.Data;
        }

        var createRequest = new RestRequest($"projects/{projectId}/repository/branches", Method.Post)
            .AddJsonBody(new { branch = branchName, @ref = fromRef });

        var createResponse = await _client.ExecutePostAsync<GitlabBranch>(createRequest);

        if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && createResponse.Data is not null)
        {
            Log.Information($"Branch '{branchName}' created in project {projectId}");
            return createResponse.Data;
        }

        Log.Error($"Failed to create branch: {(int)createResponse.StatusCode} - {createResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to create branch '{branchName}': {(int)createResponse.StatusCode} - {createResponse.Content}");
    }

    /// <summary>
    /// Creates a file commit on a branch as a specific user (via their PAT).
    /// If userToken is null, uses the admin token.
    /// </summary>
    public async Task CreateCommitOnBranchAsUser(
        int projectId,
        string branchName,
        string filePath,
        string content,
        string commitMessage,
        string? userToken)
    {
        Log.Information($"Creating commit on branch '{branchName}' in project {projectId}" +
                        (userToken != null ? " (as user)" : " (as admin)"));

        var client = _client;
        RestClient? userClient = null;

        if (userToken != null)
        {
            userClient = CreateUserClient(userToken);
            client = userClient;
        }

        try
        {
            var request = new RestRequest($"projects/{projectId}/repository/files/{Uri.EscapeDataString(filePath)}", Method.Post)
                .AddJsonBody(new
                {
                    branch = branchName,
                    content,
                    commit_message = commitMessage
                });

            var response = await client.ExecuteAsync(request);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                Log.Information($"Commit created on branch '{branchName}' in project {projectId}");
                return;
            }

            // If file already exists, update it instead
            if (response.StatusCode == HttpStatusCode.BadRequest && response.Content?.Contains("already exists") == true)
            {
                Log.Information($"File '{filePath}' already exists on branch '{branchName}', updating it");
                var updateRequest = new RestRequest($"projects/{projectId}/repository/files/{Uri.EscapeDataString(filePath)}", Method.Put)
                    .AddJsonBody(new
                    {
                        branch = branchName,
                        content,
                        commit_message = commitMessage
                    });

                var updateResponse = await client.ExecuteAsync(updateRequest);

                if (updateResponse.StatusCode is HttpStatusCode.OK)
                {
                    Log.Information($"File updated on branch '{branchName}' in project {projectId}");
                    return;
                }

                Log.Error($"Failed to update file: {(int)updateResponse.StatusCode} - {updateResponse.Content}");
                throw new InvalidOperationException(
                    $"Failed to update file on branch '{branchName}': {(int)updateResponse.StatusCode} - {updateResponse.Content}");
            }

            Log.Error($"Failed to create commit: {(int)response.StatusCode} - {response.Content}");
            throw new InvalidOperationException(
                $"Failed to create commit on branch '{branchName}': {(int)response.StatusCode} - {response.Content}");
        }
        finally
        {
            userClient?.Dispose();
        }
    }

    /// <summary>
    /// Creates a merge request. Returns it if created, or returns the existing one if already open.
    /// </summary>
    public async Task<GitlabMergeRequest> CreateMergeRequest(
        int projectId,
        string sourceBranch,
        string targetBranch,
        string title,
        string userToken)
    {
        Log.Information($"Creating MR '{title}' in project {projectId}: {sourceBranch} -> {targetBranch}");

        // Use a user-scoped client to create MR as that user
        using var userClient = CreateUserClient(userToken);

        // Check if MR already exists
        var searchRequest = new RestRequest($"projects/{projectId}/merge_requests")
            .AddQueryParameter("source_branch", sourceBranch)
            .AddQueryParameter("target_branch", targetBranch)
            .AddQueryParameter("state", "opened");

        var searchResponse = await userClient.ExecuteGetAsync<GitlabMergeRequest[]>(searchRequest);

        if (searchResponse is { IsSuccessful: true, Data: not null } && searchResponse.Data.Length > 0)
        {
            Log.Information($"MR already exists for {sourceBranch} -> {targetBranch} in project {projectId}");
            return searchResponse.Data[0];
        }

        var createRequest = new RestRequest($"projects/{projectId}/merge_requests", Method.Post)
            .AddJsonBody(new
            {
                source_branch = sourceBranch,
                target_branch = targetBranch,
                title
            });

        var createResponse = await userClient.ExecutePostAsync<GitlabMergeRequest>(createRequest);

        if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && createResponse.Data is not null)
        {
            Log.Information($"MR '{title}' created in project {projectId} (IID: {createResponse.Data.Iid})");
            return createResponse.Data;
        }

        Log.Error($"Failed to create MR: {(int)createResponse.StatusCode} - {createResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to create MR '{title}': {(int)createResponse.StatusCode} - {createResponse.Content}");
    }

    /// <summary>
    /// Approves a merge request as the given user.
    /// </summary>
    public async Task ApproveMergeRequest(int projectId, int mergeRequestIid, string approverToken)
    {
        Log.Information($"Approving MR !{mergeRequestIid} in project {projectId}");

        using var approverClient = CreateUserClient(approverToken);

        var request = new RestRequest($"projects/{projectId}/merge_requests/{mergeRequestIid}/approve", Method.Post);
        var response = await approverClient.ExecutePostAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            Log.Information($"MR !{mergeRequestIid} approved in project {projectId}");
            return;
        }

        // 401 can occur when already approved
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.Content?.Contains("already approved") == true)
        {
            Log.Information($"MR !{mergeRequestIid} was already approved or cannot be approved again");
            return;
        }

        Log.Error($"Failed to approve MR: {(int)response.StatusCode} - {response.Content}");
        throw new InvalidOperationException(
            $"Failed to approve MR !{mergeRequestIid}: {(int)response.StatusCode} - {response.Content}");
    }

    public async Task<GitLabPersonalAccessToken> CreatePersonalAccessToken(
        string username,
        string tokenName,
        string[] scopes)
    {
        Log.Information($"Creating personal access token '{tokenName}' for user '{username}'...");

        // Get user ID by username
        var userSearchRequest = new RestRequest("users")
            .AddQueryParameter("username", username);

        var userSearchResponse = await _client.ExecuteGetAsync<GitlabUser[]>(userSearchRequest);

        if (userSearchResponse is not { IsSuccessful: true, Data: not null } || userSearchResponse.Data.Length == 0)
        {
            throw new InvalidOperationException($"User '{username}' not found in GitLab");
        }

        var userId = userSearchResponse.Data[0].Id;

        // Create personal access token via admin API
        var createRequest = new RestRequest($"users/{userId}/personal_access_tokens", Method.Post)
            .AddJsonBody(new
            {
                name = tokenName,
                scopes = scopes,
                expires_at = DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd")
            });

        var createResponse = await _client.ExecutePostAsync<GitLabPersonalAccessToken>(createRequest);

        if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && createResponse.Data is not null)
        {
            Log.Information($"Personal access token '{tokenName}' created for user '{username}'");
            return createResponse.Data;
        }

        Log.Error($"Failed to create personal access token: {(int)createResponse.StatusCode} - {createResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to create personal access token '{tokenName}' for user '{username}': {(int)createResponse.StatusCode} - {createResponse.Content}");
    }
}