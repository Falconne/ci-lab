using System.Text.Json.Serialization;

namespace Mergician.Entities;

/// <summary>
///     Minimal model for deserializing the GET /api/v4/personal_access_tokens/self response.
///     Only includes the fields needed for timezone detection.
/// </summary>
public class GitLabTokenSelfInfo
{
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }
}
