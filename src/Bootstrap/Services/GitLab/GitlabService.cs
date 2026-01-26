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

    public GitlabService(string gitlabUrl, string token)
    {
        var gitlabUrl1 = gitlabUrl.TrimEnd('/');
        _token = token;
        _client = new RestClient(
            new RestClientOptions($"{gitlabUrl1}/api/v4")
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

    public async Task<bool> CreateTopLevelProject(string projectName, int? namespaceId = null)
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

    public async Task<bool> CreateSubProject(string projectName, int? namespaceId = null)
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

    private async Task<bool> CreateAndPopulateProject(
        string projectName,
        int? namespaceId = null,
        Func<string, Task>? populateSpecificFiles = null)
    {
        var project = await CreateProject(projectName, namespaceId);

        var hasCommits = await CheckProjectHasCommits(project.Id);
        if (hasCommits)
        {
            Log.Information($"Project '{projectName}' already has commits, skipping repo population");
            return true;
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

                                         This is a test project for the CI lab environment.

                                         """;

            var readmePath = Path.Combine(tempDir, "README.md");
            await File.WriteAllTextAsync(readmePath, readmeContent);

            InitializeAndPushRepository(tempDir, project, projectName);

            Log.Information($"Repository populated and pushed to '{projectName}'");
            return true;
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
    }

    private string CreateTempDirectory(string projectName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cilab-{projectName}-{Guid.NewGuid():N}");
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
        catch
        {
            // Ignore cleanup errors
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

        var signature = new Signature("CI Lab Bootstrap", "bootstrap@cilab.local", DateTimeOffset.Now);
        repo.Commit($"Initial commit for {projectName}", signature, signature);

        var repoUrl = project.HttpUrlToRepo.Replace("http://", $"http://root:{_token}@");
        repo.Network.Remotes.Add("origin", repoUrl);

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