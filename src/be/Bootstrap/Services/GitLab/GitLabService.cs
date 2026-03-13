using Bootstrap.Entities.GitLab;
using LibGit2Sharp;
using RestSharp;
using Serilog;
using System.Net;

namespace Bootstrap.Services.GitLab;

public class GitLabService : IDisposable
{
    private readonly RestClient _client;

    private readonly string _token;

    public GitLabService(string gitLabURL, string token)
    {
        _token = token;
        _client = new RestClient(
            new RestClientOptions($"{gitLabURL.TrimEnd('/')}/api/v4")
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
    ///     Creates a RestClient authenticated as a specific user (via their PAT).
    ///     Caller is responsible for disposing the returned client.
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

    public async Task<GitLabGroup> CreateGroup(string groupName)
    {
        Log.Information($"Creating GitLab group '{groupName}'");

        var searchRequest = new RestRequest("groups")
            .AddQueryParameter("search", groupName);

        var searchResponse = await _client.ExecuteGetAsync<GitLabGroup[]>(searchRequest);

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
            .AddJsonBody(
                new
                {
                    name = groupName,
                    path = groupName.ToLower().Replace(" ", "-"),
                    visibility = "public"
                });

        var createResponse = await _client.ExecutePostAsync<GitLabGroup>(createRequest);

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

    public async Task<GitLabProject> CreateProject(string projectName, int? namespaceId = null)
    {
        Log.Information($"Creating Gitlab project '{projectName}'");

        var searchRequest = new RestRequest("projects")
            .AddQueryParameter("search", projectName);

        // When a specific namespace is requested, filter the search so that stale
        // projects from deleted or different namespaces are not mistakenly found.
        if (namespaceId.HasValue)
        {
            searchRequest.AddQueryParameter("namespace_id", namespaceId.Value.ToString());
        }

        var searchResponse = await _client.ExecuteGetAsync<GitLabProject[]>(searchRequest);

        if (searchResponse is { IsSuccessful: true, Data: not null })
        {
            Log.Debug($"Checking if '{projectName}' already exists");
            foreach (var proj in searchResponse.Data)
            {
                if (proj.Name == projectName)
                {
                    // If a specific namespace was requested, ensure the found project
                    // belongs to that namespace. This prevents reusing stale projects
                    // from a deleted namespace that are still visible during async deletion.
                    if (namespaceId.HasValue && proj.Namespace?.Id != namespaceId.Value)
                    {
                        Log.Debug(
                            $"Project '{projectName}' found but in namespace {proj.Namespace?.Id}, expected {namespaceId.Value} - ignoring");

                        continue;
                    }

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

        var createResponse = await _client.ExecutePostAsync<GitLabProject>(createRequest);

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

        var userSearchResponse = await _client.ExecuteGetAsync<GitLabUser[]>(userSearchRequest);

        if (userSearchResponse is not { IsSuccessful: true, Data: not null }
            || userSearchResponse.Data.Length == 0)
        {
            Log.Error($"User '{username}' not found");
            throw new InvalidOperationException($"User '{username}' not found in GitLab");
        }

        var userId = userSearchResponse.Data[0].Id;

        // Check if user is already a member
        var checkRequest = new RestRequest($"groups/{groupId}/members/{userId}");
        var checkResponse = await _client.ExecuteGetAsync<GitLabProjectMember>(checkRequest);

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

            var updateResponse = await _client.ExecuteAsync<GitLabProjectMember>(updateRequest);

            if (updateResponse.StatusCode is HttpStatusCode.OK && updateResponse.Data is not null)
            {
                Log.Information($"Updated group access level for user '{username}' to {accessLevel}");
                return;
            }

            Log.Error(
                $"Failed to update group member: {(int)updateResponse.StatusCode} - {updateResponse.Content}");

            throw new InvalidOperationException(
                $"Failed to update group member '{username}': {(int)updateResponse.StatusCode} - {updateResponse.Content}");
        }

        // Add user as a new group member
        var addRequest = new RestRequest($"groups/{groupId}/members", Method.Post)
            .AddJsonBody(new { user_id = userId, access_level = accessLevel });

        var addResponse = await _client.ExecutePostAsync<GitLabProjectMember>(addRequest);

        if (addResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && addResponse.Data is not null)
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

        var userSearchResponse = await _client.ExecuteGetAsync<GitLabUser[]>(userSearchRequest);

        if (userSearchResponse is not { IsSuccessful: true, Data: not null }
            || userSearchResponse.Data.Length == 0)
        {
            Log.Error($"User '{username}' not found");
            throw new InvalidOperationException($"User '{username}' not found in GitLab");
        }

        var userId = userSearchResponse.Data[0].Id;

        // Check if user is already a member
        var checkRequest = new RestRequest($"projects/{projectId}/members/{userId}");
        var checkResponse = await _client.ExecuteGetAsync<GitLabProjectMember>(checkRequest);

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

            var updateResponse = await _client.ExecuteAsync<GitLabProjectMember>(updateRequest);

            if (updateResponse.StatusCode is HttpStatusCode.OK && updateResponse.Data is not null)
            {
                Log.Information($"Updated access level for user '{username}' to {accessLevel}");
                return;
            }

            Log.Error(
                $"Failed to update project member: {(int)updateResponse.StatusCode} - {updateResponse.Content}");

            throw new InvalidOperationException(
                $"Failed to update project member '{username}': {(int)updateResponse.StatusCode} - {updateResponse.Content}");
        }

        // Add user as a new member
        var addRequest = new RestRequest($"projects/{projectId}/members", Method.Post)
            .AddJsonBody(new { user_id = userId, access_level = accessLevel });

        var addResponse = await _client.ExecutePostAsync<GitLabProjectMember>(addRequest);

        if (addResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && addResponse.Data is not null)
        {
            Log.Information($"User '{username}' added to project with access level {accessLevel}");
            return;
        }

        Log.Error($"Failed to add project member: {(int)addResponse.StatusCode} - {addResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to add project member '{username}': {(int)addResponse.StatusCode} - {addResponse.Content}");
    }

    public async Task<GitLabProject> CreateTopLevelProject(string projectName, int? namespaceId = null)
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

    public async Task<GitLabProject> CreateRegularProject(string projectName, int? namespaceId = null)
    {
        return await CreateAndPopulateProject(projectName, namespaceId);
    }

    private async Task<bool> CheckProjectHasCommits(int projectId)
    {
        var request = new RestRequest($"projects/{projectId}/repository/commits");

        var response = await _client.ExecuteGetAsync<GitLabCommit[]>(request);

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

        var request = new RestRequest(
                $"projects/{projectId}/repository/files/{Uri.EscapeDataString(filePath)}",
                Method.Post)
            .AddJsonBody(new { branch, content, commit_message = commitMessage });

        var response = await _client.ExecuteAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            Log.Information($"File '{filePath}' created successfully");
            return;
        }

        // If file already exists, that's okay
        if (response.StatusCode == HttpStatusCode.BadRequest
            && response.Content?.Contains("already exists") == true)
        {
            Log.Information($"File '{filePath}' already exists");
            return;
        }

        Log.Error($"Failed to create file: {(int)response.StatusCode} - {response.Content}");
        throw new InvalidOperationException(
            $"Failed to create file '{filePath}': {(int)response.StatusCode} - {response.Content}");
    }

    private async Task<GitLabProject> CreateAndPopulateProject(
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
        GitLabProject project,
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

        var searchResponse = await _client.ExecuteGetAsync<GitLabUser[]>(searchRequest);

        if (searchResponse is { IsSuccessful: true, Data: not null } && searchResponse.Data.Length > 0)
        {
            Log.Information($"User '{username}' already exists");
            return false;
        }

        // Create the user
        var createRequest = new RestRequest("users", Method.Post)
            .AddJsonBody(
                new
                {
                    username,
                    name,
                    email,
                    password,
                    skip_confirmation = true
                });

        var createResponse = await _client.ExecutePostAsync<GitLabUser>(createRequest);

        if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && createResponse.Data is not null)
        {
            Log.Information($"User '{username}' created successfully");
            return true;
        }

        Log.Error(
            $"Failed to create user '{username}': {(int)createResponse.StatusCode} - {createResponse.Content}");

        throw new InvalidOperationException(
            $"Failed to create GitLab user '{username}': {(int)createResponse.StatusCode} {createResponse.StatusCode} - {createResponse.Content}");
    }

    public async Task<GitLabOAuthApplication> CreateOAuthApplication(
        string name,
        string redirectUri,
        string scopes = "read_user read_api")
    {
        Log.Information($"Ensuring GitLab OAuth application '{name}' exists...");

        var applications = await ListOAuthApplications();

        Log.Information($"Loaded {applications.Count} OAuth applications from GitLab");

        var expectedRedirectUris = redirectUri
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Find existing apps by name or callback URL match
        var existingApps = applications
            .Where(app =>
                app.Name == name
                || expectedRedirectUris.Any(expected =>
                    app.CallbackUrl.Contains(expected, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Delete all matching apps: GitLab never returns secrets via the list API,
        // so reusing an existing app would leave us without the client secret.
        foreach (var app in existingApps)
        {
            Log.Information(
                $"Deleting existing OAuth application (id={app.Id}, name='{app.Name}') to obtain a fresh secret.");

            var deleteRequest = new RestRequest($"applications/{app.Id}", Method.Delete);
            await _client.ExecuteAsync(deleteRequest);
        }

        Log.Information($"Creating OAuth application '{name}'.");

        var createRequest = new RestRequest("applications", Method.Post)
            .AddJsonBody(new { name, redirect_uri = redirectUri, scopes, confidential = true });

        var createResponse = await _client.ExecutePostAsync<GitLabOAuthApplication>(createRequest);

        if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && createResponse.Data is not null)
        {
            Log.Information($"OAuth application '{name}' created successfully");
            return createResponse.Data;
        }

        Log.Error(
            $"Failed to create OAuth application: {(int)createResponse.StatusCode} - {createResponse.Content}");

        throw new InvalidOperationException(
            $"Failed to create OAuth application: {(int)createResponse.StatusCode} - {createResponse.Content}");
    }

    private async Task<List<GitLabOAuthApplication>> ListOAuthApplications()
    {
        var applications = new List<GitLabOAuthApplication>();
        var page = 1;

        while (true)
        {
            var listRequest = new RestRequest("applications")
                .AddQueryParameter("per_page", "100")
                .AddQueryParameter("page", page.ToString());

            var listResponse = await _client.ExecuteGetAsync<GitLabOAuthApplication[]>(listRequest);

            if (listResponse is not { IsSuccessful: true, Data: not null })
            {
                Log.Error(
                    $"Failed to list OAuth applications (page {page}): {(int)listResponse.StatusCode} - {listResponse.Content}");

                throw new InvalidOperationException(
                    $"Failed to list OAuth applications (page {page}): {(int)listResponse.StatusCode} - {listResponse.Content}");
            }

            applications.AddRange(listResponse.Data);
            Log.Information($"OAuth applications page {page} loaded ({listResponse.Data.Length} entries)");

            var nextPageHeader = listResponse.Headers?
                .FirstOrDefault(h => string.Equals(
                    h.Name?.ToString(),
                    "X-Next-Page",
                    StringComparison.OrdinalIgnoreCase))
                ?.Value?
                .ToString();

            if (string.IsNullOrWhiteSpace(nextPageHeader) || nextPageHeader == "0")
            {
                break;
            }

            if (!int.TryParse(nextPageHeader, out page) || page <= 0)
            {
                Log.Error(
                    $"Invalid X-Next-Page header value while listing OAuth applications: '{nextPageHeader}'");

                throw new InvalidOperationException(
                    $"Invalid X-Next-Page header value while listing OAuth applications: '{nextPageHeader}'");
            }
        }

        return applications;
    }

    public async Task ConfigureProjectMergeRequestSettings(int projectId)
    {
        Log.Information($"Configuring merge request settings for project {projectId}");

        var updateRequest = new RestRequest($"projects/{projectId}", Method.Put)
            .AddJsonBody(new { only_allow_merge_if_pipeline_succeeds = true });

        var updateResponse = await _client.ExecuteAsync(updateRequest);

        if (updateResponse.StatusCode is HttpStatusCode.OK)
        {
            Log.Information($"Merge request settings configured for project {projectId}");
            return;
        }

        Log.Error(
            $"Failed to configure project merge request settings: {(int)updateResponse.StatusCode} - {updateResponse.Content}");

        throw new InvalidOperationException(
            $"Failed to configure project merge request settings: {(int)updateResponse.StatusCode} - {updateResponse.Content}");
    }

    /// <summary>
    ///     Creates a branch from the given ref (defaults to "main").
    ///     Returns the branch if created or already exists.
    /// </summary>
    public async Task<GitLabBranch> CreateBranch(int projectId, string branchName, string fromRef = "main")
    {
        Log.Information($"Creating branch '{branchName}' in project {projectId} from '{fromRef}'");

        // Check if branch already exists
        var encodedBranch = Uri.EscapeDataString(branchName);
        var checkRequest = new RestRequest($"projects/{projectId}/repository/branches/{encodedBranch}");
        var checkResponse = await _client.ExecuteGetAsync<GitLabBranch>(checkRequest);

        if (checkResponse.IsSuccessful && checkResponse.Data is not null)
        {
            Log.Information($"Branch '{branchName}' already exists in project {projectId}");
            return checkResponse.Data;
        }

        var createRequest = new RestRequest($"projects/{projectId}/repository/branches", Method.Post)
            .AddJsonBody(new { branch = branchName, @ref = fromRef });

        var createResponse = await _client.ExecutePostAsync<GitLabBranch>(createRequest);

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
    ///     Creates a file commit on a branch as a specific user (via their PAT).
    ///     If userToken is null, uses the admin token.
    /// </summary>
    public async Task CreateCommitOnBranchAsUser(
        int projectId,
        string branchName,
        string filePath,
        string content,
        string commitMessage,
        string? userToken)
    {
        Log.Information(
            $"Creating commit on branch '{branchName}' in project {projectId}"
            + (userToken != null ? " (as user)" : " (as admin)"));

        var client = _client;
        RestClient? userClient = null;

        if (userToken != null)
        {
            userClient = CreateUserClient(userToken);
            client = userClient;
        }

        try
        {
            var request = new RestRequest(
                    $"projects/{projectId}/repository/files/{Uri.EscapeDataString(filePath)}",
                    Method.Post)
                .AddJsonBody(new { branch = branchName, content, commit_message = commitMessage });

            var response = await client.ExecuteAsync(request);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                Log.Information($"Commit created on branch '{branchName}' in project {projectId}");
                return;
            }

            // If file already exists, update it instead
            if (response.StatusCode == HttpStatusCode.BadRequest
                && response.Content?.Contains("already exists") == true)
            {
                Log.Information($"File '{filePath}' already exists on branch '{branchName}', updating it");
                var updateRequest = new RestRequest(
                        $"projects/{projectId}/repository/files/{Uri.EscapeDataString(filePath)}",
                        Method.Put)
                    .AddJsonBody(new { branch = branchName, content, commit_message = commitMessage });

                var updateResponse = await client.ExecuteAsync(updateRequest);

                if (updateResponse.StatusCode is HttpStatusCode.OK)
                {
                    Log.Information($"File updated on branch '{branchName}' in project {projectId}");
                    return;
                }

                Log.Error(
                    $"Failed to update file: {(int)updateResponse.StatusCode} - {updateResponse.Content}");

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
    ///     Creates a merge request. Returns it if created, or returns the existing one if already open.
    /// </summary>
    public async Task<GitLabMergeRequest> CreateMergeRequest(
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

        var searchResponse = await userClient.ExecuteGetAsync<GitLabMergeRequest[]>(searchRequest);

        if (searchResponse is { IsSuccessful: true, Data: not null } && searchResponse.Data.Length > 0)
        {
            Log.Information($"MR already exists for {sourceBranch} -> {targetBranch} in project {projectId}");
            return searchResponse.Data[0];
        }

        var createRequest = new RestRequest($"projects/{projectId}/merge_requests", Method.Post)
            .AddJsonBody(new { source_branch = sourceBranch, target_branch = targetBranch, title });

        var createResponse = await userClient.ExecutePostAsync<GitLabMergeRequest>(createRequest);

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
    ///     Approves a merge request as the given user.
    /// </summary>
    public async Task ApproveMergeRequest(int projectId, int mergeRequestIid, string approverToken)
    {
        Log.Information($"Approving MR !{mergeRequestIid} in project {projectId}");

        using var approverClient = CreateUserClient(approverToken);

        var request = new RestRequest(
            $"projects/{projectId}/merge_requests/{mergeRequestIid}/approve",
            Method.Post);

        var response = await approverClient.ExecutePostAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            Log.Information($"MR !{mergeRequestIid} approved in project {projectId}");
            return;
        }

        // 401 can occur when already approved
        if (response.StatusCode == HttpStatusCode.Unauthorized
            || response.Content?.Contains("already approved") == true)
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

        var userSearchResponse = await _client.ExecuteGetAsync<GitLabUser[]>(userSearchRequest);

        if (userSearchResponse is not { IsSuccessful: true, Data: not null }
            || userSearchResponse.Data.Length == 0)
        {
            throw new InvalidOperationException($"User '{username}' not found in GitLab");
        }

        var userId = userSearchResponse.Data[0].Id;

        // Revoke any existing tokens with the same name to ensure idempotency.
        // Old token values cannot be retrieved from the GitLab API, so always
        // revoke and recreate to guarantee the .env file has a valid token.
        var existingTokensRequest = new RestRequest("personal_access_tokens")
            .AddQueryParameter("user_id", userId.ToString())
            .AddQueryParameter("state", "active");

        var existingTokensResponse =
            await _client.ExecuteGetAsync<GitLabPersonalAccessToken[]>(existingTokensRequest);

        if (existingTokensResponse is { IsSuccessful: true, Data: not null })
        {
            foreach (var existingToken in existingTokensResponse.Data.Where(t => t.Name == tokenName))
            {
                Log.Information(
                    $"Revoking existing token '{tokenName}' (ID: {existingToken.Id}) for user '{username}'");

                var revokeRequest = new RestRequest(
                    $"personal_access_tokens/{existingToken.Id}",
                    Method.Delete);

                await _client.ExecuteAsync(revokeRequest);
            }
        }

        // Create personal access token via admin API
        var createRequest = new RestRequest($"users/{userId}/personal_access_tokens", Method.Post)
            .AddJsonBody(
                new
                {
                    name = tokenName,
                    scopes,
                    expires_at = DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd")
                });

        var createResponse = await _client.ExecutePostAsync<GitLabPersonalAccessToken>(createRequest);

        if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && createResponse.Data is not null)
        {
            Log.Information($"Personal access token '{tokenName}' created for user '{username}'");
            return createResponse.Data;
        }

        Log.Error(
            $"Failed to create personal access token: {(int)createResponse.StatusCode} - {createResponse.Content}");

        throw new InvalidOperationException(
            $"Failed to create personal access token '{tokenName}' for user '{username}': {(int)createResponse.StatusCode} - {createResponse.Content}");
    }

    /// <summary>
    ///     Deletes a GitLab group by name. This cascades and deletes all projects within the group.
    ///     Does nothing if the group does not exist.
    /// </summary>
    public async Task DeleteGroup(string groupName)
    {
        Log.Information($"Deleting GitLab group '{groupName}' (and all its projects)...");

        var searchRequest = new RestRequest("groups")
            .AddQueryParameter("search", groupName);

        var searchResponse = await _client.ExecuteGetAsync<GitLabGroup[]>(searchRequest);

        if (searchResponse is not { IsSuccessful: true, Data: not null })
        {
            Log.Warning($"Could not search for group '{groupName}': {searchResponse.Content}");
            return;
        }

        var group = searchResponse.Data.FirstOrDefault(g => g.Name == groupName);
        if (group == null)
        {
            Log.Information($"Group '{groupName}' not found, nothing to delete");
            return;
        }

        var deleteRequest = new RestRequest($"groups/{group.Id}", Method.Delete);
        var deleteResponse = await _client.ExecuteAsync(deleteRequest);

        if (deleteResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted
            or HttpStatusCode.NoContent)
        {
            Log.Information($"Group '{groupName}' (ID: {group.Id}) deleted successfully");
            return;
        }

        Log.Error(
            $"Failed to delete group '{groupName}': {(int)deleteResponse.StatusCode} - {deleteResponse.Content}");

        throw new InvalidOperationException(
            $"Failed to delete GitLab group '{groupName}': {(int)deleteResponse.StatusCode} - {deleteResponse.Content}");
    }

    /// <summary>
    ///     Polls until a GitLab group no longer exists. Throws if the group still exists after the timeout.
    /// </summary>
    public async Task WaitForGroupDeletion(string groupName, int timeoutSeconds = 120)
    {
        Log.Information($"Waiting for GitLab group '{groupName}' to finish deleting...");

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var exists = await GroupExists(groupName);
            if (!exists)
            {
                Log.Information($"GitLab group '{groupName}' has been deleted");
                return;
            }

            Log.Debug($"Group '{groupName}' still exists, waiting 5 seconds...");
            await Task.Delay(5000);
        }

        throw new InvalidOperationException(
            $"Timed out waiting for GitLab group '{groupName}' to be deleted after {timeoutSeconds} seconds");
    }

    /// <summary>
    ///     Deletes a GitLab project by exact name match (top-level, not within a group).
    ///     Does nothing if the project does not exist.
    /// </summary>
    public async Task DeleteProject(string projectName)
    {
        Log.Information($"Deleting GitLab project '{projectName}'...");

        var searchRequest = new RestRequest("projects")
            .AddQueryParameter("search", projectName);

        var searchResponse = await _client.ExecuteGetAsync<GitLabProject[]>(searchRequest);

        if (searchResponse is not { IsSuccessful: true, Data: not null })
        {
            Log.Warning($"Could not search for project '{projectName}': {searchResponse.Content}");
            return;
        }

        var project = searchResponse.Data.FirstOrDefault(p => p.Name == projectName);
        if (project == null)
        {
            Log.Information($"Project '{projectName}' not found, nothing to delete");
            return;
        }

        var deleteRequest = new RestRequest($"projects/{project.Id}", Method.Delete);
        var deleteResponse = await _client.ExecuteAsync(deleteRequest);

        if (deleteResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted
            or HttpStatusCode.NoContent)
        {
            Log.Information($"Project '{projectName}' (ID: {project.Id}) deleted successfully");
            return;
        }

        Log.Error(
            $"Failed to delete project '{projectName}': {(int)deleteResponse.StatusCode} - {deleteResponse.Content}");

        throw new InvalidOperationException(
            $"Failed to delete GitLab project '{projectName}': {(int)deleteResponse.StatusCode} - {deleteResponse.Content}");
    }

    /// <summary>
    ///     Polls until a GitLab project no longer exists. Throws if the project still exists after the timeout.
    /// </summary>
    public async Task WaitForProjectDeletion(string projectName, int timeoutSeconds = 120)
    {
        Log.Information($"Waiting for GitLab project '{projectName}' to finish deleting...");

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var exists = await ProjectExists(projectName);
            if (!exists)
            {
                Log.Information($"GitLab project '{projectName}' has been deleted");
                return;
            }

            Log.Debug($"Project '{projectName}' still exists, waiting 5 seconds...");
            await Task.Delay(5000);
        }

        throw new InvalidOperationException(
            $"Timed out waiting for GitLab project '{projectName}' to be deleted after {timeoutSeconds} seconds");
    }

    private async Task<bool> GroupExists(string groupName)
    {
        var request = new RestRequest("groups").AddQueryParameter("search", groupName);
        var response = await _client.ExecuteGetAsync<GitLabGroup[]>(request);

        if (response is not { IsSuccessful: true, Data: not null })
        {
            return false;
        }

        return response.Data.Any(g => g.Name == groupName);
    }

    private async Task<bool> ProjectExists(string projectName)
    {
        var request = new RestRequest("projects").AddQueryParameter("search", projectName);
        var response = await _client.ExecuteGetAsync<GitLabProject[]>(request);

        if (response is not { IsSuccessful: true, Data: not null })
        {
            return false;
        }

        return response.Data.Any(p => p.Name == projectName);
    }
}