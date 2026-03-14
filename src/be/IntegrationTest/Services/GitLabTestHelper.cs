using IntegrationTest.Entities;
using RestSharp;
using Serilog;
using System.Net;
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
        _adminClient = new RestClient(
            new RestClientOptions(TestConfig.GitLabUrl)
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
        var request = new RestRequest("/api/v4/projects");
        request.AddQueryParameter("search", projectName);
        request.AddQueryParameter("per_page", "100");

        var response = _adminClient.Execute(request);
        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"Failed to search for project '{projectName}': {response.StatusCode} {response.Content}");
        }

        var projects = JsonSerializer.Deserialize<List<GitLabProjectInfo>>(response.Content!, JsonOptions)
                       ?? throw new InvalidOperationException(
                           $"Failed to deserialize projects for '{projectName}'");

        var match = projects.FirstOrDefault(p =>
            p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            throw new InvalidOperationException(
                $"Project '{projectName}' not found. Available: {string.Join(", ", projects.Select(p => p.Name))}");
        }

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
        {
            throw new InvalidOperationException(
                $"Failed to create branch '{branchName}': {branchResponse.StatusCode} {branchResponse.Content}");
        }

        Log.Information("Created branch '{BranchName}' in project {ProjectId}", branchName, projectId);

        // Create a commit as the test user so the push event is attributed to them
        var userToken = TestConfig.GetTestUserToken(username);
        var userClient = new RestClient(
            new RestClientOptions(TestConfig.GitLabUrl)
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            });

        userClient.AddDefaultHeader("PRIVATE-TOKEN", userToken);

        var filePath = $"changes/{branchName.Replace("/", "-")}-live-test.txt";
        var commitRequest = new RestRequest(
            $"/api/v4/projects/{projectId}/repository/files/{Uri.EscapeDataString(filePath)}",
            Method.Post);

        commitRequest.AddJsonBody(
            new
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
        {
            throw new InvalidOperationException(
                $"Failed to create commit on '{branchName}': {commitResponse.StatusCode} {commitResponse.Content}");
        }

        Log.Information(
            "Created commit on branch '{BranchName}' as user '{Username}'",
            branchName,
            username);
    }

    /// <summary>
    ///     Creates a merge request as the specified test user.
    ///     By default, the source branch will be deleted when the MR is merged.
    /// </summary>
    public int CreateMergeRequest(
        int projectId,
        string sourceBranch,
        string username,
        string? title = null,
        bool shouldDeleteSourceBranch = true)
    {
        var userToken = TestConfig.GetTestUserToken(username);
        var userClient = new RestClient(
            new RestClientOptions(TestConfig.GitLabUrl)
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            });

        userClient.AddDefaultHeader("PRIVATE-TOKEN", userToken);

        var request = new RestRequest($"/api/v4/projects/{projectId}/merge_requests", Method.Post);
        request.AddJsonBody(
            new
            {
                source_branch = sourceBranch,
                target_branch = "main",
                title = title ?? $"MR for {sourceBranch} (integration test)",
                should_remove_source_branch = shouldDeleteSourceBranch
            });

        var response = userClient.Execute(request);
        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"Failed to create MR for '{sourceBranch}': {response.StatusCode} {response.Content}");
        }

        var mr = JsonSerializer.Deserialize<GitLabMrInfo>(response.Content!, JsonOptions)
                 ?? throw new InvalidOperationException("Failed to deserialize MR response");

        Log.Information(
            "Created MR !{MrIid} for branch '{BranchName}' as '{Username}' (deleteSourceBranch={DeleteSourceBranch})",
            mr.Iid,
            sourceBranch,
            username,
            shouldDeleteSourceBranch);

        return mr.Iid;
    }

    /// <summary>
    ///     Approves a merge request as the specified test user.
    /// </summary>
    public void ApproveMergeRequest(int projectId, int mrIid, string approverUsername)
    {
        var userToken = TestConfig.GetTestUserToken(approverUsername);
        var userClient = new RestClient(
            new RestClientOptions(TestConfig.GitLabUrl)
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            });

        userClient.AddDefaultHeader("PRIVATE-TOKEN", userToken);

        var request = new RestRequest(
            $"/api/v4/projects/{projectId}/merge_requests/{mrIid}/approve",
            Method.Post);

        var response = userClient.Execute(request);
        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"Failed to approve MR !{mrIid}: {response.StatusCode} {response.Content}");
        }

        Log.Information(
            "Approved MR !{MrIid} in project {ProjectId} as '{Username}'",
            mrIid,
            projectId,
            approverUsername);
    }

    /// <summary>
    ///     Deletes a branch from a project.
    /// </summary>
    public void DeleteBranch(int projectId, string branchName)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        var request = new RestRequest(
            $"/api/v4/projects/{projectId}/repository/branches/{encodedBranch}",
            Method.Delete);

        var response = _adminClient.Execute(request);

        if (!response.IsSuccessful && response.StatusCode != HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Failed to delete branch '{branchName}': {response.StatusCode} {response.Content}");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            Log.Information(
                "Branch '{BranchName}' in project {ProjectId} was already deleted",
                branchName,
                projectId);

            return;
        }

        Log.Information("Deleted branch '{BranchName}' in project {ProjectId}", branchName, projectId);
    }

    /// <summary>
    ///     Gets the HEAD commit SHA for a branch.
    /// </summary>
    public string GetBranchHeadSha(int projectId, string branchName)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        var request = new RestRequest($"/api/v4/projects/{projectId}/repository/branches/{encodedBranch}");

        var response = _adminClient.Execute(request);
        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"Failed to get branch '{branchName}': {response.StatusCode} {response.Content}");
        }

        var branch = JsonSerializer.Deserialize<GitLabBranchInfo>(response.Content!, JsonOptions)
                     ?? throw new InvalidOperationException(
                         $"Failed to deserialize branch info for '{branchName}'");

        Log.Information("Branch '{BranchName}' HEAD SHA: {Sha}", branchName, branch.Commit.Id);
        return branch.Commit.Id;
    }

    /// <summary>
    ///     Sets an external pipeline status on a commit via the GitLab Commit Statuses API.
    ///     This creates or updates a pipeline status that affects merge readiness.
    /// </summary>
    public void SetCommitStatus(
        int projectId,
        string sha,
        string state,
        string pipelineName = "integration-test")
    {
        var request = new RestRequest($"/api/v4/projects/{projectId}/statuses/{sha}", Method.Post);
        request.AddJsonBody(
            new { state, name = pipelineName, description = $"Integration test pipeline: {state}" });

        var response = _adminClient.Execute(request);
        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"Failed to set commit status '{state}' on {sha}: {response.StatusCode} {response.Content}");
        }

        Log.Information(
            "Set commit status '{State}' on SHA {Sha} in project {ProjectId}",
            state,
            sha[..8],
            projectId);
    }

    /// <summary>
    ///     Gets merge request details including merge status.
    /// </summary>
    public GitLabMrDetail GetMergeRequestDetail(int projectId, int mrIid)
    {
        var request = new RestRequest($"/api/v4/projects/{projectId}/merge_requests/{mrIid}");
        var response = _adminClient.Execute(request);
        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"Failed to get MR !{mrIid}: {response.StatusCode} {response.Content}");
        }

        return JsonSerializer.Deserialize<GitLabMrDetail>(response.Content!, JsonOptions)
               ?? throw new InvalidOperationException($"Failed to deserialize MR !{mrIid}");
    }

    /// <summary>
    ///     Pushes a commit to the default branch (main) of a project to make
    ///     other branches diverge from it. Returns the new commit SHA.
    /// </summary>
    public string PushCommitToMain(int projectId, string message = "Push to main for divergence test")
    {
        var filePath = $"divergence-test-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
        var request = new RestRequest(
            $"/api/v4/projects/{projectId}/repository/files/{Uri.EscapeDataString(filePath)}",
            Method.Post);

        request.AddJsonBody(
            new
            {
                branch = "main",
                content = $"Divergence test at {DateTime.UtcNow:O}",
                commit_message = message
            });

        var response = _adminClient.Execute(request);
        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"Failed to push commit to main: {response.StatusCode} {response.Content}");
        }

        // Get the new main branch SHA
        var sha = GetBranchHeadSha(projectId, "main");
        Log.Information("Pushed commit to main in project {ProjectId}, new HEAD: {Sha}", projectId, sha[..8]);
        return sha;
    }

    /// <summary>
    ///     Accepts (merges) a merge request using the admin token.
    ///     The MR must be in a mergeable state.
    /// </summary>
    public void AcceptMergeRequest(int projectId, int mrIid)
    {
        var request = new RestRequest(
            $"/api/v4/projects/{projectId}/merge_requests/{mrIid}/merge",
            Method.Put);

        var response = _adminClient.Execute(request);
        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"Failed to accept MR !{mrIid} in project {projectId}: {response.StatusCode} {response.Content}");
        }

        Log.Information("Accepted MR !{MrIid} in project {ProjectId}", mrIid, projectId);
    }

    /// <summary>
    ///     Updates whether the project requires all pipelines to pass before merging.
    ///     Use this to temporarily relax the CI requirement when merging in tests that
    ///     don't have a CI pipeline configured (e.g., projects set up with
    ///     <c>only_allow_merge_if_pipeline_succeeds = true</c> by the bootstrapper).
    /// </summary>
    public void SetProjectPipelineRequirement(int projectId, bool required)
    {
        var request = new RestRequest($"/api/v4/projects/{projectId}", Method.Put);
        request.AddJsonBody(new { only_allow_merge_if_pipeline_succeeds = required });

        var response = _adminClient.Execute(request);
        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"Failed to update pipeline requirement for project {projectId}: {response.StatusCode} {response.Content}");
        }

        Log.Information(
            "Set project {ProjectId} pipeline requirement to {Required}",
            projectId,
            required);
    }

    /// <summary>
    ///     Closes an open merge request.
    /// </summary>
    public void CloseMergeRequest(int projectId, int mrIid)
    {
        var request = new RestRequest($"/api/v4/projects/{projectId}/merge_requests/{mrIid}", Method.Put);
        request.AddJsonBody(new { state_event = "close" });

        var response = _adminClient.Execute(request);
        if (!response.IsSuccessful)
        {
            Log.Warning("Failed to close MR !{MrIid}: {Status}", mrIid, response.StatusCode);
        }
        else
        {
            Log.Information("Closed MR !{MrIid} in project {ProjectId}", mrIid, projectId);
        }
    }
}