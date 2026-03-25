using Mergician.Entities;
using Mergician.Services.Authentication;
using System.Text.RegularExpressions;

namespace Mergician.Services.GitLab;

/// <summary>
///     Parses GitLab merge request URLs and looks up merge request details via the GitLab API.
///     Used by merge group management features to add branches by MR URL and find merge groups by MR URL.
/// </summary>
public partial class MergeRequestLookupService
{
    private readonly GitLabService _gitLabService;

    private readonly ILogger<MergeRequestLookupService> _logger;

    public MergeRequestLookupService(
        GitLabService gitLabService,
        ILogger<MergeRequestLookupService> logger)
    {
        _gitLabService = gitLabService;
        _logger = logger;
    }

    /// <summary>
    ///     Parses a GitLab merge request URL and returns the project path and MR IID.
    ///     Handles URLs like: https://gitlab.example.com/group/project/-/merge_requests/42
    /// </summary>
    public ParsedMergeRequestUrl? ParseMergeRequestUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("Empty merge request URL provided");
            return null;
        }

        var match = MergeRequestUrlPattern().Match(url.Trim());
        if (!match.Success)
        {
            _logger.LogWarning("Could not parse merge request URL: {Url}", url);
            return null;
        }

        var projectPath = match.Groups["projectPath"].Value;
        var mergeRequestIid = int.Parse(match.Groups["mergeRequestIid"].Value);

        _logger.LogDebug("Parsed MR URL: projectPath={ProjectPath}, mergeRequestIid={MergeRequestIid}", projectPath, mergeRequestIid);
        return new ParsedMergeRequestUrl(projectPath, mergeRequestIid);
    }

    /// <summary>
    ///     Looks up an open merge request by project path and MR IID.
    ///     Returns the project and source branch name, or null if the MR or project is not found.
    /// </summary>
    public async Task<MergeRequestLookupResult?> LookupMergeRequest(
        AccessDetailsBase accessDetails,
        string projectPath,
        int mergeRequestIid,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Looking up merge request: project={ProjectPath}, MR IID={MergeRequestIid}",
            projectPath,
            mergeRequestIid);

        var project = await LookupProjectByPath(accessDetails, projectPath, cancellationToken);
        if (project == null)
        {
            _logger.LogWarning("Project not found for path: {ProjectPath}", projectPath);
            return null;
        }

        var mergeRequests = await _gitLabService.GetMergeRequestsByIid(
            accessDetails,
            project.Id,
            mergeRequestIid,
            cancellationToken);

        if (mergeRequests.Count == 0)
        {
            _logger.LogWarning(
                "Open merge request !{MergeRequestIid} not found in project {ProjectPath} (id={ProjectId})",
                mergeRequestIid,
                projectPath,
                project.Id);

            return null;
        }

        var mr = mergeRequests[0];
        _logger.LogInformation(
            "Found merge request !{MergeRequestIid} in project {ProjectName}: sourceBranch={SourceBranch}",
            mergeRequestIid,
            project.Name,
            mr.SourceBranch);

        return new MergeRequestLookupResult(project, mr.SourceBranch, mr.Title);
    }

    private async Task<GitLabProject?> LookupProjectByPath(
        AccessDetailsBase accessDetails,
        string projectPath,
        CancellationToken cancellationToken)
    {
        return await _gitLabService.GetProjectByPath(accessDetails, projectPath, cancellationToken);
    }

    [GeneratedRegex(@"https?://[^/]+/(?<projectPath>.+?)/-/merge_requests/(?<mergeRequestIid>\d+)")]
    private static partial Regex MergeRequestUrlPattern();
}