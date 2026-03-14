using System.Text.Json.Serialization;

namespace Bootstrap.Entities.GitLab;

public sealed class GitLabMergeRequest
{
    [JsonPropertyName("iid")]
    public int Iid { get; set; }
}