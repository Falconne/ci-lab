using System.Text.Json.Serialization;

namespace Bootstrap.Entities.GitLab;

public sealed class GitLabProject
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("http_url_to_repo")]
    public required string HttpURLToRepo { get; set; }

    [JsonPropertyName("namespace")]
    public GitLabProjectNamespace? Namespace { get; set; }
}

public sealed class GitLabProjectNamespace
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
}