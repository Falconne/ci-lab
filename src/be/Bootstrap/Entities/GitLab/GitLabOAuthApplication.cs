using System.Text.Json.Serialization;

namespace Bootstrap.Entities.GitLab;

public class GitLabOAuthApplication
{
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("application_id")]
    public string ApplicationId { get; set; } = "";

    public string Secret { get; set; } = "";

    [JsonPropertyName("callback_url")]
    public string CallbackUrl { get; set; } = "";
}
