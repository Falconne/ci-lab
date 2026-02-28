using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabProject
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    [JsonPropertyName("name_with_namespace")]
    public string NameWithNamespace { get; set; } = "";

    [JsonPropertyName("web_url")]
    public string WebUrl { get; set; } = "";
}