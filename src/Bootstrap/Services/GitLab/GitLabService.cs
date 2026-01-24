using Bootstrap.Services.Utilities;
using LibGit2Sharp;
using Serilog;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bootstrap.Entities.GitLab;

namespace Bootstrap.Services.GitLab;

public class GitLabService
{
    private static string BuildApiUrl(string gitlabUrl, string endpoint)
    {
        return ApiUrlHelper.BuildUrl(gitlabUrl, "api/v4", endpoint);
    }

    public static async Task<bool> ValidateGitLabToken(HttpClient client, string gitlabUrl, string token)
    {
        try
        {
            var apiUrl = BuildApiUrl(gitlabUrl, "user");
            var request = HttpRequestHelper.CreateWithPrivateToken(HttpMethod.Get, apiUrl, token);

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var user = await response.Content.ReadFromJsonAsync<GitLabUser>();
                var username = user?.Username;
                Log.Information($"Authenticated as: {username}");
                return true;
            }

            // Log detailed error information when validation fails
            var responseBody = await response.Content.ReadAsStringAsync();
            Log.Error(
                $"GitLab token validation failed: {(int)response.StatusCode} {response.StatusCode}");

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

    public static async Task<JsonElement?> CreateGitLabProject(
        HttpClient client,
        string gitlabUrl,
        string token,
        string projectName)

    {
        var apiUrl = BuildApiUrl(gitlabUrl, "projects");
        Log.Information($"Creating GitLab project '{projectName}' via {apiUrl}");

        try
        {
            var checkUrl = BuildApiUrl(gitlabUrl, $"projects?search={projectName}");
            var checkRequest = HttpRequestHelper.CreateWithPrivateToken(HttpMethod.Get, checkUrl, token);

            var checkResponse = await client.SendAsync(checkRequest);
            if (checkResponse.IsSuccessStatusCode)
            {
                var existingProjects = await checkResponse.Content.ReadFromJsonAsync<GitLabProject[]>();
                if (existingProjects is not null)
                {
                    foreach (var proj in existingProjects)
                    {
                        if (proj.Name == projectName)
                        {
                            Log.Information($"Project '{projectName}' already exists");
                            // Convert to JsonElement to preserve existing return contract
                            var json = JsonSerializer.SerializeToElement(proj);
                            return json;
                        }
                    }
                }
            }

            var request = HttpRequestHelper.CreateWithPrivateToken(HttpMethod.Post, apiUrl, token);
            request.Content = JsonContent.Create(new { name = projectName, initialize_with_readme = false });

            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                Log.Information($"Project '{projectName}' created");
                // Deserialize to GitLabProject then return as JsonElement to keep caller expectations
                try
                {
                    var proj = JsonSerializer.Deserialize<GitLabProject>(content);
                    if (proj is not null)
                    {
                        return JsonSerializer.SerializeToElement(proj);
                    }
                }
                catch
                {
                    return JsonSerializer.Deserialize<JsonElement>(content);
                }
                return null;
            }

            Log.Error($"GitLab API error {(int)response.StatusCode}: {content}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to call GitLab API: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> CreateAndPopulateGitLabProject(
        HttpClient client,
        string gitlabUrl,
        string token,
        string projectName,
        int projectNumber)
    {
        var project = await CreateGitLabProject(client, gitlabUrl, token, projectName);
        if (project is null)
        {
            return false;
        }

        int projectId = 0;
        string? httpUrlToRepo = null;

        // Try to get properties safely
        if (project.Value.TryGetProperty("id", out var idProp))
        {
            projectId = idProp.GetInt32();
        }

        if (project.Value.TryGetProperty("http_url_to_repo", out var urlProp))
        {
            httpUrlToRepo = urlProp.GetString();
        }

        // If possible, map JsonElement to strong type for clarity
        try
        {
            var mapped = JsonSerializer.Deserialize<GitLabProject>(project.Value.GetRawText());
            if (mapped is not null)
            {
                projectId = mapped.Id;
                httpUrlToRepo = mapped.HttpUrlToRepo ?? httpUrlToRepo;
            }
        }
        catch
        {
        }

        if (string.IsNullOrEmpty(httpUrlToRepo))
        {
            await Console.Error.WriteLineAsync(
                $"[bootstrap] ERROR: Could not get repository URL for project '{projectName}'");

            return false;
        }

        var hasCommits = await CheckGitLabProjectHasCommits(client, gitlabUrl, token, projectId);
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

    public async Task<bool> CheckGitLabProjectHasCommits(
        HttpClient client,
        string gitlabUrl,
        string token,
        int projectId)
    {
        try
        {
            var apiUrl = BuildApiUrl(
                gitlabUrl,
                $"projects/{projectId}/repository/commits");

            var request = HttpRequestHelper.CreateWithPrivateToken(HttpMethod.Get, apiUrl, token);

            var response = await client.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (response.IsSuccessStatusCode)
            {
                var commits = await response.Content.ReadFromJsonAsync<GitLabCommit[]>();
                return commits is not null && commits.Length > 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}