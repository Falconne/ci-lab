using Mergician.Entities;
using Mergician.Services.Authentication;
using Mergician.Utilities;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Mergician.Services.Gitlab;

public class GitlabService
{
    private static readonly JsonSerializerOptions _jsonOptions = JsonOptions.CaseInsensitive;

    private readonly GitLabApiClient _gitLabApiClient;

    private readonly ILogger<GitlabService> _logger;

    private readonly CacheService<int, GitLabProject> _projectCache;

    private readonly GitLabTimezoneService _timezoneService;

    public GitlabService(
        GitLabApiClient gitLabApiClient,
        CacheService<int, GitLabProject> projectCache,
        GitLabTimezoneService timezoneService,
        ILogger<GitlabService> logger)
    {
        _gitLabApiClient = gitLabApiClient;
        _projectCache = projectCache;
        _timezoneService = timezoneService;
        _logger = logger;
    }

    /// <summary>
    ///     Returns true if the branch name is a common default branch name.
    /// </summary>
    public static bool IsPossibleDefaultBranch(string branchName)
    {
        return branchName is "main" or "master" or "develop";
    }

    /// <summary>
    ///     Returns true if the project or group name indicates it is scheduled for deletion.
    ///     GitLab renames groups and their projects to include "deletion_scheduled" in the
    ///     namespace during its asynchronous deletion process.
    /// </summary>
    public static bool IsScheduledForDeletion(string nameWithNamespace)
    {
        return nameWithNamespace.Contains("deletion_scheduled", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<GitLabUserInfo?> GetCurrentUser(AccessDetailsBase accessDetails)
    {
        return await _gitLabApiClient.ExecuteAsync(
            async (client, cancellationToken) =>
            {
                using var request = accessDetails.CreateRequest(HttpMethod.Get, "user");
                using var response = await client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    if (GitLabApiClient.IsRetriableStatusCode(response.StatusCode))
                    {
                        throw new GitLabUnexpectedResponseException("GetCurrentUser", response.StatusCode);
                    }

                    _logger.LogError("GetCurrentUser failed with status {StatusCode}", (int)response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return GitLabApiClient.DeserializeOrThrow<GitLabUserInfo>(json, _jsonOptions, "GetCurrentUser");
            },
            "GetCurrentUser",
            GitLabApiFailureBehavior.EnterStartupMode);
    }

    /// <summary>
    ///     Streams unique branch push events at or after the given timestamp.
    ///     Uses date-level filtering in the GitLab API call and applies timestamp-level
    ///     filtering in-process to preserve sub-day precision.
    /// </summary>
    public async IAsyncEnumerable<(string BranchName, int ProjectId, DateTimeOffset CreatedAt)>
        GetPushEventsForUserSince(
            AccessDetailsBase accessDetails,
            DateTimeOffset since,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sinceUtc = since.ToUniversalTime();

        // GitLab events API 'after' is date-only and interprets the date in the server's
        // configured timezone. Convert our UTC timestamp to GitLab's local time, then
        // query from the previous day to avoid missing events near the date boundary.
        var sinceInGitLabLocal = _timezoneService.AdjustToGitLabLocal(sinceUtc);
        var afterDate = sinceInGitLabLocal.AddDays(-1).ToString("yyyy-MM-dd");

        _logger.LogDebug(
            "GetPushEventsForUserSince: sinceUtc={SinceUtc}, gitLabLocal={GitLabLocal}, afterDate={AfterDate}, gitLabOffset={Offset}",
            sinceUtc,
            sinceInGitLabLocal,
            afterDate,
            _timezoneService.GitLabUtcOffset);

        var page = 1;
        var yieldedCount = 0;
        var emittedBranchProjects = new HashSet<string>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var operationName = $"GetPushEventsForUserSince(page={page})";
            var pageResult = await _gitLabApiClient.ExecuteAsync(
                async (httpClient, token) =>
                {
                    using var request = accessDetails.CreateRequest(
                        HttpMethod.Get,
                        $"events?after={afterDate}&action=pushed&sort=desc&per_page=100&page={page}");

                    using var response = await httpClient.SendAsync(request, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (GitLabApiClient.IsRetriableStatusCode(response.StatusCode))
                        {
                            throw new GitLabUnexpectedResponseException(operationName, response.StatusCode);
                        }

                        _logger.LogError(
                            "GetPushEventsForUserSince failed on page {Page} with status {StatusCode}",
                            page,
                            (int)response.StatusCode);

                        return (PageEvents: new List<GitLabPushEvent>(), NextPage: (string?)null, Success: false);
                    }

                    var json = await response.Content.ReadAsStringAsync(token);
                    var parsedEvents = GitLabApiClient.DeserializeOrThrow<List<GitLabPushEvent>>(
                        json,
                        _jsonOptions,
                        operationName);
                    var nextPage = response.Headers.TryGetValues("X-Next-Page", out var nextPageValues)
                        ? nextPageValues.FirstOrDefault()
                        : null;

                    return (PageEvents: parsedEvents, NextPage: nextPage, Success: true);
                },
                operationName,
                GitLabApiFailureBehavior.Throw,
                cancellationToken);

            if (!pageResult.Success)
            {
                yield break;
            }

            var pageEvents = pageResult.PageEvents;

            _logger.LogDebug(
                "Fetched {Count} GitLab push events from page {Page} (after={AfterDate})",
                pageEvents.Count,
                page,
                afterDate);

            foreach (var pushEvent in pageEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (pushEvent.CreatedAt < sinceUtc)
                {
                    continue;
                }

                if (pushEvent.PushData is not { RefType: "branch", Ref: not null })
                {
                    continue;
                }

                var key = $"{pushEvent.PushData.Ref}:{pushEvent.ProjectId}";
                if (!emittedBranchProjects.Add(key))
                {
                    continue;
                }

                yieldedCount++;
                yield return (pushEvent.PushData.Ref, pushEvent.ProjectId, pushEvent.CreatedAt);
            }

            var nextPage = pageResult.NextPage;
            if (string.IsNullOrWhiteSpace(nextPage))
            {
                break;
            }

            if (int.TryParse(nextPage, out page) && page > 0)
            {
                continue;
            }

            _logger.LogError(
                "Unexpected X-Next-Page header value '{NextPage}' when fetching GitLab push events",
                nextPage);

            break;
        }

        _logger.LogInformation(
            "Streamed {Count} unique branch push events since {Since}",
            yieldedCount,
            sinceUtc);
    }

    public async Task<GitLabProject?> GetProject(AccessDetailsBase accessDetails, int projectId)
    {
        if (_projectCache.TryGet(projectId, out var cached))
        {
            _logger.LogDebug("Returning cached project info for project {ProjectId}", projectId);
            return cached;
        }

        var operationName = $"GetProject({projectId})";
        var project = await _gitLabApiClient.ExecuteAsync(
            async (client, cancellationToken) =>
            {
                using var request = accessDetails.CreateRequest(HttpMethod.Get, $"projects/{projectId}");
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    if (GitLabApiClient.IsRetriableStatusCode(response.StatusCode))
                    {
                        throw new GitLabUnexpectedResponseException(operationName, response.StatusCode);
                    }

                    _logger.LogError(
                        "GetProject({ProjectId}) failed with status {StatusCode}",
                        projectId,
                        (int)response.StatusCode);

                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return GitLabApiClient.DeserializeOrThrow<GitLabProject>(json, _jsonOptions, operationName);
            },
            operationName,
            GitLabApiFailureBehavior.Throw);

        if (project != null)
        {
            if (IsScheduledForDeletion(project.NameWithNamespace))
            {
                _logger.LogDebug(
                    "Not caching project {ProjectId} ('{NameWithNamespace}'): scheduled for deletion",
                    projectId,
                    project.NameWithNamespace);
            }
            else
            {
                _projectCache.Set(projectId, project);
                _logger.LogDebug("Cached project info for project {ProjectId}", projectId);
            }
        }

        return project;
    }

    /// <summary>
    ///     Fetches full branch details including the latest commit information.
    ///     Returns null when the branch does not exist or the request fails.
    /// </summary>
    public async Task<GitLabBranchDetails?> GetBranchDetails(
        AccessDetailsBase accessDetails,
        int projectId,
        string branchName)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        var operationName = $"GetBranchDetails(projectId={projectId}, branchName={branchName})";

        return await _gitLabApiClient.ExecuteAsync(
            async (client, cancellationToken) =>
            {
                using var request = accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/repository/branches/{encodedBranch}");

                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        _logger.LogDebug(
                            "GetBranchDetails for '{BranchName}' in project {ProjectId} returned {StatusCode}",
                            branchName,
                            projectId,
                            (int)response.StatusCode);

                        return null;
                    }

                    if (GitLabApiClient.IsRetriableStatusCode(response.StatusCode))
                    {
                        throw new GitLabUnexpectedResponseException(operationName, response.StatusCode);
                    }

                    _logger.LogError(
                        "GetBranchDetails for '{BranchName}' in project {ProjectId} returned {StatusCode}",
                        branchName,
                        projectId,
                        (int)response.StatusCode);

                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return GitLabApiClient.DeserializeOrThrow<GitLabBranchDetails>(json, _jsonOptions, operationName);
            },
            operationName,
            GitLabApiFailureBehavior.Throw);
    }

    /// <summary>
    ///     Checks branch lookup status in the given project.
    ///     Returns Missing only for 404 responses; all other failures are Unavailable.
    /// </summary>
    public async Task<GitLabBranchLookupResult> GetBranchLookupResult(
        AccessDetailsBase accessDetails,
        int projectId,
        string branchName)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        var operationName = $"GetBranchLookupResult(projectId={projectId}, branchName={branchName})";

        return await _gitLabApiClient.ExecuteAsync(
            async (client, cancellationToken) =>
            {
                using var request = accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/repository/branches/{encodedBranch}");

                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Branch '{BranchName}' exists in project {ProjectId}", branchName, projectId);
                    return new GitLabBranchLookupResult(
                        GitLabBranchLookupStatus.Exists,
                        (int)response.StatusCode);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogDebug(
                        "Branch '{BranchName}' does not exist in project {ProjectId} (status {StatusCode})",
                        branchName,
                        projectId,
                        (int)response.StatusCode);

                    return new GitLabBranchLookupResult(
                        GitLabBranchLookupStatus.Missing,
                        (int)response.StatusCode);
                }

                if (GitLabApiClient.IsRetriableStatusCode(response.StatusCode))
                {
                    throw new GitLabUnexpectedResponseException(operationName, response.StatusCode);
                }

                _logger.LogError(
                    "Branch lookup unavailable for '{BranchName}' in project {ProjectId} (status {StatusCode})",
                    branchName,
                    projectId,
                    (int)response.StatusCode);

                return new GitLabBranchLookupResult(
                    GitLabBranchLookupStatus.Unavailable,
                    (int)response.StatusCode);
            },
            operationName,
            GitLabApiFailureBehavior.Throw);
    }

    /// <summary>
    ///     Finds open merge requests for a given source branch in a project.
    /// </summary>
    public async Task<List<GitLabMergeRequest>> GetMergeRequests(
        AccessDetailsBase accessDetails,
        int projectId,
        string sourceBranch)
    {
        var encodedBranch = Uri.EscapeDataString(sourceBranch);
        var operationName = $"GetMergeRequests(projectId={projectId}, sourceBranch={sourceBranch})";

        return await _gitLabApiClient.ExecuteAsync(
            async (client, cancellationToken) =>
            {
                using var request = accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/merge_requests?source_branch={encodedBranch}&state=opened");

                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    if (GitLabApiClient.IsRetriableStatusCode(response.StatusCode))
                    {
                        throw new GitLabUnexpectedResponseException(operationName, response.StatusCode);
                    }

                    _logger.LogError(
                        "GetMergeRequests failed with status {StatusCode} for project {ProjectId}, branch '{BranchName}'",
                        (int)response.StatusCode,
                        projectId,
                        sourceBranch);

                    return [];
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return GitLabApiClient.DeserializeOrThrow<List<GitLabMergeRequest>>(
                    json,
                    _jsonOptions,
                    operationName);
            },
            operationName,
            GitLabApiFailureBehavior.Throw);
    }

    /// <summary>
    ///     Gets the approval state for a merge request.
    /// </summary>
    public async Task<GitLabApprovalState?> GetMergeRequestApprovals(
        AccessDetailsBase accessDetails,
        int projectId,
        int mergeRequestIid)
    {
        var operationName = $"GetMergeRequestApprovals(projectId={projectId}, mergeRequestIid={mergeRequestIid})";

        return await _gitLabApiClient.ExecuteAsync(
            async (client, cancellationToken) =>
            {
                using var request = accessDetails.CreateRequest(
                    HttpMethod.Get,
                    $"projects/{projectId}/merge_requests/{mergeRequestIid}/approvals");

                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        _logger.LogInformation(
                            "GetMergeRequestApprovals is not available for project {ProjectId}, MR {MergeRequestIid} (status {StatusCode}); assuming 0 approvals required",
                            projectId,
                            mergeRequestIid,
                            (int)response.StatusCode);

                        return new GitLabApprovalState { ApprovalsRequired = 0, ApprovedBy = [] };
                    }

                    if (GitLabApiClient.IsRetriableStatusCode(response.StatusCode))
                    {
                        throw new GitLabUnexpectedResponseException(operationName, response.StatusCode);
                    }

                    _logger.LogError(
                        "GetMergeRequestApprovals failed with status {StatusCode}",
                        (int)response.StatusCode);

                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return GitLabApiClient.DeserializeOrThrow<GitLabApprovalState>(json, _jsonOptions, operationName);
            },
            operationName,
            GitLabApiFailureBehavior.Throw);
    }
}