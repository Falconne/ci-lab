using System.Text.Json.Serialization;

namespace Bootstrap.Services.GitLab.Entities;

public sealed class GitLabProject
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("http_url_to_repo")]
    public string? HttpUrlToRepo { get; set; }
}
