using System.Text.Json.Serialization;

namespace Bootstrap.Entities.Gitlab;

public sealed class GitlabBranch
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}
