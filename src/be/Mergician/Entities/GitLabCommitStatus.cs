using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabCommitStatus
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("target_url")]
    public string? TargetUrl { get; set; }
}