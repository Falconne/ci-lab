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

    public async Task<GitlabProject> CreateProject(string projectName)
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

        var createRequest = new RestRequest("projects", Method.Post)
            .AddJsonBody(new { name = projectName, initialize_with_readme = false });

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

    public async Task<bool> CreateTopLevelProject(
        string projectName,
        int projectNumber)
    {
        var project = await CreateProject(projectName);

        var hasCommits = await CheckProjectHasCommits(project.Id);
        if (hasCommits)
        {
            Log.Information($"Project '{projectName}' already has commits, skipping repo population");
            return true;
        }

        var tempDir = CreateTempDirectory(projectName);

        try
        {
            var random = new Random(projectNumber * 1000 + DateTime.UtcNow.Millisecond);
            var sleepDuration = random.Next(10, 61);

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

            var readmeContent = $"""
                                 # {projectName}

                                 This is test project #{projectNumber} for the CI lab environment.

                                 ## Build

                                 To build this project, run:

                                 ```bash
                                 ./build.sh
                                 ```

                                 The build simulates compilation and takes approximately {sleepDuration} seconds.

                                 ## Project Details

                                 - Project Name: {projectName}
                                 - Project Number: {projectNumber}
                                 - Build Duration: ~{sleepDuration}s

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

    public async Task<bool> CreateSubProject(
        string projectName,
        int projectNumber)
    {
        var project = await CreateProject(projectName);

        var hasCommits = await CheckProjectHasCommits(project.Id);
        if (hasCommits)
        {
            Log.Information($"Project '{projectName}' already has commits, skipping repo population");
            return true;
        }

        var tempDir = CreateTempDirectory(projectName);

        try
        {
            var readmeContent = $"""
                                 # {projectName}

                                 This is test sub-project #{projectNumber} for the CI lab environment.

                                 ## Project Details

                                 - Project Name: {projectName}
                                 - Project Number: {projectNumber}

                                 """;

            var readmePath = Path.Combine(tempDir, "README.md");
            await File.WriteAllTextAsync(readmePath, readmeContent);

            InitializeAndPushRepository(tempDir, project, projectName);

            Log.Information($"Sub-project repository populated and pushed to '{projectName}'");
            return true;
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
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