using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Services.GitLab;
using Mergician.Utilities;
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
    private static readonly JsonSerializerOptions _jsonOptions = JsonOptions.CaseInsensitive;

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
        var operationName = $"GetDetailedMergeRequest(projectId={projectId}, mrIid={mergeRequestIid})";

        try
        {
            return await _gitLabApiClient.ExecuteAsync<GitLabDetailedMergeRequest>(
                () => accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/merge_requests/{mergeRequestIid}"),
                _jsonOptions,
                operationName,
                GitLabApiFailureBehavior.Throw);
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
        var operationName = $"GetLatestMergeRequestPipeline(projectId={projectId}, mrIid={mergeRequestIid})";

        try
        {
            var pipelines = await _gitLabApiClient.ExecuteAsync<List<GitLabPipelineDetail>>(
                () => accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/merge_requests/{mergeRequestIid}/pipelines?per_page=1&sort=desc"),
                _jsonOptions,
                operationName,
                GitLabApiFailureBehavior.Throw);

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
        var operationName = $"RebaseMergeRequest(projectId={projectId}, mrIid={mergeRequestIid})";

        try
        {
            await _gitLabApiClient.ExecuteAsync<GitLabRebaseResponse>(
                () =>
                {
                    var request = accessDetails.CreateRequest(
                        HttpMethod.Put,
                        $"projects/{projectId}/merge_requests/{mergeRequestIid}/rebase");
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    return request;
                },
                _jsonOptions,
                operationName,
                GitLabApiFailureBehavior.Throw);

            _logger.LogInformation(
                "Initiated rebase for project {ProjectId}, MR {MrIid}",
                projectId,
                mergeRequestIid);

            return true;
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            _logger.LogWarning(
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
    public async Task<GitLabMergeResponse?> AcceptMergeRequest(
        AccessDetailsBase accessDetails,
        int projectId,
        int mergeRequestIid)
    {
        var operationName = $"AcceptMergeRequest(projectId={projectId}, mrIid={mergeRequestIid})";

        try
        {
            var result = await _gitLabApiClient.ExecuteAsync<GitLabMergeResponse>(
                () =>
                {
                    var request = accessDetails.CreateRequest(
                        HttpMethod.Put,
                        $"projects/{projectId}/merge_requests/{mergeRequestIid}/merge");
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    return request;
                },
                _jsonOptions,
                operationName,
                GitLabApiFailureBehavior.Throw);

            _logger.LogInformation(
                "Accepted merge request for project {ProjectId}, MR {MrIid}, state={State}",
                projectId,
                mergeRequestIid,
                result.State);

            return result;
        }
        catch (GitLabUnexpectedResponseException ex)
        {
            if (ex.StatusCode == HttpStatusCode.NotAcceptable
                || ex.StatusCode == HttpStatusCode.MethodNotAllowed
                || ex.StatusCode == HttpStatusCode.Conflict)
            {
                _logger.LogWarning(
                    "AcceptMergeRequest not possible for project {ProjectId}, MR {MrIid}: status {StatusCode}, body: {Body}",
                    projectId,
                    mergeRequestIid,
                    (int)ex.StatusCode,
                    ex.ResponseBody);

                return null;
            }

            _logger.LogError(
                "AcceptMergeRequest failed with status {StatusCode} for project {ProjectId}, MR {MrIid}",
                (int)ex.StatusCode,
                projectId,
                mergeRequestIid);

            return null;
        }
    }
}
