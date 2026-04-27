using System.Text.Json.Serialization;

namespace Bootstrap.Entities.GitLab;

public sealed class GitLabMergeRequest
{
    [JsonPropertyName("iid")]
    public int Iid { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("work_in_progress")]
    public bool WorkInProgress { get; set; }
}