using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabPipelineJob
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("stage")]
    public string Stage { get; set; } = "";

    [JsonPropertyName("web_url")]
    public string WebUrl { get; set; } = "";
}
