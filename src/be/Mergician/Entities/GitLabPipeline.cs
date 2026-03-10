using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabPipeline
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
}