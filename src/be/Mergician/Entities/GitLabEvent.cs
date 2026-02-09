using System.Text.Json.Serialization;

namespace Mergician.Entities;

public class GitLabEvent
{
    public int Id { get; set; }

    [JsonPropertyName("action_name")]
    public string ActionName { get; set; } = "";

    [JsonPropertyName("target_type")]
    public string? TargetType { get; set; }

    [JsonPropertyName("target_title")]
    public string? TargetTitle { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("push_data")]
    public GitLabPushData? PushData { get; set; }

    [JsonPropertyName("project_id")]
    public int ProjectId { get; set; }
}

public class GitLabPushData
{
    [JsonPropertyName("commit_count")]
    public int CommitCount { get; set; }

    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("ref_type")]
    public string? RefType { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }
}
