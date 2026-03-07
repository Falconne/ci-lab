using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Utilities;
using System.Text.Json;

namespace Mergician.Services.Gitlab;

public class GitlabPipelineService
{
    private static readonly JsonSerializerOptions _jsonOptions = JsonOptions.CaseInsensitive;

    private readonly GitLabApiClient _gitLabApiClient;

    private readonly ILogger<GitlabPipelineService> _logger;

    public GitlabPipelineService(
        GitLabApiClient gitLabApiClient,
        ILogger<GitlabPipelineService> logger)
    {
        _gitLabApiClient = gitLabApiClient;
        _logger = logger;
    }

    /// <summary>
    ///     Returns external job-like build statuses for the latest pipeline on the branch.
    /// </summary>
    public async Task<List<BranchBuildJob>> GetLatestExternalJobsForBranch(
        AccessDetailsBase accessDetails,
        int projectId,
        string branchName,
        CancellationToken cancellationToken = default)
    {
        var commitSha = await GetBranchHeadCommitSha(
            accessDetails,
            projectId,
            branchName,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(commitSha))
        {
            _logger.LogDebug(
                "No head commit found for branch '{BranchName}' in project {ProjectId}; skipping build status lookup",
                branchName,
                projectId);

            return [];
        }

        var latestPipeline = await GetLatestPipeline(
            accessDetails,
            projectId,
            branchName,
            cancellationToken);

        if (latestPipeline == null)
        {
            _logger.LogDebug(
                "No pipeline found for branch '{BranchName}' in project {ProjectId}; returning no external jobs",
                branchName,
                projectId);

            return [];
        }

        var externalJobs = await GetExternalJobsFromPipeline(
            accessDetails,
            projectId,
            latestPipeline.Id,
            cancellationToken);

        if (externalJobs.Count > 0)
        {
            _logger.LogDebug(
                "Resolved {Count} external jobs from pipeline {PipelineId} for branch '{BranchName}' in project {ProjectId}",
                externalJobs.Count,
                latestPipeline.Id,
                branchName,
                projectId);

            return externalJobs;
        }

        _logger.LogDebug(
            "No stage=external jobs in pipeline {PipelineId} for branch '{BranchName}' in project {ProjectId}; checking commit statuses fallback",
            latestPipeline.Id,
            branchName,
            projectId);

        var fallbackStatuses = await GetExternalStatusesFromCommit(
            accessDetails,
            projectId,
            commitSha,
            latestPipeline.Id,
            cancellationToken);

        _logger.LogDebug(
            "Resolved {Count} fallback commit statuses for branch '{BranchName}' in project {ProjectId}",
            fallbackStatuses.Count,
            branchName,
            projectId);

        return fallbackStatuses;
    }

    private async Task<string?> GetBranchHeadCommitSha(
        AccessDetailsBase accessDetails,
        int projectId,
        string branchName,
        CancellationToken cancellationToken)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        var operationName = $"GetBranchHeadCommitSha(projectId={projectId}, branchName={branchName})";

        return await _gitLabApiClient.ExecuteAsync(
            async (client, token) =>
            {
                using var request = accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/repository/branches/{encodedBranch}");

                using var response = await client.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    if (GitLabApiClient.IsRetriableStatusCode(response.StatusCode))
                    {
                        throw new GitLabUnexpectedResponseException(operationName, response.StatusCode);
                    }

                    _logger.LogError(
                        "GetBranchHeadCommitSha failed with status {StatusCode} for branch '{BranchName}' in project {ProjectId}",
                        (int)response.StatusCode,
                        branchName,
                        projectId);

                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(token);
                var details = GitLabApiClient.DeserializeOrThrow<GitLabBranchDetails>(
                    json,
                    _jsonOptions,
                    operationName);
                if (string.IsNullOrWhiteSpace(details.Commit?.Id))
                {
                    _logger.LogError(
                        "GetBranchHeadCommitSha returned no commit id for branch '{BranchName}' in project {ProjectId}",
                        branchName,
                        projectId);

                    return null;
                }

                return details.Commit.Id;
            },
            operationName,
            GitLabApiFailureBehavior.Throw,
            cancellationToken);
    }

    private async Task<GitLabPipeline?> GetLatestPipeline(
        AccessDetailsBase accessDetails,
        int projectId,
        string branchName,
        CancellationToken cancellationToken)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        var operationName = $"GetLatestPipeline(projectId={projectId}, branchName={branchName})";

        return await _gitLabApiClient.ExecuteAsync(
            async (client, token) =>
            {
                using var request = accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/pipelines?ref={encodedBranch}&order_by=updated_at&sort=desc&per_page=1");

                using var response = await client.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    if (GitLabApiClient.IsRetriableStatusCode(response.StatusCode))
                    {
                        throw new GitLabUnexpectedResponseException(operationName, response.StatusCode);
                    }

                    _logger.LogError(
                        "GetLatestPipeline failed with status {StatusCode} for branch '{BranchName}' in project {ProjectId}",
                        (int)response.StatusCode,
                        branchName,
                        projectId);

                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(token);
                var pipelines = GitLabApiClient.DeserializeOrThrow<List<GitLabPipeline>>(
                    json,
                    _jsonOptions,
                    operationName);
                return pipelines.FirstOrDefault();
            },
            operationName,
            GitLabApiFailureBehavior.Throw,
            cancellationToken);
    }

    private async Task<List<BranchBuildJob>> GetExternalJobsFromPipeline(
        AccessDetailsBase accessDetails,
        int projectId,
        int pipelineId,
        CancellationToken cancellationToken)
    {
        var operationName = $"GetExternalJobsFromPipeline(projectId={projectId}, pipelineId={pipelineId})";

        return await _gitLabApiClient.ExecuteAsync(
            async (client, token) =>
            {
                using var request = accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/pipelines/{pipelineId}/jobs?per_page=100");

                using var response = await client.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    if (GitLabApiClient.IsRetriableStatusCode(response.StatusCode))
                    {
                        throw new GitLabUnexpectedResponseException(operationName, response.StatusCode);
                    }

                    _logger.LogError(
                        "GetExternalJobsFromPipeline failed with status {StatusCode} for project {ProjectId}, pipeline {PipelineId}",
                        (int)response.StatusCode,
                        projectId,
                        pipelineId);

                    return [];
                }

                var json = await response.Content.ReadAsStringAsync(token);
                var jobs = GitLabApiClient.DeserializeOrThrow<List<GitLabPipelineJob>>(
                    json,
                    _jsonOptions,
                    operationName);

                return jobs
                    .Where(job => string.Equals(job.Stage, "external", StringComparison.OrdinalIgnoreCase))
                    .Select(job => new BranchBuildJob(
                        string.IsNullOrWhiteSpace(job.Name) ? "external-job" : job.Name,
                        string.IsNullOrWhiteSpace(job.Status) ? "unknown" : job.Status,
                        string.IsNullOrWhiteSpace(job.WebUrl) ? null : job.WebUrl))
                    .ToList();
            },
            operationName,
            GitLabApiFailureBehavior.Throw,
            cancellationToken);
    }

    private async Task<List<BranchBuildJob>> GetExternalStatusesFromCommit(
        AccessDetailsBase accessDetails,
        int projectId,
        string commitSha,
        int pipelineId,
        CancellationToken cancellationToken)
    {
        var encodedCommit = Uri.EscapeDataString(commitSha);
        var operationName =
            $"GetExternalStatusesFromCommit(projectId={projectId}, commitSha={commitSha}, pipelineId={pipelineId})";

        return await _gitLabApiClient.ExecuteAsync(
            async (client, token) =>
            {
                using var request = accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/repository/commits/{encodedCommit}/statuses?pipeline_id={pipelineId}&per_page=100");

                using var response = await client.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    if (GitLabApiClient.IsRetriableStatusCode(response.StatusCode))
                    {
                        throw new GitLabUnexpectedResponseException(operationName, response.StatusCode);
                    }

                    _logger.LogError(
                        "GetExternalStatusesFromCommit failed with status {StatusCode} for project {ProjectId}, commit {CommitSha}, pipeline {PipelineId}",
                        (int)response.StatusCode,
                        projectId,
                        commitSha,
                        pipelineId);

                    return [];
                }

                var json = await response.Content.ReadAsStringAsync(token);
                var statuses = GitLabApiClient.DeserializeOrThrow<List<GitLabCommitStatus>>(
                    json,
                    _jsonOptions,
                    operationName);

                return statuses
                    .Select(status => new BranchBuildJob(
                        string.IsNullOrWhiteSpace(status.Name) ? "external-job" : status.Name,
                        string.IsNullOrWhiteSpace(status.Status) ? "unknown" : status.Status,
                        status.TargetUrl))
                    .ToList();
            },
            operationName,
            GitLabApiFailureBehavior.Throw,
            cancellationToken);
    }
}