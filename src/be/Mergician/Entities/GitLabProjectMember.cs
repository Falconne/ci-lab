using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabProjectMember
{
    [JsonPropertyName("access_level")]
    public int AccessLevel { get; set; }
}
