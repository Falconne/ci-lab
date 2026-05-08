using System.Text.Json.Serialization;

namespace Mergician.Entities;

/// <summary>
///     A merge request that is blocking another MR from being merged.
///     Returned by the GitLab <c>blocking_merge_requests</c> endpoint
///     (available on GitLab Premium and above).
/// </summary>
public class GitLabBlockingMergeRequest
{
    [JsonPropertyName("iid")]
    public int Iid { get; set; }

    [JsonPropertyName("project_id")]
    public int ProjectId { get; set; }

    [JsonPropertyName("source_branch")]
    public string SourceBranch { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("web_url")]
    public string WebUrl { get; set; } = "";
}
