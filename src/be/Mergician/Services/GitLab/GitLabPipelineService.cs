using Mergician.Entities;
using Mergician.Services.Authentication;
using Util;

namespace Mergician.Services.GitLab;

public class GitLabPipelineService
{
    private readonly GitLabApiClient _gitLabApiClient;

    private readonly ILogger<GitLabPipelineService> _logger;

    public GitLabPipelineService(
        GitLabApiClient gitLabApiClient,
        ILogger<GitLabPipelineService> logger)
    {
        _gitLabApiClient = gitLabApiClient;
        _logger = logger;
    }

    /// <summary>
    ///     Returns build job statuses for the latest pipeline on the branch.
    /// </summary>
    public async Task<List<BranchBuildJob>> GetLatestBuildJobsForBranch(
        AccessDetailsBase accessDetails,
        int projectId,
        string branchName,
        CancellationToken cancellationToken)
    {
        var latestPipeline = await GetLatestPipeline(
            accessDetails,
            projectId,
            branchName,
            cancellationToken);

        if (latestPipeline == null)
        {
            _logger.LogDebug(
                "No pipeline found for branch '{BranchName}' in project {ProjectId}; returning no build jobs",
                branchName,
                projectId);

            return [];
        }

        var buildJobs = await GetJobsFromPipeline(
            accessDetails,
            projectId,
            latestPipeline.Id,
            cancellationToken);

        _logger.LogDebug(
            "Resolved {Count} jobs from pipeline {PipelineId} for branch '{BranchName}' in project {ProjectId}",
            buildJobs.Count,
            latestPipeline.Id,
            branchName,
            projectId);

        return buildJobs;
    }

    private async Task<GitLabPipeline?> GetLatestPipeline(
        AccessDetailsBase accessDetails,
        int projectId,
        string branchName,
        CancellationToken cancellationToken)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);

        try
        {
            var pipelines = await _gitLabApiClient.ExecuteAsync<List<GitLabPipeline>>(
                () => accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/pipelines?ref={encodedBranch}&order_by=updated_at&sort=desc&per_page=1"),
                cancellationToken);

            return pipelines.FirstOrDefault();
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError(
                "GetLatestPipeline failed with status {StatusCode} for branch '{BranchName}' in project {ProjectId}",
                (int)ex.StatusCode,
                branchName,
                projectId);

            return null;
        }
    }

    private async Task<List<BranchBuildJob>> GetJobsFromPipeline(
        AccessDetailsBase accessDetails,
        int projectId,
        int pipelineId,
        CancellationToken cancellationToken)
    {
        List<GitLabPipelineJob> jobs;

        try
        {
            jobs = await _gitLabApiClient.ExecuteAsync<List<GitLabPipelineJob>>(
                () => accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/pipelines/{pipelineId}/jobs?per_page=100"),
                cancellationToken);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError(
                "GetJobsFromPipeline failed with status {StatusCode} for project {ProjectId}, pipeline {PipelineId}",
                (int)ex.StatusCode,
                projectId,
                pipelineId);

            return [];
        }

        return jobs
            .Select(job => new BranchBuildJob(
                job.Name.IsEmpty() ? "job" : job.Name,
                job.Status.IsEmpty() ? "unknown" : job.Status,
                job.WebUrl.IsEmpty() ? null : job.WebUrl))
            .ToList();
    }
}