using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabPushEvent
{
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("project_id")]
    public int ProjectId { get; set; }

    [JsonPropertyName("push_data")]
    public GitLabPushEventData? PushData { get; set; }
}

public class GitLabPushEventData
{
    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("ref_type")]
    public string? RefType { get; set; }
}
