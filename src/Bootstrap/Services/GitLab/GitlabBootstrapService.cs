using Bootstrap.Entities.Gitlab;
using Bootstrap.Services.Utilities;
using LibGit2Sharp;
using Serilog;
using System.Net;
using System.Net.Http.Json;

namespace Bootstrap.Services.Gitlab;

public class GitlabBootstrapService
{
    private readonly HttpClient _httpClient;

    public GitlabBootstrapService(string gitlabUrl, HttpClient httpClient)
    {
        GitlabUrl = gitlabUrl;
        _httpClient = httpClient;
    }

    public string GitlabUrl { get; }

    public async Task<bool> ValidateGitlabToken(string token)
    {
        try
        {
            var apiUrl = BuildApiUrl("user");
            var request = HttpRequestHelper.CreateWithPrivateToken(HttpMethod.Get, apiUrl, token);

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var user = await response.Content.ReadFromJsonAsync<GitlabUser>();
                var username = user?.Username;
                Log.Information($"Authenticated as: {username}");
                return true;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            Log.Error(
                $"Gitlab token validation failed: {(int)response.StatusCode} {response.StatusCode}");

            if (!string.IsNullOrWhiteSpace(responseBody) && responseBody.Length < 500)
            {
                Log.Error($"Response: {responseBody}");
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Validation error: {ex.Message}");
            return false;
        }
    }

    public async Task<GitlabProject> CreateGitlabProject(
        string token,
        string projectName)

    {
        var apiUrl = BuildApiUrl("projects");
        Log.Information($"Creating Gitlab project '{projectName}' via {apiUrl}");

        try
        {
            var checkUrl = BuildApiUrl($"projects?search={projectName}");
            var checkRequest = HttpRequestHelper.CreateWithPrivateToken(HttpMethod.Get, checkUrl, token);

            var checkResponse = await _httpClient.SendAsync(checkRequest);
            if (checkResponse.IsSuccessStatusCode)
            {
                var existingProjects = await checkResponse.Content.ReadFromJsonAsync<GitlabProject[]>();
                if (existingProjects is not null)
                {
                    foreach (var proj in existingProjects)
                    {
                        if (proj.Name == projectName)
                        {
                            Log.Information($"Project '{projectName}' already exists");
                            return proj;
                        }
                    }
                }
            }

            var request = HttpRequestHelper.CreateWithPrivateToken(HttpMethod.Post, apiUrl, token);
            request.Content = JsonContent.Create(new { name = projectName, initialize_with_readme = false });

            var response = await _httpClient.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                Log.Information($"Project '{projectName}' created");
                var project = await response.Content.ReadFromJsonAsync<GitlabProject>();
                if (project is null)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    throw new InvalidOperationException(
                        $"Failed to deserialize Gitlab project response: {content}");
                }

                return project;
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Error($"Gitlab API error {(int)response.StatusCode}: {errorContent}");
            throw new InvalidOperationException(
                $"Failed to create Gitlab project '{projectName}': {(int)response.StatusCode} {response.StatusCode} - {errorContent}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to call Gitlab API: {ex.Message}");
            throw;
        }
    }

    public async Task<bool> CreateAndPopulateGitlabProject(
        string token,
        string projectName,
        int projectNumber)
    {
        var project = await CreateGitlabProject(token, projectName);

        var projectId = project.Id;
        var httpUrlToRepo = project.HttpUrlToRepo;

        var hasCommits = await CheckGitlabProjectHasCommits(token, projectId);
        if (hasCommits)
        {
            Log.Information($"Project '{projectName}' already has commits, skipping repo population");
            return true;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"cilab-{projectName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

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

            Repository.Init(tempDir);
            using var repo = new Repository(tempDir);

            Commands.Stage(repo, "*");

            var signature = new Signature("CI Lab Bootstrap", "bootstrap@cilab.local", DateTimeOffset.Now);
            repo.Commit($"Initial commit for {projectName}", signature, signature);

            var repoUrl = httpUrlToRepo.Replace("http://", $"http://root:{token}@");
            repo.Network.Remotes.Add("origin", repoUrl);

            var pushOptions = new PushOptions
            {
                CredentialsProvider = (url, user, cred) => new UsernamePasswordCredentials
                {
                    Username = "root",
                    Password = token
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

            Log.Information($"Repository populated and pushed to '{projectName}'");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to populate project '{projectName}': {ex.Message}");
            return false;
        }
        finally
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
            }
        }
    }

    public async Task<bool> CheckGitlabProjectHasCommits(
        string token,
        int projectId)
    {
        try
        {
            var apiUrl = BuildApiUrl($"projects/{projectId}/repository/commits");

            var request = HttpRequestHelper.CreateWithPrivateToken(HttpMethod.Get, apiUrl, token);

            var response = await _httpClient.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (response.IsSuccessStatusCode)
            {
                var commits = await response.Content.ReadFromJsonAsync<GitlabCommit[]>();
                return commits is not null && commits.Length > 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private string BuildApiUrl(string endpoint)
    {
        return ApiUrlHelper.BuildUrl(GitlabUrl, "api/v4", endpoint);
    }
}