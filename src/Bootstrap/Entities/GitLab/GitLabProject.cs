using System.Text.Json.Serialization;

namespace Bootstrap.Entities.Gitlab;

public sealed class GitlabProject
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("http_url_to_repo")]
    public required string HttpUrlToRepo { get; set; }
}