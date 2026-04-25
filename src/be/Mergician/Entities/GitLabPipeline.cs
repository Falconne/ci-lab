using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabPipeline
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("sha")]
    public string Sha { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";
}