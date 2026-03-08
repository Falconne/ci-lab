using System.Text.Json.Serialization;

namespace Bootstrap.Entities.GitLab;

public sealed class GitLabBranch
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}
