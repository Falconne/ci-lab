using System.Text.Json.Serialization;

namespace Bootstrap.Entities.Gitlab;

public sealed class GitlabMergeRequest
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
}
