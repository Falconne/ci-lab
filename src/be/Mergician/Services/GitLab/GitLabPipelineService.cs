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
    ///     For external pipelines (e.g. TeamCity commit status publisher), commit statuses
    ///     are fetched instead of pipeline jobs, since external pipelines have no jobs.
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

        // External pipelines (e.g. from TeamCity's commit status publisher) have no jobs.
        // Fall back to commit statuses for the pipeline's commit SHA.
        if (latestPipeline.Source == "external")
        {
            _logger.LogDebug(
                "Pipeline {PipelineId} for branch '{BranchName}' in project {ProjectId} is external; fetching commit statuses for SHA {Sha}",
                latestPipeline.Id,
                branchName,
                projectId,
                latestPipeline.Sha);

            var commitStatusJobs = await GetJobsFromCommitStatuses(
                accessDetails,
                projectId,
                latestPipeline.Sha,
                cancellationToken);

            _logger.LogDebug(
                "Resolved {Count} commit status jobs for branch '{BranchName}' in project {ProjectId} (SHA {Sha})",
                commitStatusJobs.Count,
                branchName,
                projectId,
                latestPipeline.Sha);

            return commitStatusJobs;
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
            var pipelines = await _gitLabApiClient.Execute<List<GitLabPipeline>>(
                () => accessDetails.CreateRequest(
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

    private async Task<List<BranchBuildJob>> GetJobsFromCommitStatuses(
        AccessDetailsBase accessDetails,
        int projectId,
        string sha,
        CancellationToken cancellationToken)
    {
        List<GitLabCommitStatus> statuses;

        try
        {
            var encodedSha = Uri.EscapeDataString(sha);
            var query = $"projects/{projectId}/repository/commits/{encodedSha}/statuses?per_page=100";
            statuses = await _gitLabApiClient.Execute<List<GitLabCommitStatus>>(
                () => accessDetails.CreateRequest(query),
                cancellationToken);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError(
                "GetJobsFromCommitStatuses failed with status {StatusCode} for project {ProjectId}, SHA {Sha}",
                (int)ex.StatusCode,
                projectId,
                sha);

            return [];
        }

        return statuses
            .Select(s => new BranchBuildJob(
                s.Name.IsEmpty() ? "build" : s.Name,
                s.Status.IsEmpty() ? "unknown" : s.Status,
                s.TargetUrl.IsEmpty() ? null : s.TargetUrl))
            .ToList();
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
            var query = $"projects/{projectId}/pipelines/{pipelineId}/jobs?per_page=100";
            jobs = await _gitLabApiClient.Execute<List<GitLabPipelineJob>>(
                () => accessDetails.CreateRequest(query),
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