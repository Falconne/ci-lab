using System.Text.RegularExpressions;

namespace Mergician.Services;

/// <summary>
///     Parses GitLab merge request URLs and looks up merge request details via the GitLab API.
///     Used by merge group management features to add branches by MR URL and find merge groups by MR URL.
/// </summary>
public partial class MergeRequestLookupService
{
    private readonly GitLab.GitLabService _gitLabService;

    private readonly ILogger<MergeRequestLookupService> _logger;

    public MergeRequestLookupService(
        GitLab.GitLabService gitLabService,
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
        var mrIid = int.Parse(match.Groups["mrIid"].Value);

        _logger.LogDebug("Parsed MR URL: projectPath={ProjectPath}, mrIid={MrIid}", projectPath, mrIid);
        return new ParsedMergeRequestUrl(projectPath, mrIid);
    }

    /// <summary>
    ///     Looks up a merge request by project path and MR IID.
    ///     Returns the project and source branch name, or null if the MR or project is not found.
    /// </summary>
    public async Task<MergeRequestLookupResult?> LookupMergeRequest(
        Authentication.AccessDetailsBase accessDetails,
        string projectPath,
        int mrIid,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Looking up merge request: project={ProjectPath}, MR IID={MrIid}",
            projectPath,
            mrIid);

        var project = await LookupProjectByPath(accessDetails, projectPath, cancellationToken);
        if (project == null)
        {
            _logger.LogWarning("Project not found for path: {ProjectPath}", projectPath);
            return null;
        }

        var mergeRequests = await _gitLabService.GetMergeRequestsByIid(
            accessDetails, project.Id, mrIid, cancellationToken);

        if (mergeRequests.Count == 0)
        {
            _logger.LogWarning(
                "Merge request !{MrIid} not found in project {ProjectPath} (id={ProjectId})",
                mrIid, projectPath, project.Id);
            return null;
        }

        var mr = mergeRequests[0];
        _logger.LogInformation(
            "Found merge request !{MrIid} in project {ProjectName}: sourceBranch={SourceBranch}",
            mrIid, project.Name, mr.SourceBranch);

        return new MergeRequestLookupResult(project, mr.SourceBranch, mr.Title);
    }

    private async Task<Entities.GitLabProject?> LookupProjectByPath(
        Authentication.AccessDetailsBase accessDetails,
        string projectPath,
        CancellationToken cancellationToken)
    {
        return await _gitLabService.GetProjectByPath(accessDetails, projectPath, cancellationToken);
    }

    [GeneratedRegex(@"https?://[^/]+/(?<projectPath>.+?)/-/merge_requests/(?<mrIid>\d+)")]
    private static partial Regex MergeRequestUrlPattern();
}

public record ParsedMergeRequestUrl(string ProjectPath, int MrIid);

public record MergeRequestLookupResult(Entities.GitLabProject Project, string SourceBranch, string MergeRequestTitle);
