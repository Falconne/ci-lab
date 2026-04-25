using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabMergeRequest
{
    [JsonPropertyName("iid")]
    public int Iid { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("web_url")]
    public string WebUrl { get; set; } = "";

    [JsonPropertyName("project_id")]
    public int ProjectId { get; set; }

    [JsonPropertyName("source_branch")]
    // ReSharper disable once UnusedMember.Global
    public string SourceBranch { get; set; } = "";

    [JsonPropertyName("detailed_merge_status")]
    public string DetailedMergeStatus { get; set; } = "";

    [JsonPropertyName("rebase_in_progress")]
    public bool RebaseInProgress { get; set; }
}