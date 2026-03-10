using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabMergeRequest
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("iid")]
    public int Iid { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("source_branch")]
    public string SourceBranch { get; set; } = "";

    [JsonPropertyName("target_branch")]
    public string TargetBranch { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("web_url")]
    public string WebUrl { get; set; } = "";
}