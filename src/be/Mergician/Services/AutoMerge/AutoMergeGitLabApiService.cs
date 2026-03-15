using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.GitLab;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Mergician.Services.AutoMerge;

/// <summary>
///     GitLab API operations related to auto merge functionality:
///     retrieving detailed MR info, rebasing, checking pipelines, and merging.
/// </summary>
public class AutoMergeGitLabApiService
{
    private readonly GitLabApiClient _gitLabApiClient;

    private readonly ILogger<AutoMergeGitLabApiService> _logger;

    public AutoMergeGitLabApiService(
        GitLabApiClient gitLabApiClient,
        ILogger<AutoMergeGitLabApiService> logger)
    {
        _gitLabApiClient = gitLabApiClient;
        _logger = logger;
    }

    /// <summary>
    ///     Fetches detailed merge request information including merge status and conflicts.
    /// </summary>
    public async Task<GitLabDetailedMergeRequest?> GetDetailedMergeRequest(
        AccessDetailsBase accessDetails,
        int projectId,
        int mergeRequestIid)
    {
        try
        {
            return await _gitLabApiClient.ExecuteAsync<GitLabDetailedMergeRequest>(() =>
                accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/merge_requests/{mergeRequestIid}"));
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError(
                "GetDetailedMergeRequest failed with status {StatusCode} for project {ProjectId}, MR {MrIid}",
                (int)ex.StatusCode,
                projectId,
                mergeRequestIid);

            return null;
        }
    }

    /// <summary>
    ///     Fetches the latest pipeline for a merge request, or null if none exists.
    /// </summary>
    public async Task<GitLabPipelineDetail?> GetLatestMergeRequestPipeline(
        AccessDetailsBase accessDetails,
        int projectId,
        int mergeRequestIid)
    {
        try
        {
            var pipelines = await _gitLabApiClient.ExecuteAsync<List<GitLabPipelineDetail>>(() =>
                accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/merge_requests/{mergeRequestIid}/pipelines?per_page=1&sort=desc"));

            return pipelines.FirstOrDefault();
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError(
                "GetLatestMergeRequestPipeline failed with status {StatusCode} for project {ProjectId}, MR {MrIid}",
                (int)ex.StatusCode,
                projectId,
                mergeRequestIid);

            return null;
        }
    }

    /// <summary>
    ///     Triggers a rebase for a merge request.
    ///     Returns true if the rebase was initiated, false on failure.
    /// </summary>
    public async Task<bool> RebaseMergeRequest(
        AccessDetailsBase accessDetails,
        int projectId,
        int mergeRequestIid)
    {
        try
        {
            await _gitLabApiClient.ExecuteAsync<GitLabRebaseResponse>(() =>
            {
                var request = accessDetails.CreateRequest(
                    HttpMethod.Put,
                    $"projects/{projectId}/merge_requests/{mergeRequestIid}/rebase");

                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                return request;
            });

            _logger.LogInformation(
                "Initiated rebase for project {ProjectId}, MR {MrIid}",
                projectId,
                mergeRequestIid);

            return true;
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError(
                "RebaseMergeRequest failed with status {StatusCode} for project {ProjectId}, MR {MrIid}",
                (int)ex.StatusCode,
                projectId,
                mergeRequestIid);

            return false;
        }
    }

    /// <summary>
    ///     Accepts (merges) a merge request.
    ///     Returns the merge response, or null if the merge failed.
    /// </summary>
    public async Task<GitLabMergeResponse?> Merge(
        AccessDetailsBase accessDetails,
        int projectId,
        int mergeRequestIid)
    {
        try
        {
            var result = await _gitLabApiClient.ExecuteAsync<GitLabMergeResponse>(() =>
            {
                var request = accessDetails.CreateRequest(
                    HttpMethod.Put,
                    $"projects/{projectId}/merge_requests/{mergeRequestIid}/merge");

                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                return request;
            });

            _logger.LogInformation(
                "Merged MR for project {ProjectId}, MR {MrIid}, state={State}",
                projectId,
                mergeRequestIid,
                result.State);

            return result;
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            if (ex.StatusCode is
                HttpStatusCode.NotAcceptable
                or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.Conflict)
            {
                _logger.LogWarning(
                    "Merge not possible for project {ProjectId}, MR {MrIid}: status {StatusCode}, body: {Body}",
                    projectId,
                    mergeRequestIid,
                    (int)ex.StatusCode,
                    ex.ResponseBody);

                return null;
            }

            _logger.LogError(
                "Merge failed with status {StatusCode} for project {ProjectId}, MR {MrIid}",
                (int)ex.StatusCode,
                projectId,
                mergeRequestIid);

            return null;
        }
    }

    /// <summary>
    ///     Posts a comment on a merge request.
    /// </summary>
    public async Task PostComment(
        AccessDetailsBase accessDetails,
        int projectId,
        int mergeRequestIid,
        string body)
    {
        try
        {
            var jsonBody = JsonSerializer.Serialize(new { body });

            await _gitLabApiClient.ExecuteAsync<GitLabNote>(() =>
            {
                var request = accessDetails.CreateRequest(
                    HttpMethod.Post,
                    $"projects/{projectId}/merge_requests/{mergeRequestIid}/notes");

                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                return request;
            });

            _logger.LogInformation(
                "Posted comment on project {ProjectId}, MR {MrIid}",
                projectId,
                mergeRequestIid);
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogError(
                "PostComment failed with status {StatusCode} for project {ProjectId}, MR {MrIid}",
                (int)ex.StatusCode,
                projectId,
                mergeRequestIid);
        }
    }
}