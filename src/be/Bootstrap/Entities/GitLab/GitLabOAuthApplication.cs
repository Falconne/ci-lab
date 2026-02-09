using System.Text.Json.Serialization;

namespace Bootstrap.Entities.Gitlab;

public class GitLabOAuthApplication
{
    public int Id { get; set; }

    [JsonPropertyName("application_id")]
    public string ApplicationId { get; set; } = "";

    public string Secret { get; set; } = "";

    [JsonPropertyName("callback_url")]
    public string CallbackUrl { get; set; } = "";
}
