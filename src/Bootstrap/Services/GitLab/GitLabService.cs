using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LibGit2Sharp;
using Bootstrap.Services.Utilities;

namespace Bootstrap.Services.GitLab;

public class GitLabService
{
    public async Task<bool> ValidateGitLabTokenAsync(HttpClient client, string gitlabUrl, string token)
    {
        try
        {
            var apiUrl = $"{gitlabUrl.TrimEnd('/')}/api/v4/user";
            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl)
            {
                Headers = { { "PRIVATE-TOKEN", token } }
            };

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var userData = await response.Content.ReadFromJsonAsync<JsonElement>();
                var username = userData.GetProperty("username").GetString();
                Console.WriteLine($"[bootstrap]   Authenticated as: {username}");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bootstrap] ERROR:   Validation error: {ex.Message}");
            return false;
        }
    }

    public async Task<JsonElement?> CreateGitLabProjectAsync(HttpClient client, string gitlabUrl, string token, string projectName)
    {
        var apiUrl = $"{gitlabUrl.TrimEnd('/')}/api/v4/projects";
        Console.WriteLine($"[bootstrap] Creating GitLab project '{projectName}' via {apiUrl}");

        try
        {
            var checkUrl = $"{gitlabUrl.TrimEnd('/')}/api/v4/projects?search={projectName}";
            var checkRequest = new HttpRequestMessage(HttpMethod.Get, checkUrl)
            {
                Headers = { { "PRIVATE-TOKEN", token } }
            };

            var checkResponse = await client.SendAsync(checkRequest);
            if (checkResponse.IsSuccessStatusCode)
            {
                var existingProjects = await checkResponse.Content.ReadFromJsonAsync<JsonElement[]>();
                if (existingProjects is not null)
                {
                    foreach (var proj in existingProjects)
                    {
                        if (proj.GetProperty("name").GetString() == projectName)
                        {
                            Console.WriteLine($"[bootstrap]   Project '{projectName}' already exists");
                            return proj;
                        }
                    }
                }
            }

            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = JsonContent.Create(new { name = projectName, initialize_with_readme = false }),
                Headers = { { "PRIVATE-TOKEN", token } }
            };

            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                Console.WriteLine($"[bootstrap]   ✓ Project '{projectName}' created");
                return JsonSerializer.Deserialize<JsonElement>(content);
            }

            Console.Error.WriteLine($"[bootstrap] ERROR: GitLab API error {(int)response.StatusCode}: {content}");
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bootstrap] ERROR: Failed to call GitLab API: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> CreateAndPopulateGitLabProjectAsync(HttpClient client, string gitlabUrl, string token, string projectName, int projectNumber)
    {
        var project = await CreateGitLabProjectAsync(client, gitlabUrl, token, projectName);
        if (project is null || !project.HasValue)
        {
            return false;
        }

        var projectId = project.Value.GetProperty("id").GetInt32();
        var httpUrlToRepo = project.Value.GetProperty("http_url_to_repo").GetString();

        if (string.IsNullOrEmpty(httpUrlToRepo))
        {
            Console.Error.WriteLine($"[bootstrap] ERROR: Could not get repository URL for project '{projectName}'");
            return false;
        }

        var hasCommits = await CheckGitLabProjectHasCommitsAsync(client, gitlabUrl, token, projectId);
        if (hasCommits)
        {
            Console.WriteLine($"[bootstrap]   Project '{projectName}' already has commits, skipping repo population");
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
            File.WriteAllText(buildShPath, buildShContent);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(buildShPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                                  UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                                  UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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
            File.WriteAllText(readmePath, readmeContent);

            Repository.Init(tempDir);
            using var repo = new Repository(tempDir);

            Commands.Stage(repo, "*");

            var signature = new Signature("CI Lab Bootstrap", "bootstrap@cilab.local", DateTimeOffset.Now);
            repo.Commit($"Initial commit for {projectName}", signature, signature);

            var repoUrl = httpUrlToRepo.Replace("http://", $"http://root:{token}@");
            repo.Network.Remotes.Add("origin", repoUrl);

            var pushOptions = new PushOptions
            {
                CredentialsProvider = (_url, _user, _cred) => new UsernamePasswordCredentials
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

                repo.Branches.Update(localBranch, b =>
                {
                    b.Remote = "origin";
                    b.UpstreamBranch = $"refs/heads/{remoteName}";
                });
            }
            catch (Exception ex)
            {
                throw new Exception($"Git push failed for branch '{localName}': {ex.Message}", ex);
            }

            Console.WriteLine($"[bootstrap]   ✓ Repository populated and pushed to '{projectName}'");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bootstrap] ERROR: Failed to populate project '{projectName}': {ex.Message}");
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

    public async Task<bool> CheckGitLabProjectHasCommitsAsync(HttpClient client, string gitlabUrl, string token, int projectId)
    {
        try
        {
            var apiUrl = $"{gitlabUrl.TrimEnd('/')}/api/v4/projects/{projectId}/repository/commits";
            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl)
            {
                Headers = { { "PRIVATE-TOKEN", token } }
            };

            var response = await client.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (response.IsSuccessStatusCode)
            {
                var commits = await response.Content.ReadFromJsonAsync<JsonElement[]>();
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
