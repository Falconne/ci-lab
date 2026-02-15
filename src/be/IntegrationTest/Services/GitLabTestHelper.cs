using IntegrationTest.Entities;
using RestSharp;
using Serilog;
using System.Text.Json;

namespace IntegrationTest.Services;

/// <summary>
///     Helper for interacting with the GitLab API during integration tests.
///     Uses the test user's PAT to create branches and commits so that
///     push events are attributed to the correct user.
/// </summary>
public class GitLabTestHelper
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly RestClient _adminClient;

    public GitLabTestHelper()
    {
        var adminToken = TestConfig.GetGitLabAdminToken();
        _adminClient = new RestClient(new RestClientOptions(TestConfig.GitLabUrl)
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        });
        _adminClient.AddDefaultHeader("PRIVATE-TOKEN", adminToken);
    }

    /// <summary>
    ///     Finds a project by name. Returns the project ID.
    /// </summary>
    public int GetProjectId(string projectName)
    {
        var request = new RestRequest("/api/v4/projects", Method.Get);
        request.AddQueryParameter("search", projectName);
        request.AddQueryParameter("per_page", "100");

        var response = _adminClient.Execute(request);
        if (!response.IsSuccessful)
            throw new InvalidOperationException(
                $"Failed to search for project '{projectName}': {response.StatusCode} {response.Content}");

        var projects = JsonSerializer.Deserialize<List<GitLabProjectInfo>>(response.Content!, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize projects for '{projectName}'");

        var match = projects.FirstOrDefault(p =>
            p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

        if (match == null)
            throw new InvalidOperationException(
                $"Project '{projectName}' not found. Available: {string.Join(", ", projects.Select(p => p.Name))}");

        Log.Information("Found project '{ProjectName}' with ID {ProjectId}", projectName, match.Id);
        return match.Id;
    }

    /// <summary>
    ///     Creates a branch and pushes a commit as the specified test user,
    ///     so the push event is attributed to them.
    /// </summary>
    public void CreateBranchWithCommit(int projectId, string branchName, string username)
    {
        // Create the branch using the admin token
        var branchRequest = new RestRequest($"/api/v4/projects/{projectId}/repository/branches", Method.Post);
        branchRequest.AddJsonBody(new { branch = branchName, @ref = "main" });

        var branchResponse = _adminClient.Execute(branchRequest);
        if (!branchResponse.IsSuccessful && !branchResponse.Content!.Contains("already exists"))
            throw new InvalidOperationException(
                $"Failed to create branch '{branchName}': {branchResponse.StatusCode} {branchResponse.Content}");

        Log.Information("Created branch '{BranchName}' in project {ProjectId}", branchName, projectId);

        // Create a commit as the test user so the push event is attributed to them
        var userToken = TestConfig.GetTestUserToken(username);
        var userClient = new RestClient(new RestClientOptions(TestConfig.GitLabUrl)
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        });
        userClient.AddDefaultHeader("PRIVATE-TOKEN", userToken);

        var filePath = $"changes/{branchName.Replace("/", "-")}-live-test.txt";
        var commitRequest = new RestRequest(
            $"/api/v4/projects/{projectId}/repository/files/{Uri.EscapeDataString(filePath)}", Method.Post);
        commitRequest.AddJsonBody(new
        {
            branch = branchName,
            content = $"Live test change by {username} at {DateTime.UtcNow:O}",
            commit_message = $"Live test commit for {branchName}"
        });

        var commitResponse = userClient.Execute(commitRequest);
        if (!commitResponse.IsSuccessful)
        {
            // File might already exist from a previous run, try PUT instead
            commitRequest.Method = Method.Put;
            commitResponse = userClient.Execute(commitRequest);
        }

        if (!commitResponse.IsSuccessful)
            throw new InvalidOperationException(
                $"Failed to create commit on '{branchName}': {commitResponse.StatusCode} {commitResponse.Content}");

        Log.Information(
            "Created commit on branch '{BranchName}' as user '{Username}'",
            branchName,
            username);
    }

    /// <summary>
    ///     Creates a merge request as the specified test user.
    /// </summary>
    public int CreateMergeRequest(int projectId, string sourceBranch, string username)
    {
        var userToken = TestConfig.GetTestUserToken(username);
        var userClient = new RestClient(new RestClientOptions(TestConfig.GitLabUrl)
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        });
        userClient.AddDefaultHeader("PRIVATE-TOKEN", userToken);

        var request = new RestRequest($"/api/v4/projects/{projectId}/merge_requests", Method.Post);
        request.AddJsonBody(new
        {
            source_branch = sourceBranch,
            target_branch = "main",
            title = $"MR for {sourceBranch} (integration test)"
        });

        var response = userClient.Execute(request);
        if (!response.IsSuccessful)
            throw new InvalidOperationException(
                $"Failed to create MR for '{sourceBranch}': {response.StatusCode} {response.Content}");

        var mr = JsonSerializer.Deserialize<GitLabMrInfo>(response.Content!, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize MR response");

        Log.Information(
            "Created MR !{MrIid} for branch '{BranchName}' as '{Username}'",
            mr.Iid,
            sourceBranch,
            username);

        return mr.Iid;
    }

    /// <summary>
    ///     Approves a merge request as the specified test user.
    /// </summary>
    public void ApproveMergeRequest(int projectId, int mrIid, string approverUsername)
    {
        var userToken = TestConfig.GetTestUserToken(approverUsername);
        var userClient = new RestClient(new RestClientOptions(TestConfig.GitLabUrl)
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        });
        userClient.AddDefaultHeader("PRIVATE-TOKEN", userToken);

        var request = new RestRequest(
            $"/api/v4/projects/{projectId}/merge_requests/{mrIid}/approve", Method.Post);

        var response = userClient.Execute(request);
        if (!response.IsSuccessful)
            throw new InvalidOperationException(
                $"Failed to approve MR !{mrIid}: {response.StatusCode} {response.Content}");

        Log.Information(
            "Approved MR !{MrIid} in project {ProjectId} as '{Username}'",
            mrIid,
            projectId,
            approverUsername);
    }
}
