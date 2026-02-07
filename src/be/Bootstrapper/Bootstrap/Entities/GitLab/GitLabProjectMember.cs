using System.Text.Json.Serialization;

namespace Bootstrap.Entities.Gitlab;

public sealed class GitlabProjectMember
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("username")]
    public required string Username { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("access_level")]
    public required int AccessLevel { get; set; }
}
